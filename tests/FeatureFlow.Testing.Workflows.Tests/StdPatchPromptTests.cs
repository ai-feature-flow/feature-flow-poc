using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Testing.Workflows.Tests;

/// <summary>
/// The approval a person sees for std's implementer's patch: the core's prompt (<c>Waits.Describe</c>, docs/04 section 6.1),
/// on std's own <c>ticket_delivery</c>. The run is driven to <c>implement</c> by skipping the steps before it, as a person may.
/// </summary>
public class StdPatchPromptTests
{
    [Fact]
    public void An_implementers_patch_waits_with_the_reason_why_where_it_continues_and_the_changes()
    {
        var d = new DiagnosticBag();
        var std = Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), d)!;
        var run = new RunInfo("test-prompt", ["acme/api"], new Dictionary<string, YNode> { ["ticket_id"] = new YScalar("FF-1042", true, new SourceLocation("test", 0, 0)) });
        var (core, diagnostics) = RunCore.Start(new LibrarySet(std), new LibraryReference("std", "templates", "ticket_delivery"), run);
        Assert.True(core is not null, string.Join("; ", diagnostics.Where(x => x.Blocks)));

        var at = DriveTo(core, "implement");
        var patch = PatchReader.Read(YamlText.Parse("""
            api_version: feature-flow/v1
            kind: patch
            metadata: { proposed_by: { type: agent, step_id: implement }, reason: "the orders table needs a migration" }
            base_revision: 1
            resume_at: migrate
            changes:
              - op: insert
                after: implement
                step:
                  id: migrate
                  agent: { role: std:roles/implementer, task: std:tasks/implement_ticket }
                  with: { ticket_id: FF-1042 }
                  outcomes: { migrated: success, blocked: blocked }
            """, "patch.yaml", d)!, d)!;
        var completed = core.Apply(new StepCompleted(at, "implemented", Outputs, new Dictionary<string, string> { ["implementation_claims"] = "artifact:implement/claims" }, patch));
        Assert.True(completed.Accepted, string.Join("; ", completed.Errors));

        var wait = Assert.Single(core.State.Waits, w => w.Kind == WaitKind.PatchApproval);
        var shown = core.Describe(wait);
        Assert.Equal("ticket.patch_approval", shown.Type);
        Assert.StartsWith("""
            implement proposes a change to the plan (revision 1).
            Reason: the orders table needs a migration
            Needs approval: implement has planning.review: required
            Continues at: migrate
            Changes:
            - op: insert
              after: implement
              step:
                id: migrate
            """.ReplaceLineEndings("\n"), shown.Prompt, StringComparison.Ordinal);
    }

    /// <summary>The outputs the engine publishes for every agent step (docs/04 section 3.6), as a report gives them.</summary>
    static readonly Dictionary<string, object?> Outputs = new() { ["coverage_known"] = false, ["changed_files_unread"] = 0L, ["commits"] = 0L };

    /// <summary>
    /// Skips, as a person may before they run, every step before <paramref name="target"/> with its first success outcome, then
    /// lets the run reach it.
    /// </summary>
    static string DriveTo(RunCore core, string target)
    {
        foreach (var step in core.Plan.AllSteps.TakeWhile(s => s.Id != target).Where(s => s.Outcomes().Any(o => o.Class == OutcomeClass.Success)))
        {
            var outcome = step.Outcomes().First(o => o.Class == OutcomeClass.Success).Name;
            var skipped = core.Apply(new StepSkipped(step.Id, outcome, "test", "driving to " + target, MayTouchProtected: true));
            Assert.True(skipped.Accepted, $"{step.Id}: {string.Join("; ", skipped.Errors)}");
        }

        for (var i = 0; i < 100; i++)
            foreach (var command in core.Advance())
                if (command is StartStep s)
                    return s.StepId == target ? s.StepId : throw new InvalidOperationException($"{s.StepId} started before {target}");

        throw new InvalidOperationException($"{target} did not start");
    }
}
