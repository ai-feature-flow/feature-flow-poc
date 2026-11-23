using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract for the console's B1 records: a step's counters, a pull request as last read, and a
/// registered repository are each kept by key with the last write winning, and read back whole; <c>recent_answers</c> gives
/// one actor's answers across runs, newest first; step states and pull requests change their run (<c>run_changed</c>),
/// repositories change none. A run's new fields read back. Each query answers for its own run only. A record store's test
/// project subclasses it; each test uses its own ids, so it can run against a shared database.
/// </summary>
public abstract class ConsoleRecordsContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];
    static readonly DateTimeOffset At = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    string Id(string name) => $"{unique}-{name}";

    Task<ProviderResult<Unit>> Append(RecordBatch batch) => Store.AppendAsync(batch, Ct);

    async Task<IReadOnlyList<T>> Query<T>(string query, params (string Key, string Value)[] parameters) =>
        Ok(await Store.QueryAsync<T>(query, parameters.ToDictionary(p => p.Key, p => p.Value), Ct));

    StepStateRecord Step(string run, string step, int executions, int used, long? allowed = 3, long granted = 0) =>
        new(Id(run), step, executions, used, allowed, granted, At.AddMinutes(executions));

    PullRequestRecord Pr(string run, string repository, int number, string state, string step = "open_pr", IReadOnlyList<PullRequestFile>? files = null, CheckStatus? checks = null) =>
        new(Id(run), repository, number, step,
            new PullRequestState(new PullRequestRef(new RepoRef(repository), number), state, "Add DELETE /widgets/{id} (#200)", "body", "ff/run/x", "abc123",
                "main", false, true, ["a-reviewer"], [], null, null, $"https://github.com/{repository}/pull/{number}", 10, 2, 1, "clean"),
            files, checks, At);

    [Fact]
    public async Task A_steps_counters_keep_their_last_write_per_step_and_answer_for_their_own_run()
    {
        Ok(await Append(new RecordBatch(StepStates: [Step("run", "review", 1, 1), Step("run", "implement", 1, 1), Step("other", "implement", 1, 1)])));
        Ok(await Append(new RecordBatch(StepStates: [Step("run", "implement", 2, 2, allowed: 5, granted: 2), Step("run", "approve", 1, 0, allowed: null)])));

        var states = await Query<StepStateRecord>(Queries.StepStates, ("run_id", Id("run")));

        Assert.Equal(
            [("approve", 1, 0, (long?)null, 0L), ("implement", 2, 2, 5L, 2L), ("review", 1, 1, 3L, 0L)],
            states.Select(s => (s.StepId, s.Executions, s.AttemptsUsed, s.AttemptsAllowed, s.AttemptsGranted)));
        Assert.Equal(At.AddMinutes(2), states[1].UpdatedAt);
    }

    [Fact]
    public async Task A_pull_request_keeps_its_last_read_per_repository_and_number_and_reads_back_whole()
    {
        var files = new List<PullRequestFile> { new("src/Widgets.cs", "modified", 10, 2) };
        var checks = new CheckStatus("abc123", [new CheckRun("build", CheckStates.Completed, Conclusions.Success, "ci.yml", "https://ci/1")]);
        Ok(await Append(new RecordBatch(PullRequests: [Pr("run", "acme/api", 7, PullRequestStates.Open), Pr("run", "acme/client", 3, PullRequestStates.Open),
            Pr("other", "acme/api", 7, PullRequestStates.Open)])));
        Ok(await Append(new RecordBatch(PullRequests: [Pr("run", "acme/api", 7, PullRequestStates.Merged, "merge", files, checks)])));

        var prs = await Query<PullRequestRecord>(Queries.PullRequests, ("run_id", Id("run")));

        Assert.Equal([("acme/api", 7, PullRequestStates.Merged, "merge"), ("acme/client", 3, PullRequestStates.Open, "open_pr")],
            prs.Select(p => (p.Repository, p.Number, p.State.State, p.StepId)));
        var merged = prs[0];
        Assert.Equal((10, 2, 1, "clean"), (merged.State.Additions, merged.State.Deletions, merged.State.ChangedFiles, merged.State.MergeableState));
        Assert.Equal(("acme/api", 7), (merged.State.PullRequest.Repo.FullName, merged.State.PullRequest.Number));
        Assert.Equal(files, merged.Files);
        Assert.Equal(("abc123", "build", Conclusions.Success), (merged.Checks!.Revision, merged.Checks.Checks[0].Name, merged.Checks.Checks[0].Conclusion));
        Assert.Null(prs[1].Files);
        Assert.Null(prs[1].Checks);
    }

    [Fact]
    public async Task A_repository_keeps_its_last_registration_by_name_and_they_list_by_name()
    {
        RepositoryRecord R(string name, string by) => new($"{unique}/{name}", "github", $"https://github.com/{unique}/{name}", "main", by, At);
        Ok(await Append(new RecordBatch(Repositories: [R("web", "a-person"), R("api", "a-person")])));
        Ok(await Append(new RecordBatch(Repositories: [R("api", "another-person")])));

        var mine = (await Query<RepositoryRecord>(Queries.Repositories)).Where(r => r.Name.StartsWith(unique + "/", StringComparison.Ordinal)).ToList();

        Assert.Equal([($"{unique}/api", "another-person"), ($"{unique}/web", "a-person")], mine.Select(r => (r.Name, r.RegisteredBy)));
        Assert.Equal(("github", $"https://github.com/{unique}/web", "main"), (mine[1].Host, mine[1].Url, mine[1].DefaultBranch));
    }

    [Fact]
    public async Task Recent_answers_are_one_actors_answers_across_runs_newest_first_up_to_the_limit()
    {
        var me = Id("me");
        AnswerRecord A(string run, string wait, string actor, int minutes) => new(Id(run), wait, actor, "{}", "sent", At.AddMinutes(minutes), "because");
        Ok(await Append(new RecordBatch(Answers: [A("r1", "w1", me, 1), A("r1", "w2", Id("someone"), 2)])));
        Ok(await Append(new RecordBatch(Answers: [A("r2", "w1", me, 3)])));
        Ok(await Append(new RecordBatch(Answers: [A("r1", "w3", me, 4)])));

        var recent = await Query<AnswerRecord>(Queries.RecentAnswers, ("actor", me), ("limit", "10"));

        Assert.Equal([(Id("r1"), "w3"), (Id("r2"), "w1"), (Id("r1"), "w1")], recent.Select(a => (a.RunId, a.WaitId)));
        Assert.Equal("because", recent[0].Reason);
        Assert.Equal([(Id("r1"), "w3"), (Id("r2"), "w1")], (await Query<AnswerRecord>(Queries.RecentAnswers, ("actor", me), ("limit", "2"))).Select(a => (a.RunId, a.WaitId)));
    }

    [Fact]
    public async Task A_runs_tracker_issue_level_parent_step_and_start_read_back()
    {
        var run = new RunRecord(Id("child"), Id("parent"), "std:templates/ticket_delivery", "running", "a-person", ["acme/api"], false, At, null,
            "ticket", "deliver_tickets", "ACME-12", "Add DELETE /widgets/{id}", "https://acme.atlassian.net/browse/ACME-12", At.AddMinutes(-5));
        Ok(await Append(new RecordBatch(Runs: [run])));

        var read = Assert.Single(await Query<RunRecord>(Queries.Run, ("run_id", Id("child"))));

        Assert.Equal(run with { Repositories = read.Repositories }, read);
        Assert.Equal(["acme/api"], read.Repositories);
    }

    [Fact]
    public async Task Step_states_and_pull_requests_change_their_run_and_repositories_change_none()
    {
        var before = (await Query<ChangeRecord>(Queries.LatestChange)).SingleOrDefault()?.Sequence ?? 0;

        Ok(await Append(new RecordBatch(StepStates: [Step("a", "implement", 1, 1)])));
        Ok(await Append(new RecordBatch(PullRequests: [Pr("b", "acme/api", 1, PullRequestStates.Open)])));
        Ok(await Append(new RecordBatch(Repositories: [new RepositoryRecord($"{unique}/api", "github", null, null, "a-person", At)])));

        var changes = (await Query<ChangeRecord>(Queries.Changes, ("after", before.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Where(c => c.RunId.StartsWith(unique, StringComparison.Ordinal));
        Assert.Equal([(Id("a"), ChangeKinds.RunChanged), (Id("b"), ChangeKinds.RunChanged)], changes.Select(c => (c.RunId, c.Kind)));
    }
}
