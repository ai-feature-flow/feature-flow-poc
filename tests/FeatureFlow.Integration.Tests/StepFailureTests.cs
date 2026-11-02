using System.Text.Json.Nodes;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A step whose activity fails with a non-retryable error reaches the core as a work error. Here plan.expand
/// reads a work list with no items: each execution fails, the allowance runs out and the step escalates, rather
/// than staying in flight for ever.
/// </summary>
[Trait("Category", "Integration")]
public class StepFailureTests
{
    [Fact]
    public async Task A_non_retryable_activity_failure_fails_the_attempt_and_escalates()
    {
        await using var h = await Harness.StartAsync("expand-fail", """
            { "epic_delivery:tickets.plan": [{ outcome: planned, outputs: { needs_integration: false }, artifacts: { work_list: { schema: 1, level: ticket } } }] }
            """, autoApprove: true);
        var id = h.Start("std:templates/epic_delivery", "--param", "epic_name=billing", "--repo", "acme/api");
        var approval = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(1));
        Assert.Equal(0, h.Cli("answer", id, approval!, "approved").Exit);

        var deadline = DateTime.UtcNow.AddMinutes(1);
        JsonNode? wait = null;
        while (wait is null && DateTime.UtcNow < deadline)
        {
            wait = (await h.ShowAsync(id))["waits"]!.AsArray().FirstOrDefault(w => w!["stepId"]!.ToString() == "tickets.expand");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        var view = await h.ShowAsync(id);
        Assert.True(wait is not null, "tickets.expand never escalated: " + view.ToJsonString());
        Assert.Equal("intervention", wait!["kind"]!.ToString());
        var failures = view["timeline"]!.AsArray()
            .Where(e => e!["kind"]!.ToString() == "attempt_failed" && e["stepId"]!.ToString() == "tickets.expand").ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, f => Assert.Contains("holds no list of items", f!["detail"]!.ToString()));
    }
}
