using System.Text.Json.Nodes;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A step whose activity fails with a non-retryable error reaches the core as a work error. Here plan.expand
/// reads a list artifact with no items (its type has no schema, so the planner may report one): each execution
/// fails, the allowance runs out and the step escalates, rather than staying in flight for ever.
/// </summary>
[Trait("Category", "Integration")]
public class StepFailureTests
{
    [Fact]
    public async Task A_non_retryable_activity_failure_fails_the_attempt_and_escalates()
    {
        await using var h = await Harness.StartAsync("expand-fail", """
            { "expand_loose:plan": { outcome: planned, artifacts: { work_list: { schema: 1, level: ticket } } } }
            """, libraries: [Path.Combine(Harness.TestLibraries, "m5")]);
        var id = h.Start("m5:templates/expand_loose", "--repo", Harness.RepoName);

        var deadline = DateTime.UtcNow.AddMinutes(1);
        JsonNode? wait = null;
        while (wait is null && DateTime.UtcNow < deadline)
        {
            wait = (await h.ShowAsync(id))["waits"]!.AsArray().FirstOrDefault(w => w!["step_id"]!.ToString() == "expand");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        var view = await h.ShowAsync(id);
        Assert.True(wait is not null, "expand never escalated: " + view.ToJsonString());
        Assert.Equal("intervention", wait!["kind"]!.ToString());
        var failures = view["timeline"]!.AsArray()
            .Where(e => e!["kind"]!.ToString() == "attempt_failed" && e["step_id"]!.ToString() == "expand").ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, f => Assert.Contains("holds no list of items", f!["detail"]!.ToString()));
    }
}
