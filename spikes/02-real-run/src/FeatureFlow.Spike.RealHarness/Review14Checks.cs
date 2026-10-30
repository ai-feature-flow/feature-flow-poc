using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>Executable regression checks for review 14 A1, A5 and A6.</summary>
public static class Review14Checks
{
    public static async Task<int> RunAsync()
    {
        HarnessProgram.WriteHeader("Review 14: policy floors and run-start checks");
        var failures = 0;
        var organisation = OrganisationConfiguration.Load();
        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");

        var accepted = RunStartValidator.Validate(plan, organisation);
        Console.WriteLine("[A5] corrected plan against organisation configuration:");
        accepted.WriteTo(Console.Out);
        if (!accepted.Accepted)
            failures++;
        Console.WriteLine();

        var unknownPlan = plan.Clone();
        unknownPlan.ById("gate_merge")!.Gate = new StepGate
        {
            DecisionType = "ticket.unknown_approval",
        };
        var rejected = RunStartValidator.Validate(unknownPlan, organisation);
        Console.WriteLine("[A5] plan with unknown decision type:");
        rejected.WriteTo(Console.Out);
        if (rejected.Accepted ||
            !rejected.Problems.Any(problem => problem.Check == 21 &&
                                               problem.Message.Contains("ticket.unknown_approval", StringComparison.Ordinal)))
        {
            failures++;
        }
        Console.WriteLine();

        var harnessDecision = new DecisionRecord(
            "gate_merge",
            OrganisationConfiguration.TicketMergeDecision,
            "approved",
            new DecisionActor(DecisionActorKind.Harness, "real-harness", Array.Empty<string>()),
            DateTimeOffset.UtcNow);
        var mainAuthorization = MergeAuthorization.Evaluate(
            "main", "main", new[] { harnessDecision }, organisation);
        var mainMergeProviderCalled = false;
        if (mainAuthorization.Allowed)
            mainMergeProviderCalled = true;

        Console.WriteLine($"[A1] main PR, gate_merge approved by actor=harness: {mainAuthorization.Message}");
        Console.WriteLine($"[A1] merge provider called={mainMergeProviderCalled}");
        if (mainAuthorization.Allowed || mainMergeProviderCalled ||
            !mainAuthorization.Message.Contains("floor F1", StringComparison.Ordinal) ||
            !mainAuthorization.Message.Contains("missing human approval", StringComparison.Ordinal))
        {
            failures++;
        }
        Console.WriteLine();

        var humanDecision = new DecisionRecord(
            "gate_merge",
            OrganisationConfiguration.TicketMergeDecision,
            "approved",
            new DecisionActor(DecisionActorKind.Human, "product-owner", new[] { "approver" }),
            DateTimeOffset.UtcNow);
        var integrationAuthorization = MergeAuthorization.Evaluate(
            "ff/epic/spike-2", "main", new[] { humanDecision }, organisation);
        var integrationMergeProviderCalled = false;
        if (integrationAuthorization.Allowed)
            integrationMergeProviderCalled = true;

        Console.WriteLine($"[A1] integration PR, approval actor=human identity=product-owner: {integrationAuthorization.Message}");
        Console.WriteLine($"[A1] merge provider called={integrationMergeProviderCalled}; result=" +
                          $"{(integrationMergeProviderCalled ? "SUCCEEDED" : "REFUSED")}");
        if (!integrationAuthorization.Allowed || !integrationMergeProviderCalled)
            failures++;
        Console.WriteLine();

        // A1, the hole review 15 found. The default branch must come from the
        // code host, not from feature-flow.yaml on the ticket branch — the agent
        // can write that file, so a floor that trusted it could be edited away.
        //
        // The attack: the agent edits feature-flow.yaml to declare some other
        // branch as the default, then merges into the real default branch. If
        // the floor read the manifest, the merge would be judged by the weaker
        // ticket policy and allowed through. Here the code host says 'main'
        // while the manifest claims 'ff/not-the-default', so the merge must be
        // refused and the disagreement named.
        var spoofed = MergeAuthorization.Evaluate(
            baseBranch: "main",
            defaultBranch: "main",                      // what the code host says
            decisions: new[] { harnessDecision },
            organisation: organisation,
            declaredDefaultBranch: "ff/not-the-default"); // what the manifest claims
        Console.WriteLine($"[A1] manifest declares a different default branch: {spoofed.Message}");
        if (spoofed.Allowed ||
            !spoofed.Message.Contains("floor F1", StringComparison.Ordinal) ||
            !spoofed.Message.Contains("declares default branch", StringComparison.Ordinal))
        {
            failures++;
        }
        Console.WriteLine();

        var patch = new PlanPatch
        {
            BaseRevision = plan.Revision,
            ProposedBy = "agent:replan",
            Reason = "introduce an unapproved release-manager role",
            Changes =
            {
                new PatchChange
                {
                    Op = PatchOps.ReplaceInputs,
                    Target = "implement",
                    Inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "feature-flow-default:roles/release-manager",
                    },
                },
            },
        };
        var patchResult = ReplanPolicy.Apply(plan, patch, organisation);
        Console.WriteLine($"[A6] agent re-plan with disallowed role: {patchResult.Kind}");
        Console.WriteLine($"[A6] reason: {string.Join("; ", patchResult.Reasons)}");
        if (patchResult.Kind != PatchResultKind.PendingHumanDecision)
            failures++;

