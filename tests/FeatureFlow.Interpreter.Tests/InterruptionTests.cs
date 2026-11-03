using System.Text.Json;
using FeatureFlow.Model;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// LOG 139: a running step returned to pending without a new execution (<see cref="StepInterrupted"/>), as the shell
/// does to a wait at a hand-over. It starts the same execution again after the hand-over, using no attempt (F4).
/// </summary>
public class InterruptionTests
{
    /// <summary>A wait with one attempt: a second execution would be refused, so a resume that counted would show.</summary>
    const string Waiting = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: waiting, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: checks
            action: ci.checks
            limits: { attempts: 1 }
            routes: { passed: { end: completed } }
        """;

    static readonly Probe Lib = new("interruption", ("waiting", Waiting));

    [Fact]
    public void An_interrupted_wait_restarts_its_own_execution_after_a_hand_over_and_uses_no_attempt()
    {
        var core = Lib.Start("waiting");
        Assert.Equal([new StartStep("checks", 1)], core.Advance());

        Assert.True(core.Apply(new StepInterrupted("checks", "handed over")).Accepted);
        Assert.Empty(core.State.InFlight);
        Assert.Empty(core.Flush());

        // The hand-over: the state is carried as the shell carries it, and the next run advances.
        var json = JsonSerializer.Serialize(core.Snapshot());
        core = RunCore.Resume(Lib.Libraries, Probe.Ref("waiting"), JsonSerializer.Deserialize<CarriedCore>(json)!);
        Assert.Equal([new StartStep("checks", 1)], core.Advance());

        var record = core.State.Steps["checks"];
        Assert.Equal((1, 1, 1L), (record.Executions, record.AttemptsUsed, core.State.RunAttempts));
        Assert.DoesNotContain(core.State.Trace, t => t.Kind == "exhausted");
        Assert.Contains(core.State.Trace, t => t is { Kind: "resumed", StepId: "checks", Detail: "execution 1" });

        Assert.True(core.Apply(new StepCompleted("checks", "passed", new Dictionary<string, object?> { ["skipped"] = 0L })).Accepted);
        core.Advance();
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void Only_a_running_step_can_be_interrupted()
    {
        var core = Lib.Start("waiting");
        Assert.False(core.Apply(new StepInterrupted("checks", "handed over")).Accepted);
        core.Advance();
        Assert.True(core.Apply(new StepInterrupted("checks", "handed over")).Accepted);
        Assert.False(core.Apply(new StepInterrupted("checks", "twice")).Accepted);
    }
}
