using System.Text;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// The testing and analysis actions (docs/04 section 9) and <c>repo.inspect</c> (section 12). The repository's
/// manifest is read at the head being tested, never copied into the plan (RW11). Tiers run where the manifest says: in
/// a sandbox, or delegated to CI inside the same step. Results are parsed by <see cref="ResultFiles"/> in either case.
/// </summary>
static class Testing
{
    /// <summary>
    /// <c>test.run</c> (section 9.1): the tiers <c>tier_tags</c> selects (all, when empty). A baseline that is not clean
    /// is <c>baseline_failed</c>. A head already tested with the same tiers adopts that result and runs nothing.
    /// </summary>
    public static async Task<ActionResult> TestRunAsync(ActionRun run)
    {
        var baseline = run.Bool("baseline", false);
        var repo = run.Repository();
        var (evidence, adopted) = await RunTiersAsync(run, repo, run.Strings("tier_tags"), baseline);
        var outcome = evidence.Passed ? "passed" : baseline ? "baseline_failed" : "failed";
        return run.Report(outcome, evidence: adopted ?? await run.PutJsonAsync(evidence));
    }

    /// <summary>
    /// <c>test.integration</c> (section 9.2) as <c>test.run</c> across repositories: the tiers tagged <c>tier_tags</c>
    /// (default <c>integration</c>) of each repository in <c>repositories</c> (default the run's). Starting the services
    /// from <c>integration_environment</c> is not built yet: a tier that needs them delegates to CI (LOG 120).
    /// </summary>
    public static async Task<ActionResult> IntegrationAsync(ActionRun run)
    {
        var names = run.Strings("repositories") is { Count: > 0 } given ? given : run.Scope.Repositories;
        var tags = run.Strings("tier_tags") is { Count: > 0 } t ? t : ["integration"];
        var all = new List<TestEvidence>();
        foreach (var name in names)
            all.Add((await RunTiersAsync(run, run.Repository(name), tags, baseline: false, adopt: false)).Evidence);
        var evidence = all.Count == 1
            ? all[0]
            : new TestEvidence(string.Join(", ", all.Select(e => e.Repository)), string.Join(", ", all.Select(e => e.Revision)), false, all.All(e => e.Passed),
                all.SelectMany(e => e.Tiers.Select(r => r with { Tier = $"{e.Repository}: {r.Tier}" })).ToList());
        return run.Report(evidence.Passed ? "passed" : "failed", evidence: await run.PutJsonAsync(evidence));
    }

