namespace FeatureFlow.Interpreter.Tests;

/// <summary>Ready steps, starting on first completion, joins and chains (docs/02 section 2.1; docs/03 section 8).</summary>
public class JoinTests
{
    static string Fan(string join, int? max = null) => $$"""
        api_version: feature-flow/v1
        kind: template
        metadata: { name: fan_{{join}}, level: ticket }
        steps:
          - id: fan
            parallel:
              join: {{join}}
              {{(max is null ? "" : $"max_concurrency: {max}")}}
              branches:
                - { id: a, action: test.run }
                - { id: b, action: test.run }
                - { id: c, action: test.run }
            routes: { completed: { end: completed }, any_failed: { end: failed } }
        """;

    const string Chain = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: chain, level: ticket }
        steps:
          - id: each
            for_each:
              items: [unit, component, e2e]
              mode: chain
              step: { action: test.run, with: { tier_tags: [{ expr: "item" }] } }
            routes: { completed: { end: completed }, any_failed: { end: failed } }
        """;

    const string Mixed = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: mixed, level: ticket }
        steps:
          - id: both
            parallel:
              branches:
                - id: ask
                  decision: { type: delivery.partial_recovery, prompt: std:prompts/partial_delivery }
                  outcomes: { retry: success, revert: failure }
                - { id: work, action: test.run }
            routes: { completed: { end: completed }, any_failed: { end: failed } }
        """;

    static readonly Probe Lib = new("joins",
        ("fan_all", Fan("all", 2)), ("fan_any", Fan("any", 2)), ("fan_2", Fan("2")), ("chain", Chain), ("mixed", Mixed),
        ("mixed_any", MixedAny));

    const string MixedAny = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: mixed_any, level: ticket }
        steps:
          - id: both
            parallel:
              join: any
              branches:
                - id: ask
                  decision: { type: delivery.partial_recovery, prompt: std:prompts/partial_delivery }
                  outcomes: { retry: success, revert: failure }
                - { id: work, action: test.run }
            routes: { completed: { end: completed }, any_failed: { end: failed } }
        """;

    [Fact]
    public void A_join_that_decides_closes_the_decisions_still_open_in_its_branches()
    {
        var core = Lib.Start("mixed_any");
        core.Advance();

        core.Apply(new StepCompleted("work", "passed"));
        var commands = core.Advance();

        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Contains(commands, c => c is CloseWait { StepId: "ask" });
        Assert.Empty(core.State.Waits);
    }

    [Fact]
    public void A_waiting_branch_starts_as_soon_as_a_running_one_ends()
    {
        var core = Lib.Start("fan_all");
        Assert.Equal([new StartStep("a", 1), new StartStep("b", 1)], core.Advance().OfType<StartStep>());

        core.Apply(new StepCompleted("a", "passed"));

        // c starts while b is still running: no batch waits for its slowest member.
        Assert.Equal([new StartStep("c", 1)], core.Advance().OfType<StartStep>());
        Assert.Contains("b", core.State.InFlight);
    }

    [Fact]
    public void Join_all_completes_when_every_branch_succeeds_and_fails_if_any_does_not()
    {
        var passing = Lib.Start("fan_all");
        new Driver(passing) { Respond = (id, _) => new StepCompleted(id, "passed") }.Run();
        var failing = Lib.Start("fan_all");
        new Driver(failing) { Respond = (id, _) => new StepCompleted(id, id == "b" ? "failed" : "passed") }.Run();

        Assert.Equal(RunStatus.Completed, passing.State.Status);
        Assert.Equal(RunStatus.Failed, failing.State.Status);
        Assert.Equal(["a", "b", "c"], Driver.Path(failing));
    }

    [Fact]
    public void Join_any_completes_on_the_first_success_and_cancels_the_rest()
    {
        var core = Lib.Start("fan_any");
        core.Advance();
        core.Apply(new StepCompleted("a", "failed"));
        core.Advance();
        core.Apply(new StepCompleted("b", "passed"));

        var commands = core.Advance();

        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Contains(new CancelStep("c"), commands);
    }

    [Fact]
    public void Join_n_fails_as_soon_as_n_successes_are_impossible()
    {
        var core = Lib.Start("fan_2");
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, id == "c" ? "passed" : "failed") }.Run();

        Assert.Equal(RunStatus.Failed, core.State.Status);
        Assert.Equal(["a", "b", "c"], Driver.Path(core));
        Assert.Contains(core.State.Trace, t => t is { Kind: "completed", StepId: "fan", Detail: "any_failed" });
    }

    [Fact]
    public void A_chain_runs_items_in_order_and_stops_at_the_first_that_does_not_succeed()
    {
        var all = Lib.Start("chain");
        new Driver(all) { Respond = (id, _) => new StepCompleted(id, "passed") }.Run();
        var stopped = Lib.Start("chain");
        new Driver(stopped) { Respond = (id, _) => new StepCompleted(id, id == "each_1" ? "failed" : "passed") }.Run();

        Assert.Equal(["each_0", "each_1", "each_2"], Driver.Path(all));
        Assert.Equal(RunStatus.Completed, all.State.Status);
        Assert.Equal(["each_0", "each_1"], Driver.Path(stopped));
        Assert.Equal(RunStatus.Failed, stopped.State.Status);
    }

    [Fact]
    public void An_open_decision_does_not_block_other_steps()
    {
        var core = Lib.Start("mixed");
        var first = core.Advance();
        Assert.Contains(new OpenDecision("ask", 1), first);
        Assert.Contains(new StartStep("work", 1), first);

        Assert.True(core.Apply(new StepCompleted("work", "passed")).Accepted);
        core.Advance();
        Assert.Equal(RunStatus.Running, core.State.Status);
        Assert.True(core.Apply(new DecisionAnswered("ask", "retry")).Accepted);
        core.Advance();

        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void An_answer_must_be_a_declared_outcome()
    {
        var core = Lib.Start("mixed");
        core.Advance();

        var result = core.Apply(new DecisionAnswered("ask", "approve"));

        Assert.False(result.Accepted);
    }
}
