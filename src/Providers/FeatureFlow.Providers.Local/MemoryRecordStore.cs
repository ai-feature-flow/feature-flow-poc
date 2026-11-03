using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A record store in memory for M6's tests, holding runs and what M6 adds: the findings ledger and the artifact index
/// (docs/02 section 5). Finding records are kept by run and finding id, the last write winning; finding events and
/// artifacts are appended once per key. Other records are not kept here: the SQLite store holds them.
/// </summary>
public sealed class InMemoryRecordStore : IRecordStoreProvider
{
    readonly Lock gate = new();
    readonly Dictionary<string, RunRecord> runs = new(StringComparer.Ordinal);
    readonly Dictionary<(string Run, string Finding), FindingRecord> findings = [];
    readonly List<FindingEventRecord> events = [];
    readonly List<ArtifactRecord> artifacts = [];

    public ProviderDescription Describe() => new("record_store", "memory", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
    {
        if (records.Waits is { Count: > 0 } || records.Timeline is { Count: > 0 } || records.Answers is { Count: > 0 } || records.Locks is { Count: > 0 }
            || records.AgentSteps is { Count: > 0 } || records.Reports is { Count: > 0 } || records.Questions is { Count: > 0 } || records.AgentEvents is { Count: > 0 })
            throw new NotSupportedException("the in-memory record store keeps only runs, the findings ledger and the artifact index");
        lock (gate)
        {
            foreach (var run in records.Runs ?? [])
                runs[run.RunId] = run;
            foreach (var finding in records.Findings ?? [])
                findings[(finding.RunId, finding.FindingId)] = finding;
            foreach (var e in records.FindingEvents ?? [])
                if (!events.Any(x => x.RunId == e.RunId && x.FindingId == e.FindingId && x.Key == e.Key))
                    events.Add(e);
            foreach (var a in records.Artifacts ?? [])
                if (!artifacts.Any(x => x.RunId == a.RunId && x.StepId == a.StepId && x.Execution == a.Execution && x.Name == a.Name))
                    artifacts.Add(a);
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