    static async Task<(TestEvidence Evidence, string? Adopted)> RunTiersAsync(ActionRun run, RepoRef repo, IReadOnlyList<string> tags, bool baseline, bool adopt = true)
    {
        var head = await run.WorkHeadAsync(repo);
        var manifest = await run.ManifestAsync(repo, head)
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no {RepositoryManifest.FileName} at {EngineActions.Short(head)}: the engine cannot tell how to test it");
        var d = new DiagnosticBag();
        StepStartChecks.TierTags(repo.FullName, manifest, tags, d);
        if (d.HasErrors)
            throw ActionStop.CannotAnswer($"check 33 ({Checks.Title(Checks.TierTags)}): {string.Join("; ", d.Select(x => x.Message))}");
        var tiers = manifest.TiersTagged(tags);
        if (tiers.Count == 0)
            throw ActionStop.CannotAnswer($"{repo.FullName}'s {RepositoryManifest.FileName} declares no test tier to run");

        // The carried result (section 9.1): this head was already tested with the same tiers; adopt it.
        if (adopt && !baseline)
            foreach (var (entry, earlier) in (await run.EvidenceAsync<TestEvidence>("test_results", "test.run")).AsEnumerable().Reverse())
                if (earlier.Repository == repo.FullName && earlier.Revision == head && !earlier.Baseline
                    && earlier.Tiers.Select(x => x.Tier).Order().SequenceEqual(tiers.Select(x => x.Name).Order()))
                {
                    run.Events.Add($"{EngineActions.Short(head)} was already tested by {entry.StepId}; its result is adopted and nothing runs");
                    return (earlier, entry.Ref);
                }

        var sandboxes = run.Optional<ISandboxProvider>("sandbox");
        var ci = run.Optional<ICiProvider>("ci");
        var inSandbox = new List<TestTier>();
        var inCi = new List<TestTier>();
        foreach (var tier in tiers)
            if (tier.Targets.Contains("sandbox") && sandboxes is not null)
                inSandbox.Add(tier);
            else if (tier.Targets.Contains("ci") && tier.Ci is not null && ci is not null)
                inCi.Add(tier);
            else
                throw ActionStop.CannotAnswer($"tier {tier.Name} runs in {string.Join(", ", tier.Targets)}, and no provider configured here can run it there");
        if (inSandbox.FirstOrDefault(t => t.PrerequisiteEnvironment.Count > 0) is { } needy)
            throw ActionStop.CannotAnswer($"tier {needy.Name} needs {string.Join(", ", needy.PrerequisiteEnvironment)}, which a sandbox does not provide; run it in CI");

        var results = new List<TestResults>();
        if (inSandbox.Count > 0)
        {
            await using var session = await SandboxSession.OpenAsync(run, repo, head, manifest, "test");
            foreach (var tier in inSandbox)
                results.Add(await SandboxTierAsync(run, session, tier, head));
        }

        foreach (var tier in inCi)
            results.Add(await CiTierAsync(run, ci!, repo, tier, head));

        foreach (var r in results)
            run.Events.Add($"tier {r.Tier} at {EngineActions.Short(head)}: {r.Passed} passed, {r.Failed} failed, {r.Skipped} skipped" +
                           (r.Failures.Count == 0 ? "" : $" ({string.Join("; ", r.Failures.Take(5).Select(f => f.Name))})"));
        return (new TestEvidence(repo.FullName, head, baseline, results.All(r => r.Clean), results), null);
    }

    static async Task<TestResults> SandboxTierAsync(ActionRun run, SandboxSession session, TestTier tier, string head)
    {
        var (exit, output) = await session.RunAsync(tier.Command, run.Options.CommandTimeout, $"tier {tier.Name}");
        if (tier.Result is null)
            // Without a result file the tier is judged by its exit code, and its tests cannot be claimed as evidence (docs/07 section 4).
            return ByExit(head, tier.Name, exit, output);
        var files = await session.ReadFilesAsync(tier.Result.Path);
        if (files.Count == 0)
            return exit != 0
                ? ByExit(head, tier.Name, exit, output)
                : throw ActionStop.CannotAnswer($"tier {tier.Name} exited 0 and wrote no {tier.Result.Format} results at {tier.Result.Path}");
        return WithExit(Parse(tier.Result.Format, files, head, tier.Name), exit, output);
    }

    static async Task<TestResults> CiTierAsync(ActionRun run, ICiProvider ci, RepoRef repo, TestTier tier, string head)
    {
        var started = ActionRun.Must(await ci.TriggerAsync(repo, new CiJobRef(tier.Ci!.Job, new Dictionary<string, string>()), head, $"{run.Key}/{tier.Name}", run.Ct),
            $"start CI for tier {tier.Name}");
        run.Events.Add($"tier {tier.Name} delegated to CI: {started.Url ?? started.Id}");
        while (true)
        {
            run.Heartbeat();
            var status = ActionRun.Must(await ci.GetRunStatusAsync(started, run.Ct), "read the CI run");
            if (status.State == CheckStates.Completed)
                break;
            await Task.Delay(run.Options.CiPollInterval, run.Ct);
        }

        var result = ActionRun.Must(await ci.ReadResultsAsync(started, run.Ct), "read the CI run's results");
        var files = result.Files.Where(f => f.Format == (tier.Result?.Format ?? f.Format) && f.Format is ResultFormats.Trx or ResultFormats.Junit).ToList();
        var exit = result.Conclusion == Conclusions.Success ? 0 : 1;
        if (files.Count == 0)
            return ByExit(head, tier.Name, exit, $"the CI run concluded {result.Conclusion} with no test result files");
        var texts = new List<string>();
        foreach (var file in files)
            texts.Add(await run.ReadTextAsync(file.Content.Id));
        return WithExit(Parse(files[0].Format, texts, head, tier.Name), exit, $"the CI run concluded {result.Conclusion}");
    }

