using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A record store in memory for tests, holding runs and what M6 and M7 add: the findings ledger and the artifact index
/// (docs/02 section 5); overrides, notification de-duplication and sessions; and what the console's B1 adds: answers,
/// step states, pull requests and repositories; and an agent step's live activity. Finding records, sessions, session checks, step states, pull requests and
/// repositories are kept by key, the last write winning; finding events, artifacts and activity are appended once per key, a
/// notification is kept once per key with the first write winning, and overrides and answers are appended. Other records
/// are not kept here: the SQLite store holds them. The changes each batch makes are numbered and kept as the other stores
/// keep them (<see cref="Queries.Changes"/>).
/// </summary>
public sealed class InMemoryRecordStore : IRecordStoreProvider
{
    readonly Lock gate = new();
    readonly Dictionary<string, RunRecord> runs = new(StringComparer.Ordinal);
    readonly Dictionary<(string Run, string Finding), FindingRecord> findings = [];
    readonly List<FindingEventRecord> events = [];
    readonly List<ArtifactRecord> artifacts = [];
    readonly List<OverrideRecord> overrides = [];
    readonly List<NotificationRecord> notifications = [];
    readonly Dictionary<string, SessionRecord> sessions = new(StringComparer.Ordinal);
    readonly Dictionary<(string Session, string Check), SessionCheckRecord> checks = [];
    readonly List<AnswerRecord> answers = [];
    readonly Dictionary<(string Run, string Step), StepStateRecord> stepStates = [];
    readonly Dictionary<(string Run, string Repository, int Number), PullRequestRecord> pullRequests = [];
    readonly Dictionary<string, RepositoryRecord> repositories = new(StringComparer.Ordinal);
    readonly Dictionary<(string Run, string Step, int Execution, int Session, int Sequence), ActivityRecord> activity = [];

