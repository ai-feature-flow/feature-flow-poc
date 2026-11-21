namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Step-start check 32 (docs/09 section 2): a step whose consumed artifact no step has produced, and which it does not
/// mark optional, does not start; the run raises an intervention instead. And run start's warnings on the timeline.
/// </summary>
[Trait("Category", "Integration")]
public class StepStartCheckTests
{
    static readonly string[] Library = [Path.Combine(Harness.TestLibraries, "m5")];

    static readonly string[] Repo = ["--repo", Harness.RepoName];

    [Fact]
    public async Task Check_32_a_step_whose_consumed_artifact_was_never_produced_raises_an_intervention()
    {
        await using var h = await Harness.StartAsync("check32", """{ "consumes_notes:write": skipped }""", libraries: Library);
        var id = h.Start("m5:templates/consumes_notes", Repo);

        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(1));

        Assert.NotNull(waitId);
        var view = await h.ShowAsync(id);
        var wait = view["waits"]!.AsArray().Single()!;
        Assert.True(wait["step_id"]!.ToString() == "read", view.ToJsonString());
        Assert.Contains("check 32", wait["prompt"]!.ToString(), StringComparison.Ordinal);
        // The core marks the step started; the check stops it before its activity runs, so it never completes.
        Assert.DoesNotContain((await h.ShowAsync(id))["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["step_id"]!.ToString() == "read");
    }

    [Fact]
    public async Task Check_32_control_the_same_plan_with_the_artifact_produced_completes()
    {
        await using var h = await Harness.StartAsync("check32ok", """{ "consumes_notes:write": written }""", libraries: Library);
        var id = h.Start("m5:templates/consumes_notes", Repo);

        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");

        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
    }

    /// <summary>Run start's warnings (here check 34, a parameter never read) reach the run's timeline (LOG 142).</summary>
    [Fact]
    public async Task Run_start_warnings_reach_the_timeline()
    {
        await using var h = await Harness.StartAsync("start-warnings", libraries: Library);
        var id = h.Start("m5:templates/warns", Repo);

        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");

        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        Assert.Contains((await h.ShowAsync(id))["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "warning"
            && e["detail"]!.ToString().Contains("check 34", StringComparison.Ordinal)
            && e["detail"]!.ToString().Contains("'unused'", StringComparison.Ordinal));
    }
}