    static TestResults Parse(string format, IReadOnlyList<string> texts, string head, string tier)
    {
        try
        {
            return ResultFiles.Tests(format, texts, head, tier);
        }
        catch (FormatException e)
        {
            throw ActionStop.CannotAnswer($"tier {tier}'s {format} results do not parse: {e.Message}");
        }
    }

    static TestResults ByExit(string head, string tier, int exit, string output) =>
        exit == 0 ? new TestResults(head, tier, 1, 0, 0, []) : new TestResults(head, tier, 0, 1, 0, [new TestFailure(tier, $"exited {exit}: {Tail(output)}")]);

    /// <summary>A command that failed with no failed test (a build error, a crash) still fails the tier.</summary>
    static TestResults WithExit(TestResults results, int exit, string output) =>
        exit != 0 && results.Failed == 0
            ? results with { Failed = 1, Failures = [new TestFailure(results.Tier, $"exited {exit} with no failed test: {Tail(output)}")] }
            : results;

    /// <summary>
    /// <c>analysis.run</c> (section 9.3): runs the manifest's SARIF analyser in a sandbox and compares its issues at or
    /// above <c>min_level</c> with the baseline's by fingerprint. <c>fail_on</c>: <c>new_issues</c> (default),
    /// <c>any_issues</c> or <c>never</c>. A baseline run records and passes. No analyser declared: <c>passed</c>, with a note.
    /// </summary>
    public static async Task<ActionResult> AnalysisAsync(ActionRun run)
    {
        var repo = run.Repository();
        var baseline = run.Bool("baseline", false);
        var head = await run.WorkHeadAsync(repo);
        var manifest = await run.ManifestAsync(repo, head)
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no {RepositoryManifest.FileName} at {EngineActions.Short(head)}: the engine cannot tell how to analyse it");
        var analysis = manifest.Analysis;
        if (analysis is null || analysis.Analyser == "none")
        {
            run.Events.Add($"{repo.FullName} declares no static analyser; nothing to run");
            return run.Report("passed", evidence: await run.PutJsonAsync(new AnalysisEvidence(repo.FullName, head, "none", baseline, true, [], [])));
        }

        if (analysis.Analyser != "sarif")
            throw ActionStop.CannotAnswer($"the {analysis.Analyser} analyser is not built yet; only sarif runs in this version");

        string? sarif;
        int exit;
        string output;
        await using (var session = await SandboxSession.OpenAsync(run, repo, head, manifest, "analysis"))
        {
            (exit, output) = await session.RunAsync(analysis.Command!, run.Options.CommandTimeout, "the analyser");
            sarif = (await session.ReadFilesAsync(analysis.SarifPath!)).FirstOrDefault();
        }

        if (sarif is null)
            throw exit == 0
                ? ActionStop.CannotAnswer($"the analyser exited 0 and wrote no SARIF at {analysis.SarifPath}")
                : ActionStop.Failed($"the analyser exited {exit} and wrote no SARIF at {analysis.SarifPath}: {Tail(output)}");
        AnalysisResults results;
        try
        {
            results = ResultFiles.Sarif(sarif, head, analysis.Analyser);
        }
        catch (FormatException e)
        {
            throw ActionStop.CannotAnswer($"the analyser's SARIF does not parse: {e.Message}");
        }

        if (baseline)
        {
            run.Events.Add($"baseline analysis at {EngineActions.Short(head)}: {results.Issues.Count} issues");
            return run.Report("passed", evidence: await run.PutJsonAsync(new AnalysisEvidence(repo.FullName, head, analysis.Analyser, true, true, results.Issues, [])));
        }

        var minimum = run.String("min_level") ?? IssueLevels.Warning;
        var counted = results.Issues.Where(i => ResultFiles.AtLeast(i.Level, minimum)).ToList();
        var earlier = (await run.EvidenceAsync<AnalysisEvidence>("analysis_results", "analysis.run")).LastOrDefault(e => e.Value.Baseline && e.Value.Repository == repo.FullName);
        if (earlier.Value is null)
            run.Events.Add("no baseline analysis was recorded; every issue counts as new");
        var known = earlier.Value?.Issues.Select(i => i.Fingerprint).ToHashSet(StringComparer.Ordinal) ?? [];
        var fresh = counted.Where(i => !known.Contains(i.Fingerprint)).ToList();
        var failed = (run.String("fail_on") ?? "new_issues") switch
        {
            "any_issues" => counted.Count > 0,
            "never" => false,
            _ => fresh.Count > 0,
        };
        run.Events.Add($"analysis at {EngineActions.Short(head)}: {counted.Count} issues at {minimum} or above, {fresh.Count} new" +
                       (fresh.Count == 0 ? "" : $" ({string.Join("; ", fresh.Take(5).Select(i => $"{i.RuleId} {i.Path}:{i.Line}"))})"));
        var evidence = new AnalysisEvidence(repo.FullName, head, analysis.Analyser, false, !failed, results.Issues, fresh.Select(i => i.Fingerprint).ToList());
        return run.Report(failed ? "failed" : "passed", evidence: await run.PutJsonAsync(evidence));
    }

