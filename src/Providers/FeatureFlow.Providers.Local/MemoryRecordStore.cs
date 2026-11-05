using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A record store in memory for tests, holding runs and what M6 and M7 add: the findings ledger and the artifact index
/// (docs/02 section 5); overrides, notification de-duplication and sessions. Finding records, sessions and session checks
/// are kept by key, the last write winning; finding events and artifacts are appended once per key, a notification is kept
/// once per key with the first write winning, and overrides are appended. Other records are not kept here: the SQLite
/// store holds them.
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

    public ProviderDescription Describe() => new("record_store", "memory", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
    {
        if (records.Waits is { Count: > 0 } || records.Timeline is { Count: > 0 } || records.Answers is { Count: > 0 } || records.Locks is { Count: > 0 }
            || records.AgentSteps is { Count: > 0 } || records.Reports is { Count: > 0 } || records.Questions is { Count: > 0 } || records.AgentEvents is { Count: > 0 })
            throw new NotSupportedException("the in-memory record store keeps only runs, the findings ledger, the artifact index, overrides, notifications and sessions");
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
}
