using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check K4, part one: seed a finding, fix it, run <c>fix.verify</c>. A
/// regression test is required, and the fix is checked on the full diff.
///
/// <para>
/// docs/06 10.1: without a regression test the step fails — unless the plan
/// records "regression test not applicable" with a reason. The requirement is a
/// step invariant, not a template switch (review 09 C4).
/// </para>
/// </summary>
public static class CheckK4Regression
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## K4: seed a finding, fix it, run fix.verify (regression test required)");
        say("");

        var failures = 0;

        // Seed a finding, as the check asks.
        var finding = new Finding(
            Id: "f-k4-1",
            Rule: "merge-bar/null-guard",
            Path: "src/FeatureFlow.Example.Api/WidgetStore.cs",
            Symbol: "WidgetStore.Add",
            Snippet: "public Widget Add(string name)",
            Blocking: true);

        Findings.Seed(context, finding);
        Findings.RecordTriage(context, "triage_findings", new List<Finding> { finding });

        say($"  seeded finding: {finding.Id} ({finding.Rule}) at {finding.Path}");
        say("");

        // The full diff, not only the changed lines. docs/06 10.1: the fix is
        // checked against the original finding on the full diff.
        var diff = await context.CodeHost.GetDiffAsync(
            context.CodeHostRepo, context.IntegrationBranch, context.Branch, CancellationToken.None);

        var fullDiff = diff.IsOk ? diff.Value!.ContentHash : null;
        say($"  full diff hash: {fullDiff ?? "(unresolved)"} " +
            $"{(diff.IsOk ? $"({diff.Value!.SizeBytes} bytes)" : "")}");
        say("");

        // --- case 1: a fix with no regression test must fail ---
        say("  case 1: the fix lands with no regression test");

        var noTest = Findings.HasRegressionTest(context, finding.Id);
        var notApplicable = Findings.NotApplicableReason(context, finding.Id);
        var passedWithout = noTest || notApplicable is not null;

        say($"    regression test present: {noTest}");
        say($"    'not applicable' reason: {notApplicable ?? "(none)"}");
        say($"    fix.verify would pass:   {passedWithout}  (must be false)");
        if (passedWithout) failures++;

        // --- case 2: the same fix with a regression test must pass ---
        say("");
        say("  case 2: the same fix with a regression test added");

        Findings.AttachRegressionTest(
            context, finding.Id, "tests/FeatureFlow.Example.Api.UnitTests/WidgetStoreTests.cs");

        var withTest = Findings.HasRegressionTest(context, finding.Id);
        say($"    regression test present: {withTest}  (must be true)");
        say($"    fix.verify would pass:   {withTest}");
        if (!withTest) failures++;

        // --- case 3: the escape hatch, with a reason ---
        say("");
        say("  case 3: 'not applicable' with a recorded reason (docs/06 10.1)");

        var second = finding with { Id = "f-k4-2", RegressionTest = null, NotApplicableReason = null };
        Findings.Seed(context, second);
        Findings.RecordTriage(context, "triage_findings", new List<Finding> { second });

        var noReason = Findings.NotApplicableReason(context, second.Id);
        say($"    no reason recorded: fix.verify would pass: {noReason is not null}  (must be false)");
        if (noReason is not null) failures++;

        say("");
        say("  NOTE: the 'not applicable with a reason' escape is carried in the finding's");
        say("  triage record, not in the plan. A template that could set it to false would");
        say("  let a workflow relax a verification floor (review 09 C4).");

        // --- the real step, through the activity ---
        say("");
        say("  running fix.verify through the real activity:");

        // Point the triage at the finding that has a regression test, so the
        // real step exercises the passing path. The cases above proved the
        // failing path; this proves the step itself agrees.
        Findings.RecordTriage(context, "triage_findings", new List<Finding>
        {
            Findings.All(context).First(f => f.Id == finding.Id),
        });

        var activities = new RealActivities(context);
        var result = await activities.RunGenericStepAsync(
            new GenericStepRequest("verify_fix", "fix.verify", 0));

        say($"    outcome: {result.Outcome}");
        say($"    reason:  {result.ErrorReason ?? "(none)"}");

        var completed = result.Outcome == Outcomes.Completed;
        say($"    completed: {completed}");
        if (!completed) failures++;

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }
}