    /// <summary>
    /// <c>repo.inspect</c> (section 12): the manifest's facts as outputs, read at the work branch, or at the parent branch
    /// before the work branch exists: <c>tiers</c>, <c>tags</c>, <c>ci</c>, <c>analyser</c>, <c>integration_environment</c>, <c>manifest_hash</c>.
    /// </summary>
    public static async Task<ActionResult> InspectAsync(ActionRun run)
    {
        var repo = run.Repository();
        var revision = ActionRun.Must(await run.Vcs.ResolveAsync(repo, run.WorkReference, run.Ct), "read the work branch")
                       ?? "refs/heads/" + await run.ParentBranchAsync(repo);
        var manifest = await run.ManifestAsync(repo, revision)
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no {RepositoryManifest.FileName} at {revision}");
        return run.Report("inspected", new Dictionary<string, object?>
        {
            ["tiers"] = manifest.Tiers.Select(t => (object?)t.Name).ToList(),
            ["tags"] = manifest.Tiers.SelectMany(t => t.Tags).Distinct().Select(t => (object?)t).ToList(),
            ["ci"] = manifest.Tiers.Any(t => t.Targets.Contains("ci")),
            ["analyser"] = manifest.Analysis?.Analyser ?? "none",
            ["integration_environment"] = manifest.Integration is not null,
            ["manifest_hash"] = manifest.Hash,
        });
    }

    internal static string Tail(string output)
    {
        var text = output.Trim();
        return text.Length > 600 ? "…" + text[^600..] : text;
    }
}

/// <summary>
/// A sandbox for the engine's own commands (tests, analysis): created fresh for the step, with the work branch checked
/// out at the head under test and the manifest's <c>setup</c> commands run. Removed when disposed.
/// </summary>
sealed class SandboxSession : IAsyncDisposable
{
    public const string Checkout = "/ff/work";

    readonly ActionRun run;
    readonly ISandboxProvider sandboxes;
    readonly SandboxHandle handle;

