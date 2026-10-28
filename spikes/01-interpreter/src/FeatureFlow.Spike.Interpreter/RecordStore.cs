using System.Text.Json;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// The record store. docs/01 2.1: the artifact store plus the record store, and
/// git is not a store. Review 12 A1: step-run detail, patch records and history
/// samples go here rather than into carried state, so the continue-as-new input
/// stays bounded by plan size.
///
/// Spike scope: an append-only JSON file. The point is the split between what
/// the workflow carries and what it records, not the storage technology.
/// </summary>
public sealed class RecordStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string path;
    private readonly List<RecordEntry> entries = new();

    private static readonly Dictionary<string, RecordStore> ByRun =
        new(StringComparer.Ordinal);

    public RecordStore(string path)
    {
        this.path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>One store per run, under the spike results directory.</summary>
    public static RecordStore ForRun(string runId)
    {
        lock (ByRun)
        {
            if (ByRun.TryGetValue(runId, out var existing))
                return existing;

            var dir = Environment.GetEnvironmentVariable("FF_RECORD_STORE")
                      ?? Path.Combine(Path.GetTempPath(), "ff-record-store");
            var store = new RecordStore(Path.Combine(dir, $"{runId}.json"));
            ByRun[runId] = store;
            return store;
        }
    }

    /// <summary>
    /// Read a run's records straight from disk. The worker writes them in a
    /// different process, so an in-memory store held by the harness would not
    /// see them.
    /// </summary>
    public static List<RecordEntry> Read(string runId)
    {
        var dir = Environment.GetEnvironmentVariable("FF_RECORD_STORE")
                  ?? Path.Combine(Path.GetTempPath(), "ff-record-store");
        var path = Path.Combine(dir, $"{runId}.json");
        if (!File.Exists(path))
            return new List<RecordEntry>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RecordEntry>>(json, Json)
                   ?? new List<RecordEntry>();
        }
        catch
        {
            return new List<RecordEntry>();
        }
    }

    public void RecordStepRun(StepRunDetail detail)
    {
        entries.Add(new RecordEntry("step_run", DateTimeOffset.UtcNow, detail));
        Flush();
    }

    public void RecordPatch(PatchRecord record)
    {
        entries.Add(new RecordEntry("patch", DateTimeOffset.UtcNow, record));
        Flush();
    }

    public void RecordHistorySample(HistorySample sample)
    {
        entries.Add(new RecordEntry("history_sample", DateTimeOffset.UtcNow, sample));
        Flush();
    }

    public IReadOnlyList<RecordEntry> Entries => entries;

    private void Flush()
    {
        var json = JsonSerializer.Serialize(entries, Json);
        File.WriteAllText(path, json);
    }

    public sealed class RecordEntry
    {
        public RecordEntry()
        {
        }

        public RecordEntry(string kind, DateTimeOffset at, object payload)
        {
            Kind = kind;
            At = at;
            Payload = payload;
        }

        public string Kind { get; set; } = "";

        public DateTimeOffset At { get; set; }

        /// <summary>
        /// Deserialised as a JsonElement, because the payload type varies by
        /// kind. Callers cast with PayloadAs.
        /// </summary>
        public object? Payload { get; set; }

        public T? PayloadAs<T>() where T : class
        {
            if (Payload is T typed)
                return typed;
            if (Payload is System.Text.Json.JsonElement e)
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(e.GetRawText(), Json);
            }
            return null;
        }
    }
}
