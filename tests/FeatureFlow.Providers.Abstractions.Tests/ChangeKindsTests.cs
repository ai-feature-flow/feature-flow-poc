namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>The changes a record batch makes (docs/06 section 3.12): what <c>GET /events</c> reports.</summary>
public sealed class ChangeKindsTests
{
    static readonly DateTimeOffset At = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Findings_finding_events_step_states_and_pull_requests_change_their_run_once_each_and_repositories_change_none()
    {
        var finding = new FindingRecord("r1", "f1", "fp", null!, "agent", null, "open", null, null, 0, 0, 0, false, At);
        var e = new FindingEventRecord("r2", "f1", "k", FindingEventKinds.Raised, "open", "review", null, "review", null, null, At);
        var step = new StepStateRecord("r3", "implement", 1, 1, 3, 0, At);
        var pr = new PullRequestRecord("r4", "acme/api", 1, "open_pr",
            new PullRequestState(new PullRequestRef(new RepoRef("acme/api"), 1), "open", "t", "b", "h", "rev", "main", false, null, [], [], null, null, null), null, null, At);
        var repository = new RepositoryRecord("acme/api", "github", null, null, "a-person", At);

        var changes = ChangeKinds.Of(new RecordBatch(Findings: [finding, finding with { FindingId = "f2" }], FindingEvents: [e], StepStates: [step],
            PullRequests: [pr], Repositories: [repository]));

        Assert.Equal(
            [("r1", ChangeKinds.RunChanged, (string?)null), ("r2", ChangeKinds.RunChanged, null), ("r3", ChangeKinds.RunChanged, null), ("r4", ChangeKinds.RunChanged, null)],
            changes);
        Assert.Empty(ChangeKinds.Of(new RecordBatch(Repositories: [repository])));
    }
}