    public ProviderDescription Describe() => new("record_store", "memory", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
    {
        if (records.Waits is { Count: > 0 } || records.Timeline is { Count: > 0 } || records.Locks is { Count: > 0 }
            || records.AgentSteps is { Count: > 0 } || records.Reports is { Count: > 0 } || records.Questions is { Count: > 0 } || records.AgentEvents is { Count: > 0 })
            throw new NotSupportedException("the in-memory record store keeps only runs, answers, the findings ledger, the artifact index, overrides, notifications, sessions, step states, pull requests, repositories and activity");
        lock (gate)
        {
            foreach (var run in records.Runs ?? [])
                if (!runs.TryGetValue(run.RunId, out var known) || known.UpdatedAt <= run.UpdatedAt)
                    runs[run.RunId] = run;
            foreach (var finding in records.Findings ?? [])
                findings[(finding.RunId, finding.FindingId)] = finding;
            foreach (var e in records.FindingEvents ?? [])
                if (!events.Any(x => x.RunId == e.RunId && x.FindingId == e.FindingId && x.Key == e.Key))
                    events.Add(e);
            foreach (var a in records.Artifacts ?? [])
                if (!artifacts.Any(x => x.RunId == a.RunId && x.StepId == a.StepId && x.Execution == a.Execution && x.Name == a.Name))
                    artifacts.Add(a);
            overrides.AddRange(records.Overrides ?? []);
            foreach (var n in records.Notifications ?? [])
                if (!notifications.Any(x => x.Key == n.Key))
                    notifications.Add(n);
            foreach (var session in records.Sessions ?? [])
                sessions[session.SessionId] = session;
            foreach (var check in records.SessionChecks ?? [])
                checks[(check.SessionId, check.CheckId)] = check;
            answers.AddRange(records.Answers ?? []);
            foreach (var s in records.StepStates ?? [])
                stepStates[(s.RunId, s.StepId)] = s;
            foreach (var p in records.PullRequests ?? [])
                pullRequests[(p.RunId, p.Repository, p.Number)] = p;
            foreach (var r in records.Repositories ?? [])
                repositories[r.Name] = r;
            foreach (var a in records.Activity ?? [])
                activity.TryAdd((a.RunId, a.StepId, a.Execution, a.Session, a.Sequence), a);
            AppendChanges(records);
        }

        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        string P(string key) => parameters.TryGetValue(key, out var value) ? value : throw new ArgumentException($"query {queryName} needs {key}");
        lock (gate)
        {
            IEnumerable<object> rows = queryName switch
            {
                Queries.Changes => changes.Where(c => c.Sequence > SqliteRecordStore.After(parameters)).Take(SqliteRecordStore.Limit(parameters)),
                Queries.LatestChange => changes.TakeLast(1),
                Queries.Run => runs.Values.Where(r => r.RunId == P("run_id")),
                Queries.Runs when parameters.ContainsKey("parent_run_id") => runs.Values.Where(r => r.ParentRunId == P("parent_run_id")).OrderBy(r => r.RunId, StringComparer.Ordinal),
                Queries.Runs => runs.Values.OrderByDescending(r => r.UpdatedAt),
                Queries.Findings => findings.Values.Where(f => Tree(P("run_id")).Contains(f.RunId))
                    .OrderBy(f => f.RunId, StringComparer.Ordinal).ThenBy(f => f.FindingId, StringComparer.Ordinal),
                Queries.FindingEvents => events.Where(e => e.RunId == P("run_id") && e.FindingId == P("finding_id")),
                Queries.Artifacts => artifacts.Where(a => Tree(P("run_id")).Contains(a.RunId)),
                Queries.Overrides => overrides.Where(o => o.RunId == P("run_id")),
                Queries.Notification => notifications.Where(n => n.Key == P("key")),
                Queries.Notifications => notifications.Where(n => n.RunId == P("run_id")),
                Queries.Session => sessions.Values.Where(s => s.SessionId == P("session_id")),
                Queries.Sessions => sessions.Values.Where(s => s.RunId == P("run_id")).OrderBy(s => s.SessionId, StringComparer.Ordinal),
                Queries.SessionChecks => checks.Values.Where(c => c.SessionId == P("session_id")).OrderBy(c => c.CheckId, StringComparer.Ordinal),
                Queries.Answers => answers.Where(a => a.RunId == P("run_id")),
                Queries.RecentAnswers => answers.Where(a => a.Actor == P("actor")).Reverse().Take(SqliteRecordStore.Limit(parameters)),
                Queries.StepStates => stepStates.Values.Where(s => s.RunId == P("run_id")).OrderBy(s => s.StepId, StringComparer.Ordinal),
                Queries.PullRequests => pullRequests.Values.Where(p => p.RunId == P("run_id"))
                    .OrderBy(p => p.Repository, StringComparer.Ordinal).ThenBy(p => p.Number),
                Queries.Repositories => repositories.Values.OrderBy(r => r.Name, StringComparer.Ordinal),
                Queries.Activity => activity.Values
                    .Where(a => a.RunId == P("run_id") && a.StepId == P("step_id")
                        && (!parameters.TryGetValue("execution", out var execution) || a.Execution == int.Parse(execution, System.Globalization.CultureInfo.InvariantCulture)))
                    .OrderBy(a => a.Execution).ThenBy(a => a.Session).ThenBy(a => a.Sequence),
                // Waits are not kept here, so no wait is open.
                Queries.OpenWaits => Enumerable.Empty<OpenWaitRecord>(),
                _ => throw new NotSupportedException($"the in-memory record store does not answer {queryName}"),
            };
            return Task.FromResult(ProviderResult<IReadOnlyList<T>>.Success(rows.Cast<T>().ToList()));
        }
    }

    /// <summary>A run and every run below it, by parent links.</summary>
    HashSet<string> Tree(string runId)
    {
        var tree = new HashSet<string>(StringComparer.Ordinal) { runId };
        for (var added = true; added;)
        {
            added = false;
            foreach (var run in runs.Values)
                if (run.ParentRunId is { } parent && tree.Contains(parent) && tree.Add(run.RunId))
                    added = true;
        }

        return tree;
    }

    // Changes (docs/06 section 3.12): numbered in the order appended, under the store's lock.
    readonly List<ChangeRecord> changes = [];

    void AppendChanges(RecordBatch records)
    {
        foreach (var (runId, kind, waitId, stepId) in ChangeKinds.Of(records))
            changes.Add(new ChangeRecord(changes.Count + 1, runId, kind, waitId, DateTimeOffset.UtcNow, stepId));
    }
}