        Console.WriteLine();

        failures += await A3AgentAuthorsAsync(plan, organisation);
        failures += await A7ReviewerIsAModelAsync(plan, organisation);
        failures += await A8PlannerAuthorsPatchesAsync(plan, organisation);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "REVIEW 14 checks: PASS"
            : $"REVIEW 14 checks: FAIL ({failures} failure(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A3: <c>StepWork</c> is not on the agent path, and the runtime is pointed
    /// at a real model through the router.
    ///
    /// <para>
    /// The first run substituted canned content for the provider under test.
    /// This asserts the substitution is gone: no <c>agent.run</c> step has
    /// scripted content, and the runtime's configuration names a model and an
    /// endpoint resolved by reference rather than a local default.
    /// </para>
    /// </summary>
    private static async Task<int> A3AgentAuthorsAsync(Plan plan, OrganisationConfiguration organisation)
    {
        Console.WriteLine("[A3] the agent authors the change");
        var failures = 0;

        // The decisive assertion: no agent.run step has canned content.
        var agentSteps = plan.Steps.Where(s => s.Kind == "agent.run").Select(s => s.Id).ToList();
        var scripted = agentSteps.Where(id => StepWork.HasChangeFor(id)).ToList();

        Console.WriteLine($"[A3] agent.run steps:        {string.Join(", ", agentSteps)}");
        Console.WriteLine($"[A3] steps with canned work: {scripted.Count} " +
                          $"{(scripted.Count > 0 ? string.Join(", ", scripted) : "(none)")}");
        if (scripted.Count > 0)
            failures++;

        Console.WriteLine($"[A3] WorkIsScripted flag:    {Prompt.WorkIsScripted}");
        if (Prompt.WorkIsScripted)
            failures++;

        // The runtime must be configured with a real model and endpoint, both
        // resolved from the secrets store by reference (floor F2).
        var secrets = new FeatureFlow.Spike.Providers.LocalFileSecretsProvider();
        var registry = RuntimeRegistry.Create(
            secrets, new FeatureFlow.Spike.Providers.SecretScrubber(), HarnessProgram.Fixture,
            HarnessProgram.Records, "review14-a3");

        // The copilot-cli instance by name: since 2026-10-30 no role maps to it.
        var copilot = (CopilotCliAgentRuntime)registry.Instance(CopilotCliAgentRuntime.InstanceName);
        var options = copilot.Options;

        Console.WriteLine($"[A3] runtime instance:       {CopilotCliAgentRuntime.InstanceName}");
        Console.WriteLine($"[A3] model:                  {options.Model ?? "(none) — BYOK would refuse"}");
        Console.WriteLine($"[A3] endpoint:               {options.Endpoint ?? "(default routing)"}");
        Console.WriteLine($"[A3] key reference:          {options.EndpointKeyReference?.Text ?? "(none)"}");

        if (string.IsNullOrWhiteSpace(options.Model) || !options.HasEndpoint)
        {
            Console.WriteLine("[A3] FAIL: the runtime is not pointed at a model through a router");
            failures++;
        }

        // The key must resolve, and must not be a mock value.
        var resolved = await secrets.ResolveAsync(
            options.EndpointKeyReference!, CancellationToken.None);
        if (!resolved.IsOk)
        {
            Console.WriteLine($"[A3] FAIL: the router key did not resolve: " +
                              $"{resolved.Error!.Class}/{resolved.Error.Reason}");
            failures++;
        }
        else
        {
            var value = resolved.Value!.Value.Trim();
            var isMock = value.Contains("mock", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[A3] key resolved:          {resolved.Value!.Describe()} " +
                              $"mock={isMock}");
            if (isMock)
            {
                Console.WriteLine("[A3] FAIL: the resolved key is a mock value");
                failures++;
            }
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// A7: the verdict comes from a model, not from a hash.
    ///
    /// <para>
    /// The check runs the reviewer twice on the same input and reports whether
    /// the verdicts agree. It does <b>not</b> assert agreement — a disagreement
    /// is the finding, and forcing agreement would recreate the circularity A7
    /// is about. It asserts only that a model was actually consulted.
    /// </para>
    /// </summary>
    private static async Task<int> A7ReviewerIsAModelAsync(Plan plan, OrganisationConfiguration organisation)
    {
        Console.WriteLine("[A7] R4 runs a real reviewer model");
        var failures = 0;

        var context = NewContext("review14-a7", plan, organisation);

        var diffHash = await DiffHashAsync(context);
        var diffText = await DiffTextAsync(context);
        var prior = Findings.PriorFor(context, "review");
        var declined = Findings.DeclinedIds(context);

        var first = await context.Reviewer.ReviewAsync(
            diffHash, diffText, prior, declined, "review", CancellationToken.None);
        var second = await context.Reviewer.ReviewAsync(
            diffHash, diffText, prior, declined, "review", CancellationToken.None);

        if (!first.IsOk || !second.IsOk)
        {
            var error = (!first.IsOk ? first.Error : second.Error)!;
            Console.WriteLine($"[A7] NOT EVIDENCED: the reviewer model could not be called: " +
                              $"{error.Class}/{error.Reason}: {error.Detail}");
            Console.WriteLine();
            return 1;
        }

        var pass1 = first.Value!;
        var pass2 = second.Value!;

        Console.WriteLine($"[A7] pass 1: outcome={pass1.Outcome} findings={pass1.Findings.Count} " +
                          $"model={pass1.Model ?? "(unreported)"} in={pass1.InputTokens} out={pass1.OutputTokens}");
        Console.WriteLine($"[A7] pass 2: outcome={pass2.Outcome} findings={pass2.Findings.Count} " +
                          $"model={pass2.Model ?? "(unreported)"} in={pass2.InputTokens} out={pass2.OutputTokens}");

        // The assertion that matters: a model was consulted. Without a reported
        // model the verdict could still be hash-derived, which is the bug.
        var modelReported = !string.IsNullOrWhiteSpace(pass1.Model) &&
                            !string.IsNullOrWhiteSpace(pass2.Model);
        Console.WriteLine($"[A7] model consulted:        {modelReported}");
        if (!modelReported)
            failures++;

        var agree = string.Equals(pass1.Outcome, pass2.Outcome, StringComparison.Ordinal) &&
                    pass1.Findings.Count == pass2.Findings.Count;
        Console.WriteLine($"[A7] verdicts agree:         {agree}");
        if (!agree)
        {
            Console.WriteLine("[A7] FINDING: the reviewer model is not stable across passes " +
                              "on unchanged input. This is reported, not reconciled.");
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// A8: the <c>insert_after</c> measurement with a planner model authoring
    /// the patches from the readable form.
    ///
    /// <para>
    /// Until this runs, the measurement is <b>not measured</b>: the earlier
    /// numbers came from patches generated in code by cycling targets, so they
    /// measured floor F3 refusals rather than planner reliability.
    /// </para>
    /// </summary>
    private static async Task<int> A8PlannerAuthorsPatchesAsync(
        Plan plan, OrganisationConfiguration organisation)
    {
        Console.WriteLine("[A8] the planner authors the patches from the readable form");
        var failures = 0;

        var context = NewContext("review14-a8", plan, organisation);

        // The readable form must render, and must mark the protected step —
        // that is what the planner reads before choosing a target.
        var readable = ReadableForm.Render(plan);
        var marksProtected = readable.Contains("[protected]", StringComparison.Ordinal);
        Console.WriteLine($"[A8] readable form lines:    {readable.Split('\n').Length}");
        Console.WriteLine($"[A8] marks protected steps:  {marksProtected}");
        if (!marksProtected)
            failures++;

        var authored = 0;
        var plannerFailures = 0;
        var applied = 0;
        var validationFailures = 0;

        foreach (var form in new[] { "insert_after", "add_step_change_route" })
        {
            for (var i = 0; i < 3; i++)
            {
                var intent = $"Notify the team after step {i + 1} of the delivery completes.";
                var proposal = await context.Planner.AuthorAsync(plan, form, intent, CancellationToken.None);

                if (!proposal.IsOk)
                {
                    plannerFailures++;
                    Console.WriteLine($"[A8] {form} trial {i}: planner failure: {proposal.Error!.Reason}");
                    continue;
                }

                authored++;
                var working = plan.Clone();
                var result = ApplyPatch.Apply(working, proposal.Value!);

                if (result.Kind == PatchResultKind.Applied)
                    applied++;
                else
                    validationFailures++;

                Console.WriteLine($"[A8] {form} trial {i}: target={proposal.Value!.Changes[0].Target} " +
                                      $"ops={proposal.Value!.Changes.Count} -> {result.Kind}");
            }
        }

        Console.WriteLine($"[A8] authored={authored} plannerFailures={plannerFailures} " +
                          $"applied={applied} validationFailures={validationFailures}");

        // The measurement is evidenced only if the planner actually authored
        // patches. A run where every trial failed to author is not a
        // measurement of the two forms.
        if (authored == 0)
        {
            Console.WriteLine("[A8] NOT EVIDENCED: the planner authored no patches");
            failures++;
        }

        Console.WriteLine();
        return failures;
    }

    private static RunContext NewContext(
        string runId, Plan plan, OrganisationConfiguration organisation) =>
        new(runId, plan, HarnessProgram.Fixture, "ff/ticket/spike-2-1", "ff/epic/spike-2",
            "TICKET-SPIKE2-1", HarnessProgram.Records, organisation);

    private static async Task<string?> DiffHashAsync(RunContext context)
    {
        var diff = await context.CodeHost.GetDiffAsync(
            context.CodeHostRepo, context.IntegrationBranch, context.Branch, CancellationToken.None);

        return diff.IsOk ? diff.Value!.ContentHash : null;
    }

    private static async Task<string> DiffTextAsync(RunContext context)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add($"{context.IntegrationBranch}...{context.Branch}");

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return "";

        var text = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();

        return git.ExitCode == 0 ? text : "";
    }
}
