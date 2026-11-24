using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// The <c>override</c> update's routing in the core (docs/05 section 5; review R1): the duplicate and conflict ledger by
/// override id, the key and argument rules, and the input each key stands for are <see cref="RunCore.RouteOverride"/>'s;
/// <see cref="RunCore.Override"/> routes the same way and applies. <see cref="Agree"/> asserts the update validator's path
/// (RouteOverride alone) and the handler's (Override) decide the same, as <c>WaitRoutingTests</c> does for answers.
/// </summary>
public class OverrideRoutingTests
{
    const string Line = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: line, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: design
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { done: success, stuck: blocked }
          - id: review
            protected: true
            agent: { role: std:roles/reviewer, task: std:tasks/repair_failures }
            outcomes: { accepted: success, rejected: failure }
          - id: ship
            action: artifact.compose
            routes: { composed: { end: completed } }
        """;

    static readonly Probe Lib = new("override-routing", ("line", Line));

    static RunCore Started()
    {
        var core = Lib.Start("line");
        Assert.Equal([new StartStep("design", 1)], core.Advance());
        return core;
    }

    static OverrideArgs Args(string id, string key, string? stepId, IReadOnlyList<string>? variants, params (string Name, string Value)[] change) =>
        new(id, key, "alice", "because", stepId, change.ToDictionary(c => c.Name, c => c.Value), variants);

    /// <summary>
    /// Overrides as the shell does: the validator's <see cref="RunCore.RouteOverride"/>, then the handler's
    /// <see cref="RunCore.Override"/>. A refusal is the same refusal and changes nothing; a duplicate changes nothing; an
    /// accepted route is applied and recorded once.
    /// </summary>
    static OverrideOutcome Agree(RunCore core, OverrideArgs o)
    {
        var route = core.RouteOverride(o);
        var ledger = core.State.Overrides.ToList();
        var outcome = core.Override(o);
        switch (route)
        {
            case Refused refused:
                Assert.Equal(refused.Error, outcome.Error);
                Assert.Equal(ledger, core.State.Overrides);
                break;
            case Duplicate duplicate:
                Assert.Null(outcome.Error);
                Assert.Equal("duplicate: " + duplicate.Result, outcome.Result);
                Assert.Equal(ledger, core.State.Overrides);
                break;
            case Applies:
                Assert.True(outcome.Error is null, $"the validator accepted but the handler refused: {outcome.Error}");
                Assert.Equal(new AppliedOverride(o.OverrideId, o.Key, outcome.Result), core.State.Overrides[^1]);
                Assert.Equal(ledger.Count + 1, core.State.Overrides.Count);
                break;
            default:
                Assert.Fail($"unexpected route {route}");
                break;
        }

        return outcome;
    }

    [Fact]
    public void The_same_override_again_is_a_duplicate_and_its_id_with_another_key_is_a_conflict()
    {
        var core = Started();

        Assert.Equal(new OverrideOutcome(null, "applied"), Agree(core, Args("o-1", Overrides.Breakpoint, "ship", null)));
        Assert.Contains("ship", core.State.Breakpoints);
        Assert.Single(core.State.Trace, t => t is { Kind: "override", StepId: "ship", Detail: "breakpoint by alice: because -> applied" });

        Assert.Equal(new Duplicate("applied"), core.RouteOverride(Args("o-1", Overrides.Breakpoint, "ship", null)));
        Assert.Equal(new OverrideOutcome(null, "duplicate: applied"), Agree(core, Args("o-1", Overrides.Breakpoint, "ship", null)));
        Assert.Single(core.State.Trace, t => t.Kind == "override");

        Assert.Equal("conflict: override o-1 was already applied as breakpoint",
            Agree(core, Args("o-1", Overrides.Skip, "ship", null, ("outcome", "composed"))).Error);
        Assert.Single(core.State.Overrides);
        Assert.DoesNotContain("ship", core.State.Skips.Keys);
    }

    public static TheoryData<OverrideArgs, string?> Cases => new()
    {
        { Args("k-1", "pause", null, null), "'pause' is not a run-time override key" },
        { Args("k-2", Overrides.Answer, null, null), "answer is not delivered as an override: answers, recoveries and answers to agents go through the answer update" },
        { Args("k-3", Overrides.Skip, null, null), "skip needs a step" },
        { Args("k-4", Overrides.Skip, "ship", null), "skip needs change.outcome" },
        { Args("k-5", Overrides.GrantAttempts, null, null, ("attempts", "two")), "grant_attempts needs change.attempts, a whole number" },
        { Args("k-6", Overrides.Breakpoint, "ship", null, ("set", "maybe")), "breakpoint's change.set is true or false" },
        { Args("k-7", Overrides.OverrideLibrary, null, null, ("item", "std:tasks/repair_failures"), ("with", "new text")),
            "review may resolve std:tasks/repair_failures: overriding it needs override_library_protected" },
        { Args("k-8", Overrides.Breakpoint, "ship", null, ("set", "false")), null },
        { Args("k-9", Overrides.Skip, "ship", null, ("outcome", "composed")), null },
        { Args("k-10", Overrides.Skip, "review", null, ("outcome", "accepted")), "review is protected: skipping it needs skip_protected" },
        { Args("k-11", Overrides.GrantAttempts, "design", null, ("attempts", "2")), null },
        { Args("k-12", Overrides.GoTo, null, null), "go_to needs a step" },
        { Args("k-13", Overrides.Replan, "design", null), "re-plan: design is not an agent step with planning authority" },
        { Args("k-14", Overrides.GoTo, "design", null), null },
        { Args("k-15", Overrides.Instruct, null, null, ("text", "use the audit table")), "instruct needs a step" },
        { Args("k-16", Overrides.Instruct, "design", null), "instruct needs change.text" },
        { Args("k-17", Overrides.Instruct, "design", null, ("text", new string('x', 4097))), "instruct's change.text is at most 4096 bytes" },
        { Args("k-18", Overrides.Instruct, "ship", null, ("text", "use the audit table")), "ship is not an agent step; instructions go into an agent's next prompt" },
        { Args("k-19", Overrides.Instruct, "review", null, ("text", "use the audit table")), "review is protected: instructing it needs instruct_protected" },
        { Args("k-20", Overrides.Instruct, "design", null, ("text", "use the audit table")), null },
    };

    [Fact]
    public void An_instruction_for_a_protected_step_needs_the_variant_and_names_the_step()
    {
        var core = Started();

        Assert.Equal("applied (protected: review)",
            Agree(core, Args("i-1", Overrides.Instruct, "review", [Overrides.InstructProtected], ("text", "check the migration too"))).Result);
        Assert.Contains("from alice: check the migration too", core.State.Context.Single(e => e.Kind == ContextKinds.Instruction && e.StepId == "review").Text,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_validator_and_the_handler_route_an_override_the_same_way(OverrideArgs o, string? error)
    {
        var core = Started();

        var outcome = Agree(core, o);

        Assert.Equal(error, outcome.Error);
    }

    [Fact]
    public void A_skip_is_applied_with_its_result_and_a_protected_step_is_named()
    {
        var core = Started();

        Assert.Equal("applied: skipped when the run reaches it", Agree(core, Args("s-1", Overrides.Skip, "ship", null, ("outcome", "composed"))).Result);
        Assert.Equal("applied: skipped when the run reaches it (protected: review)",
            Agree(core, Args("s-2", Overrides.Skip, "review", [Overrides.SkipProtected], ("outcome", "accepted"))).Result);
    }

    [Fact]
    public void An_abort_ends_the_run_and_says_whether_its_pull_requests_close()
    {
        var core = Started();

        Assert.Equal(new OverrideOutcome(null, "aborted; its open pull requests will be closed", true),
            Agree(core, Args("a-1", Overrides.Abort, null, null, ("close_pull_requests", "true"))));
        Assert.Equal(RunStatus.Aborted, core.Status);
        Assert.Equal("the run has ended (aborted)", Agree(core, Args("a-2", Overrides.Breakpoint, "ship", null)).Error);
    }

    [Fact]
    public void A_library_override_is_stored_by_the_shell_before_it_is_recorded()
    {
        var core = Started();
        var o = Args("l-1", Overrides.OverrideLibrary, null, [Overrides.OverrideLibraryProtected], ("item", "std:tasks/repair_failures"), ("with", "new text"));

        Assert.Equal(new StoresLibrary("std:tasks/repair_failures", "new text"), core.RouteOverride(o));
        Assert.NotNull(core.Override(o).Error);
        Assert.Empty(core.State.Overrides);

        Assert.Equal("applied: a -> b (protected: review)", core.Override(o, "applied: a -> b").Result);
        Assert.Equal(new Duplicate("applied: a -> b (protected: review)"), core.RouteOverride(o));
    }
}
