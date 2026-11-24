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
            [("r1", ChangeKinds.RunChanged, (string?)null, (string?)null), ("r2", ChangeKinds.RunChanged, null, null), ("r3", ChangeKinds.RunChanged, null, null),
                ("r4", ChangeKinds.RunChanged, null, null)],
            changes);
        Assert.Empty(ChangeKinds.Of(new RecordBatch(Repositories: [repository])));
    }

    [Fact]
    public void Activity_changes_each_distinct_step_once_after_the_runs_and_waits_and_changes_no_run_itself()
    {
        ActivityRecord A(string run, string step, int sequence) => new(run, step, 1, 1, sequence, ActivityKinds.Message, null, null, "text", null, false, At);
        var wait = new WaitRecord("r2", "gate-1", "gate", "approve", "t", "Approve?", ["approved"], true, At);

        var changes = ChangeKinds.Of(new RecordBatch(Waits: [wait], Activity: [A("r1", "implement", 1), A("r1", "implement", 2), A("r1", "review", 1), A("r2", "implement", 1)]));

        Assert.Equal(
            [("r2", ChangeKinds.WaitOpened, (string?)"gate-1", (string?)null), ("r1", ChangeKinds.StepChanged, null, "implement"), ("r1", ChangeKinds.StepChanged, null, "review"),
                ("r2", ChangeKinds.StepChanged, null, "implement")],
            changes);
    }
}
