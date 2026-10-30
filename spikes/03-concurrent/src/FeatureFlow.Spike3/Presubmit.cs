using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike3;

/// <summary>
/// N7 <c>ff-presubmit</c> (docs/17 5.2, R17): resolve a ticket plan's named step
/// and its inputs through the same code path as the gated step, from a ticket's
/// worktree. Every invocation is appended to the run's records, which is the
/// evidence (not whatever a session reports back).
/// </summary>
public static class Presubmit
{
    public static async Task<int> RunAsync(string[] args)
    {
        var worktree = Path.GetFullPath(Cli.Arg(args, "--worktree") ?? ".");
        var binding = JsonDocument.Parse(File.ReadAllText(Path.Combine(worktree, ".git", "ff-run.json"))).RootElement;
        var planPath = Cli.Arg(args, "--plan") ?? binding.GetProperty("plan").GetString()!;
        var stepId = Cli.Arg(args, "--step") ?? "review";
        var ticket = binding.GetProperty("ticket").GetString()!;
        var records = binding.GetProperty("records").GetString()!;
        var runId = binding.GetProperty("runId").GetString()!;

        WorkerCommand.ConfigureSpike3();
        var plan = PlanYaml.Load(planPath, new Dictionary<string, string> { ["ticket_id"] = ticket });
        var step = plan.ById(stepId) ?? throw new ArgumentException($"no step {stepId}");
        var context = new RunContext(runId, plan, worktree, binding.GetProperty("branch").GetString()!,
            binding.GetProperty("base").GetString()!, ticket, records,
            OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml")));
        var activities = new RealActivities(context);

        object result;
        if (Cli.Flag(args, "--readiness"))
        {
            result = new
            {
                at = DateTimeOffset.UtcNow, kind = "readiness",
                readiness = Readiness.Describe(Findings.Open(context), Findings.DeclinedIds(context)),
            };
        }
        else
        {
            var inputs = await activities.BuildReviewInputsAsync(step, Cli.Arg(args, "--base-override"), Cli.Flag(args, "--omit-prior"));
            var hash = activities.RenderReviewHash(inputs);
            object? findings = null;
            if (Cli.Flag(args, "--call-model"))
            {
                var r = await context.Reviewer.ReviewAsync(inputs.DiffHash, inputs.DiffText, inputs.Prior, inputs.Declined,
                    stepId, CancellationToken.None, inputs.Specification);
                findings = r.IsOk ? r.Value!.Findings : $"{r.Error!.Class}/{r.Error.Reason}";
            }
            result = new
            {
                at = DateTimeOffset.UtcNow, kind = "review", step = stepId, baseRef = inputs.BaseRef, inputs.BaseSha,
                head = inputs.HeadSha, inputs.DiffHash, prior = inputs.Prior.Select(f => f.Id).ToList(),
                inputHash = hash, baseOverride = Cli.Arg(args, "--base-override"), omitPrior = Cli.Flag(args, "--omit-prior"),
                findings,
            };
        }

        var line = JsonSerializer.Serialize(result);
        File.AppendAllText(Path.Combine(records, $"{runId}.presubmit.jsonl"), line + Environment.NewLine);
        Console.WriteLine(line);
        return 0;
    }
}

/// <summary>
/// C5 (docs/17 5.2) at a fixed head and ledger: B's in-run reviewed head and
/// base, as branches; one ticket worker; the gated <c>review</c> and
/// <c>evaluate_readiness</c> run by the engine (entry moved to the step, every
/// route ending the run); ff-presubmit run from a Claude Code session in the
/// worktree.
/// </summary>
public static class C5Run
{
    public static async Task<int> RunAsync(ITemporalClient client, string[] args)
    {
        var suffix = Cli.Arg(args, "--suffix") ?? "spike-3";
        var dir = Path.Combine(Cli.WorkDir, suffix);
        var records = Path.Combine(dir, "c5-records");
        Directory.CreateDirectory(records);
        var runId = $"{suffix}-c5";

        var reviews = File.ReadAllLines(Path.Combine(dir, "records", $"{suffix}-b.reviews.jsonl"))
            .Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var last = reviews.Last();
        var head = last.GetProperty("head").GetString()!;
        var baseSha = last.GetProperty("baseSha").GetString()!;
        var branch = $"ff/ticket/{suffix}-c5";
        var baseBranch = $"ff/epic/{suffix}-c5base";

        var ops = new GitHubOps(Path.Combine(dir, "c5-harness"), $"{runId}-harness");
        var source = Path.Combine(dir, "ticket-b");
        Cli.Git(source, "fetch", "--quiet", "origin");
        await ops.PushAsync(source, $"{head}:refs/heads/{branch}");
        await ops.PushAsync(source, $"{baseSha}:refs/heads/{baseBranch}");
        var clone = GitHubOps.Clone(Path.Combine(dir, "c5"));
        Cli.Git(clone, "checkout", "--quiet", "-B", branch, $"origin/{branch}");
        Cli.Log($"C5 {branch} at {head} (B's last in-run review head), {baseBranch} at {baseSha}");

        var queue = $"spike-3-c5-{suffix}";
        var worker = new WorkerProc("c5", new[]
        {
            "--kind", "ticket", "--queue", queue, "--repo", clone, "--branch", branch, "--base", baseBranch,
            "--ticket", "EPIC3-B", "--run-id", runId, "--records", records,
        }, Path.Combine(dir, "logs", "worker-c5.log"));
        worker.Start();
        await Task.Delay(TimeSpan.FromSeconds(8));

        var full = PlanYaml.Load(Path.Combine(Cli.PlansDir, "ticket-delivery.yaml"));
        async Task Gated(string stepId, string label)
        {
            var plan = full.Clone();
            plan.Entry = stepId;
            var s = plan.ById(stepId)!;
            s.Routes = s.Routes.Keys.ToDictionary(k => k, _ => RouteTarget.ToEnd("c5-stop"), StringComparer.Ordinal);
            var h = await client.StartWorkflowAsync((InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
                new WorkflowOptions($"{runId}-{label}", queue));
            Cli.Log($"C5 gated {stepId}: {await h.GetResultAsync()}");
        }

        var wrapper = Path.Combine(Cli.SpikeDir, "ff-presubmit");
        string Session(string label, string commands)
        {
            var prompt = $"You are in a ticket worktree. Run each of these commands exactly, one at a time, and reply " +
                         $"with their output verbatim:\n{commands}";
            var psi = new System.Diagnostics.ProcessStartInfo("claude")
            {
                WorkingDirectory = clone, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "-p", "--model", "haiku", "--output-format", "json", "--permission-prompts", "none",
                         "--allowed-tools", $"Bash({wrapper}:*)", "--setting-sources", "project,local", "--strict-mcp-config", "-" })
                psi.ArgumentList.Add(a);
            psi.Environment.Remove("ANTHROPIC_API_KEY");
            psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardInput.Write(prompt);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            var sessionId = "";
            try { sessionId = JsonDocument.Parse(output).RootElement.GetProperty("session_id").GetString() ?? ""; }
            catch (Exception) { }
            Cli.Log($"C5 session {label}: {sessionId} exit {p.ExitCode}");
            return sessionId;
        }

        // 1. Gated review #1 on an empty ledger, so the ledger holds findings.
        await Gated("review", "review-1");
        var context = new RunContext(runId, full, clone, branch, baseBranch, "EPIC3-B", records,
            OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml")));
        var seeded = false;
        if (Findings.Open(context).Count == 0)
        {
            seeded = true;
            Findings.Seed(context, new Finding("f-c5-seed", "seeded/for-c5", "src/FeatureFlow.Example.Api/WidgetStore.cs",
                "FindByName", "seeded", false, Message: "Seeded so prior findings are non-empty for the C5 comparison."));
            Cli.Log("C5 review #1 raised nothing; one finding seeded so prior findings are non-empty");
        }

        // 2. ff-presubmit from a Claude Code session, then the sanity variants.
        var s1 = Session("review", $"{wrapper} --worktree . --step review\n" +
                                  $"{wrapper} --worktree . --step review --base-override main\n" +
                                  $"{wrapper} --worktree . --step review --omit-prior");
        // 3. Gated review #2 at the same head and ledger.
        await Gated("review", "review-2");
        // 4. ff-presubmit readiness, then 5. the gated readiness, at the same ledger.
        var s2 = Session("readiness", $"{wrapper} --worktree . --step evaluate_readiness --readiness");
        await Gated("evaluate_readiness", "readiness");
        worker.Kill("C5 over");

        var pre = File.ReadAllLines(Path.Combine(records, $"{runId}.presubmit.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var gatedReviews = File.ReadAllLines(Path.Combine(records, $"{runId}.reviews.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var gatedReadiness = File.ReadAllLines(Path.Combine(records, $"{runId}.readiness.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

        string? Hash(JsonElement e) => e.TryGetProperty("inputHash", out var h) ? h.GetString() : null;
        var preReview = pre.FirstOrDefault(e => e.GetProperty("kind").GetString() == "review" &&
                                                e.GetProperty("baseOverride").ValueKind == JsonValueKind.Null &&
                                                !e.GetProperty("omitPrior").GetBoolean());
        var preMain = pre.FirstOrDefault(e => e.GetProperty("kind").GetString() == "review" &&
                                              e.GetProperty("baseOverride").ValueKind == JsonValueKind.String);
        var preNoPrior = pre.FirstOrDefault(e => e.GetProperty("kind").GetString() == "review" &&
                                                 e.GetProperty("omitPrior").GetBoolean());
        var preReady = pre.FirstOrDefault(e => e.GetProperty("kind").GetString() == "readiness");
        var gated2 = gatedReviews.Count >= 2 ? gatedReviews[1] : default;

        var summary = new
        {
            head, baseSha, seeded, sessions = new[] { s1, s2 },
            presubmitHash = preReview.ValueKind == JsonValueKind.Undefined ? null : Hash(preReview),
            gatedHash = gated2.ValueKind == JsonValueKind.Undefined ? null : Hash(gated2),
            gatedHead = gated2.ValueKind == JsonValueKind.Undefined ? null : gated2.GetProperty("head").GetString(),
            presubmitPrior = preReview.ValueKind == JsonValueKind.Undefined ? null : preReview.GetProperty("prior").ToString(),
            gatedPrior = gated2.ValueKind == JsonValueKind.Undefined ? null : gated2.GetProperty("prior").ToString(),
            sanityBaseMainHash = preMain.ValueKind == JsonValueKind.Undefined ? null : Hash(preMain),
            sanityNoPriorHash = preNoPrior.ValueKind == JsonValueKind.Undefined ? null : Hash(preNoPrior),
            presubmitReadiness = preReady.ValueKind == JsonValueKind.Undefined ? null : preReady.GetProperty("readiness").GetString(),
            gatedReadiness = gatedReadiness.LastOrDefault().ValueKind == JsonValueKind.Undefined ? null : gatedReadiness.Last().GetProperty("readiness").GetString(),
        };
        var hashEqual = summary.presubmitHash is not null && summary.presubmitHash == summary.gatedHash;
        var readinessEqual = summary.presubmitReadiness is not null && summary.presubmitReadiness == summary.gatedReadiness;
        var sanity = summary.sanityBaseMainHash != summary.gatedHash && summary.sanityNoPriorHash != summary.gatedHash &&
                     summary.sanityBaseMainHash is not null && summary.sanityNoPriorHash is not null;
        Cli.Log($"C5 hash equal={hashEqual} readiness byte-identical={readinessEqual} sanity (main / no prior differ)={sanity}");
        var outDir = Path.Combine(Cli.ResultsDir, suffix);
        File.WriteAllText(Path.Combine(outDir, "c5.json"), JsonSerializer.Serialize(new { summary, hashEqual, readinessEqual, sanity },
            new JsonSerializerOptions { WriteIndented = true }));
        foreach (var f in Directory.GetFiles(records, $"{runId}.*.jsonl"))
            File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), overwrite: true);
        return hashEqual && readinessEqual && sanity ? 0 : 1;
    }
}