    SandboxSession(ActionRun run, ISandboxProvider sandboxes, SandboxHandle handle) => (this.run, this.sandboxes, this.handle) = (run, sandboxes, handle);

    public static async Task<SandboxSession> OpenAsync(ActionRun run, RepoRef repo, string head, RepositoryManifest manifest, string purpose)
    {
        var sandboxes = run.Provider<ISandboxProvider>("sandbox");
        var key = $"{run.Key}/{purpose}";
        // An earlier attempt's sandbox may be half-used: start clean.
        if (await sandboxes.AttachAsync(key, run.Ct) is { Ok: true } earlier)
            await sandboxes.DisposeAsync(earlier.Value!, CancellationToken.None);
        var handle = ActionRun.Must(await sandboxes.CreateAsync(new SandboxSpec(key, "/ff/home", new Dictionary<string, string>(), [Checkout, "/ff/home", "/ff/tmp"]), run.Ct),
            "create the sandbox");
        var session = new SandboxSession(run, sandboxes, handle);
        try
        {
            ActionRun.Must(await run.Vcs.CheckOutAsync(repo, run.WorkReference, head, run.Scope.WorkBranch, new WorkingCopyRef(handle, Checkout), run.Ct),
                "check out the work branch");
            foreach (var command in manifest.Setup)
            {
                var (exit, output) = await session.RunAsync(command, run.Options.CommandTimeout, "setup");
                if (exit != 0)
                    throw ActionStop.Failed($"the manifest's setup command '{command}' exited {exit}: {Testing.Tail(output)}");
            }

            if (manifest.Setup.Count > 0)
                run.Events.Add($"ran {manifest.Setup.Count} setup command(s)");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a shell command in the checkout as the agent's user, heartbeating while it runs.</summary>
    public async Task<(int Exit, string Output)> RunAsync(string command, TimeSpan timeout, string what)
    {
        var started = ActionRun.Must(await sandboxes.StartCommandAsync(handle, new SandboxCommand(["sh", "-c", command], null, Checkout),
            $"{run.Key}/{Guid.NewGuid():N}", run.Ct), $"start {what}");
        var output = new StringBuilder();
        long cursor = 0;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            run.Heartbeat();
            var status = ActionRun.Must(await sandboxes.GetCommandStatusAsync(started, cursor, run.Ct), $"read {what}");
            output.Append(status.Output);
            cursor = status.Cursor;
            if (status.Lost)
                throw new ActionInfrastructureException($"{what} was lost with its sandbox");
            if (!status.Running)
                return (status.ExitCode ?? -1, output.ToString());
            if (DateTimeOffset.UtcNow > deadline)
            {
                await sandboxes.CancelCommandAsync(started, CancellationToken.None);
                throw ActionStop.Failed($"{what} did not finish within {timeout}: {Testing.Tail(output.ToString())}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), run.Ct);
        }
    }

    /// <summary>The text of each file a path names, relative to the checkout; a <c>*</c> is expanded by the sandbox's shell.</summary>
    public async Task<IReadOnlyList<string>> ReadFilesAsync(string path)
    {
        IEnumerable<string> paths = [path];
        if (path.Contains('*'))
        {
            var (_, listed) = await RunAsync($"for f in {path}; do [ -f \"$f\" ] && printf '%s\\n' \"$f\"; done; true", TimeSpan.FromMinutes(1), "list result files");
            paths = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var texts = new List<string>();
        foreach (var p in paths)
        {
            var copied = await sandboxes.CopyOutAsync(handle, p.StartsWith('/') ? p : $"{Checkout}/{p}", run.Ct);
            if (copied.Ok)
                texts.Add(await run.ReadTextAsync(copied.Value!.Id));
            else if (copied.Error!.Class == ProviderErrorClass.Infrastructure)
                ActionRun.Must(copied, $"copy out {p}");
        }

        return texts;
    }

    public async ValueTask DisposeAsync() => await sandboxes.DisposeAsync(handle, CancellationToken.None);
}
