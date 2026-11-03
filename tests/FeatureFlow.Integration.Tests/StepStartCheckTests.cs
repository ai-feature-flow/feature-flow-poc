namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Step-start check 32 (docs/09 section 2): a step whose consumed artifact no step has produced, and which it does not
/// mark optional, does not start; the run raises an intervention instead.
/// </summary>
[Trait("Category", "Integration")]
public class StepStartCheckTests
{
    static readonly string[] Library = [Path.Combine(Harness.TestLibraries, "m5")];

    [Fact]
    public async Task Check_32_a_step_whose_consumed_artifact_was_never_produced_raises_an_intervention()
    {
        await using var h = await Harness.StartAsync("check32", """{ "consumes_notes:write": skipped }""", libraries: Library);
        var id = h.Start("m5:templates/consumes_notes");

        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(1));

        Assert.NotNull(waitId);
        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single()!;
        Assert.Equal("read", wait["stepId"]!.ToString());
        Assert.Contains("check 32", wait["reason"]!.ToString(), StringComparison.Ordinal);
        // The core marks the step started; the check stops it before its activity runs, so it never completes.
        Assert.DoesNotContain((await h.ShowAsync(id))["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == "read");
    }

    [Fact]
    public async Task Check_32_control_the_same_plan_with_the_artifact_produced_completes()
    {
        await using var h = await Harness.StartAsync("check32ok", """{ "consumes_notes:write": written }""", libraries: Library);
        var id = h.Start("m5:templates/consumes_notes");

        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");

        Assert.Equal((0, $"run {id} completed"), (exit, end.Trim()));
    }
}
