using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// The run context (docs/04 section 3.5; LOG 405): what the run has learned, rendered by the core into the next agent
/// step's spec. A work failure is a retry cause the next attempt reads; an infrastructure retry is not; a report's summary
/// is kept; <c>context: none</c> reads nothing; a child reads its parent's first; the rendering is bounded.
/// </summary>
public class RunContextTests
{
    const string Template = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: context, level: ticket }
        steps:
          - id: work
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { done: success }
            routes: { done: quiet }
          - id: quiet
            agent: { role: std:roles/planner, task: std:tasks/repair_failures, context: none }
            outcomes: { done: success }
            routes: { done: next }
          - id: next
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { done: success }
            routes: { done: { end: completed } }
        """;

    static readonly Probe Lib = new("run-context", ("context", Template));

    static string? ContextOf(RunCore core, string stepId) => core.StepSpec(stepId).Spec!.Agent!.RunContext;

    [Fact]
    public void A_work_failure_is_the_next_attempts_retry_cause_and_an_infrastructure_retry_is_not()
    {
        var core = Lib.Start("context");
        Assert.Equal([new StartStep("work", 1)], core.Advance());

        Assert.True(core.Apply(new StepFailed("work", ErrorClass.Infrastructure, "the sandbox could not be reached")).Accepted);
        Assert.Equal([new StartStep("work", 1)], core.Advance());
        Assert.Null(ContextOf(core, "work"));

        Assert.True(core.Apply(new StepFailed("work", ErrorClass.Work, "output contract: outcome 'finished' is not an outcome")).Accepted);
        Assert.Equal([new StartStep("work", 2)], core.Advance());

        var context = ContextOf(core, "work");
        Assert.NotNull(context);
        Assert.Contains("attempt 1 of work failed: output contract: outcome 'finished' is not an outcome", context, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox", context, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reports_summary_is_read_by_later_agents_except_one_with_context_none_and_is_carried()
    {
        var core = Lib.Start("context");
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("work", "done", Summary: "added the audit log table")).Accepted);
        Assert.Equal([new StartStep("quiet", 1)], core.Advance());
        Assert.Null(ContextOf(core, "quiet"));

        var resumed = RunCore.Resume(Lib.Libraries, Probe.Ref("context"), core.Snapshot());
        Assert.True(resumed.Apply(new StepCompleted("quiet", "done")).Accepted);
        Assert.Equal([new StartStep("next", 1)], resumed.Advance());
        Assert.Contains("- work reported done: added the audit log table", ContextOf(resumed, "next"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_reads_its_parents_context_first()
    {
        var run = Probe.Run() with { ParentId = "parent-1", ParentContext = "## Run context\n\n- ticket.spec_approval at approve: rejected by ann: no audit log\n" };
        var core = Lib.Start("context", run: run);
        core.Advance();
        core.Apply(new StepFailed("work", ErrorClass.Work, "tests fail"));
        core.Advance();

        var context = ContextOf(RunCore.Resume(Lib.Libraries, Probe.Ref("context"), core.Snapshot()), "work")!;
        Assert.StartsWith("## Run context\n\n### From the parent run\n\n- ticket.spec_approval at approve: rejected by ann: no audit log", context, StringComparison.Ordinal);
        Assert.True(context.IndexOf("### This run", StringComparison.Ordinal) < context.IndexOf("attempt 1 of work failed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rendered_context_is_bounded_by_dropping_the_oldest_entries_first()
    {
        var entries = Enumerable.Range(1, RunContexts.MaxEntries)
            .Select(i => new ContextEntry(ContextKinds.RetryCause, "work", i, $"attempt {i} of work failed: " + new string('x', 400))).ToList();

        var context = RunContexts.Render(entries, null)!;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context) <= RunContexts.MaxBytes);
        Assert.Matches(@"\(\d+ earlier entries omitted\)", context);
        Assert.Contains($"attempt {RunContexts.MaxEntries} of work failed", context, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt 1 of work failed", context, StringComparison.Ordinal);
        Assert.Null(RunContexts.Render([], null));
    }

    [Fact]
    public void A_summary_is_kept_to_its_bound()
    {
        var core = Lib.Start("context");
        core.Advance();
        core.Apply(new StepCompleted("work", "done", Summary: new string('é', 5000)));

        var entry = Assert.Single(core.State.Context);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.Text) <= RunContexts.MaxSummaryBytes + "work reported done: ".Length);
        Assert.EndsWith(" [...]", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_run_keeps_at_most_its_entry_bound()
    {
        var core = Lib.Start("context", new RunOptions { DefaultAttempts = 100 });
        for (var i = 1; i <= RunContexts.MaxEntries + 5; i++)
        {
            core.Advance();
            core.Apply(new StepFailed("work", ErrorClass.Work, $"failure {i}"));
        }

        Assert.Equal(RunContexts.MaxEntries, core.State.Context.Count);
        Assert.Equal("attempt 6 of work failed: failure 6", core.State.Context[0].Text);
    }

    /// <summary>One very long entry is cut to its own bound, so the rendered context still holds it and the entries before it.</summary>
    [Fact]
    public void One_long_entry_is_kept_to_the_entry_bound_and_does_not_empty_the_context()
    {
        var core = Lib.Start("context", new RunOptions { DefaultAttempts = 10 });
        core.Advance();
        core.Apply(new StepFailed("work", ErrorClass.Work, "tests fail"));
        core.Advance();
        core.Apply(new StepFailed("work", ErrorClass.Work, new string('x', 64 * 1024)));
        core.Advance();

        var entry = core.State.Context[^1];
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.Text) <= RunContexts.MaxEntryBytes);
        Assert.EndsWith(" [...]", entry.Text, StringComparison.Ordinal);
        var context = ContextOf(core, "work")!;
        Assert.Contains("attempt 1 of work failed: tests fail", context, StringComparison.Ordinal);
        Assert.Contains("attempt 2 of work failed: xxx", context, StringComparison.Ordinal);
    }

    const string Gated = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: gated, level: ticket }
        steps:
          - id: approve
            decision: { type: ticket.spec_approval }
            outcomes: { approved: success, rejected: failure }
            routes: { approved: next, rejected: { end: rejected } }
          - id: next
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { done: success }
            routes: { done: { end: completed } }
        """;

    /// <summary>An answer given without a reason reaches the next agent as what was decided and by whom, with no reason invented (LOG 502).</summary>
    [Theory]
    [InlineData(null, "- ticket.spec_approval at approve: approved by ann\n")]
    [InlineData("covers the audit log", "- ticket.spec_approval at approve: approved by ann: covers the audit log\n")]
    public void An_answer_reaches_the_next_agent_with_its_reason_only_when_one_was_given(string? reason, string line)
    {
        var lib = new Probe("run-context-gated", ("gated", Gated));
        var core = lib.Start("gated");
        var gate = Assert.IsType<OpenGate>(Assert.Single(core.Advance()));

        Assert.True(core.Answer(new AnswerArgs(gate.WaitId, "ann", "approved", Reason: reason)).Accepted);
        Assert.Equal([new StartStep("next", 1)], core.Advance());

        Assert.EndsWith(line, ContextOf(core, "next"), StringComparison.Ordinal);
    }

    const string Checked = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: checked, level: ticket }
        steps:
          - id: verify
            agent: { role: std:roles/planner, task: std:tasks/repair_failures, context: none }
            outcomes: { done: success, stuck: failure }
            routes: { done: check, stuck: { end: failed } }
          - id: check
            action: artifact.validate
            routes: { invalid: verify, valid: verify }
        """;

    static string? CausesOf(RunCore core, string stepId) => core.StepSpec(stepId).Spec!.Agent!.Causes;

    /// <summary>
    /// An action's non-success summary is addressed to the agent step it routes back to, which reads it even with
    /// <c>context: none</c>; a first execution, and one after a success, read none (LOG 513).
    /// </summary>
    [Fact]
    public void A_failures_reason_reaches_the_agent_step_it_routes_back_to_even_with_context_none()
    {
        var core = new Probe("run-context-checked", ("checked", Checked)).Start("checked");
        Assert.Equal([new StartStep("verify", 1)], core.Advance());
        Assert.Null(CausesOf(core, "verify"));

        Assert.True(core.Apply(new StepCompleted("verify", "done")).Accepted);
        Assert.Equal([new StartStep("check", 1)], core.Advance());
        Assert.True(core.Apply(new StepCompleted("check", "invalid", Summary: "check: invalid: F-1: this step may set only fixed, open, not 'declined'")).Accepted);
        Assert.Equal([new StartStep("verify", 2)], core.Advance());

        Assert.Null(ContextOf(core, "verify"));
        Assert.Equal("## Why this step runs again\n\n- check ended invalid: check: invalid: F-1: this step may set only fixed, open, not 'declined'\n",
            CausesOf(core, "verify"));

        Assert.True(core.Apply(new StepCompleted("verify", "done")).Accepted);
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("check", "valid")).Accepted);
        Assert.Equal([new StartStep("verify", 3)], core.Advance());
        Assert.Null(CausesOf(core, "verify"));
    }

    /// <summary>A cause outlasts a failed attempt: the next attempt reads it with the failure's reason (LOG 513); a success ends it (above).</summary>
    [Fact]
    public void A_cause_reaches_every_attempt_until_the_step_succeeds()
    {
        var core = new Probe("run-context-checked", ("checked", Checked)).Start("checked");
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("verify", "done")).Accepted);
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("check", "invalid", Summary: "F-1 may not be declined")).Accepted);
        Assert.Equal([new StartStep("verify", 2)], core.Advance());

        Assert.True(core.Apply(new StepFailed("verify", ErrorClass.Work, "output still invalid after correction turns")).Accepted);
        Assert.Equal([new StartStep("verify", 3)], core.Advance());

        Assert.Equal("## Why this step runs again\n\n- check ended invalid: F-1 may not be declined\n" +
                     "- attempt 2 of verify failed: output still invalid after correction turns\n", CausesOf(core, "verify"));
    }
}
