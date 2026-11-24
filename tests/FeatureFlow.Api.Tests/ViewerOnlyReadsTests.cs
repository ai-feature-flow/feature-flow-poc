using System.Net;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The build-console-b2 reads answer only a viewer of the run (review R6): a person with a role in another repository's scope
/// gets 404, as for a run that does not exist, and the body names nothing of the run but its id. A viewer in its scope reads it.
/// </summary>
public sealed class ViewerOnlyReadsTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly ApiWorld world = new(EventsTests.TwoTeams());

    public ValueTask DisposeAsync() => world.DisposeAsync();

    /// <summary>A pay run with a step started, read by a pay viewer and by a web viewer.</summary>
    async Task<((HttpStatusCode Status, string Body) Pay, (HttpStatusCode Status, string Body) Web)> ReadAsync(string path)
    {
        await world.RunAsync("r", "acme/pay");
        await world.Records.AppendAsync(new RecordBatch(Timeline: [new TimelineRecord("r", 1, "started", "build", "execution 1", DateTimeOffset.UtcNow)]), Ct);
        async Task<(HttpStatusCode, string)> As(string groups)
        {
            using var response = await world.As(groups, groups: groups).GetAsync(path, Ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        return (await As("pay-eng"), await As("web-eng"));
    }

    static void RefusedAsUnknown((HttpStatusCode Status, string Body) web)
    {
        Assert.Equal(HttpStatusCode.NotFound, web.Status);
        Assert.Contains("no run r", web.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("build", web.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_step_detail_is_404_to_a_person_who_does_not_view_the_run()
    {
        var (pay, web) = await ReadAsync("runs/r/steps/build");

        Assert.Equal(HttpStatusCode.OK, pay.Status);
        RefusedAsUnknown(web);
    }

    [Fact]
    public async Task The_review_report_is_404_to_a_person_who_does_not_view_the_run()
    {
        var (pay, web) = await ReadAsync("runs/r/review");

        Assert.Equal(HttpStatusCode.OK, pay.Status);
        RefusedAsUnknown(web);
    }

    [Fact]
    public async Task The_policy_in_force_is_404_to_a_person_who_does_not_view_the_run()
    {
        var (pay, web) = await ReadAsync("runs/r/policy");

        Assert.Equal(HttpStatusCode.OK, pay.Status);
        RefusedAsUnknown(web);
    }

    [Fact]
    public async Task The_staged_plan_is_404_to_a_person_who_does_not_view_the_run()
    {
        var (pay, web) = await ReadAsync("runs/r/plan?staged=build");

        // The run has no plan indexed, so a viewer is told that (204), not that the run does not exist.
        Assert.Equal(HttpStatusCode.NoContent, pay.Status);
        RefusedAsUnknown(web);
    }
}
