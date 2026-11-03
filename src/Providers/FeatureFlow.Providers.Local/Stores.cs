using System.Security.Cryptography;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// The artifact store on the local filesystem (docs/06 section 3.12; the solo profile, docs/07 section 6).
/// Immutable and content-addressed: a blob is stored once under the SHA-256 of its bytes.
/// </summary>
public sealed class FileSystemArtifactStore(string root) : IArtifactStoreProvider
{
    public string Root { get; } = root;

    public ProviderDescription Describe() => new("artifact_store", "filesystem", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    public async Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, $".put-{Guid.NewGuid():N}");
        string hex;
        await using (var file = File.Create(temp))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            hex = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        var target = PathOf(hex);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
            File.Delete(temp);
        else
            File.Move(temp, target, overwrite: true);
        return ProviderResult<ArtifactRef>.Success(new ArtifactRef("sha256:" + hex));
    }

    public Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken cancellationToken)
    {
        if (Hex(reference) is not { } hex || !File.Exists(PathOf(hex)))
            return Task.FromResult(ProviderResult<Stream>.Failure("not_found", $"no artifact {reference.Id}"));
        return Task.FromResult(ProviderResult<Stream>.Success(File.OpenRead(PathOf(hex))));
    }

    public Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken cancellationToken) =>
        Task.FromResult(Hex(reference) is { } hex && File.Exists(PathOf(hex))
            ? ProviderResult<Uri>.Success(new Uri(PathOf(hex)))
            : ProviderResult<Uri>.Failure("not_found", $"no artifact {reference.Id}"));

    string PathOf(string hex) => Path.Combine(Root, hex[..2], hex);

    static string? Hex(ArtifactRef reference) =>
        reference.Id.StartsWith("sha256:", StringComparison.Ordinal) && reference.Id.Length == 71 && reference.Id[7..].All(Uri.IsHexDigit)
            ? reference.Id[7..]
            : null;
}

/// <summary>
/// Secrets from a JSON file of names and values, outside the repository (the solo profile). A reference is
/// <c>secrets/&lt;name&gt;</c> or the bare name. Values are never logged. <paramref name="fromEnvironment"/> names
/// secrets read from the process environment instead, such as a subscription token an operator exports; only
/// the names listed are read.
/// </summary>
public sealed class FileSecrets(string path, IReadOnlyList<string>? fromEnvironment = null) : ISecretsProvider
{
    public ProviderDescription Describe() => new("secrets", "file", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(path)
            ? ProviderResult<Unit>.Success(default)
            : ProviderResult<Unit>.Failure("not_found", $"the secrets file {path} does not exist"));

    public async Task<ProviderResult<string>> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var name = reference.StartsWith("secrets/", StringComparison.Ordinal) ? reference["secrets/".Length..] : reference;
        if (fromEnvironment?.Contains(name) == true)
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnv
                ? ProviderResult<string>.Success(fromEnv)
                : ProviderResult<string>.Failure("not_found", $"the environment variable {name} is not set");
        if (!File.Exists(path))
            return ProviderResult<string>.Failure("not_found", $"the secrets file {path} does not exist");
        await using var file = File.OpenRead(path);
        var values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(file, cancellationToken: cancellationToken);
        return values?.TryGetValue(name, out var value) == true
            ? ProviderResult<string>.Success(value)
            : ProviderResult<string>.Failure("not_found", $"no secret named {name}");
    }
}

/// <summary>
/// The record store in SQLite (docs/06 section 3.12; the solo profile). Runs and waits are kept by key, the
/// last write winning; timeline entries, answers and locks are appended. Timeline entries are kept once per
/// run and sequence number, so a retried append adds nothing.
/// </summary>
public sealed class SqliteRecordStore : IRecordStoreProvider
{
    readonly string connectionString;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SqliteRecordStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS runs (org_id TEXT NOT NULL, run_id TEXT NOT NULL, parent_run_id TEXT, updated_at TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id));
            CREATE TABLE IF NOT EXISTS waits (org_id TEXT NOT NULL, run_id TEXT NOT NULL, wait_id TEXT NOT NULL, open INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, wait_id));
            CREATE TABLE IF NOT EXISTS timeline (org_id TEXT NOT NULL, run_id TEXT NOT NULL, sequence INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, sequence));
            CREATE TABLE IF NOT EXISTS answers (org_id TEXT NOT NULL, run_id TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS locks (org_id TEXT NOT NULL, run_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id));
            CREATE TABLE IF NOT EXISTS agent_keyed (org_id TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, step_key TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, kind, key));
            CREATE TABLE IF NOT EXISTS agent_log (org_id TEXT NOT NULL, kind TEXT NOT NULL, step_key TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS artifacts (org_id TEXT NOT NULL, run_id TEXT NOT NULL, step_id TEXT NOT NULL, execution INTEGER NOT NULL, name TEXT NOT NULL,
                id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL, UNIQUE (org_id, run_id, step_id, execution, name));
            """);
        CreateLedger(connection);
    }

    /// <summary>One organisation per installation; the key is there from the start (docs/02 section 5).</summary>
    const string Org = "default";

    public ProviderDescription Describe() => new("record_store", "sqlite", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        using var connection = Open();
        Execute(connection, "SELECT 1");
        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var run in records.Runs ?? [])
            Execute(connection, "INSERT OR REPLACE INTO runs VALUES ($o, $r, $p, $u, $b)",
                ("$o", Org), ("$r", run.RunId), ("$p", run.ParentRunId), ("$u", run.UpdatedAt.ToString("O")), ("$b", JsonSerializer.Serialize(run, Json)));
        foreach (var wait in records.Waits ?? [])
        {
            // A record that closes a wait keeps what the opening record said about it.
            if (!wait.Open)
                Execute(connection, "UPDATE waits SET open = 0, body = json_set(body, '$.open', json('false'), '$.updatedAt', $u) WHERE org_id = $o AND run_id = $r AND wait_id = $w",
                    ("$o", Org), ("$r", wait.RunId), ("$w", wait.WaitId), ("$u", JsonSerializer.Serialize(wait.UpdatedAt, Json).Trim('"')));
            Execute(connection, "INSERT OR IGNORE INTO waits VALUES ($o, $r, $w, $open, $b)",
                ("$o", Org), ("$r", wait.RunId), ("$w", wait.WaitId), ("$open", wait.Open ? 1 : 0), ("$b", JsonSerializer.Serialize(wait, Json)));
            if (wait.Open)
                Execute(connection, "UPDATE waits SET open = 1, body = $b WHERE org_id = $o AND run_id = $r AND wait_id = $w",
                    ("$o", Org), ("$r", wait.RunId), ("$w", wait.WaitId), ("$b", JsonSerializer.Serialize(wait, Json)));
        }
        foreach (var entry in records.Timeline ?? [])
            Execute(connection, "INSERT OR IGNORE INTO timeline VALUES ($o, $r, $s, $b)",
                ("$o", Org), ("$r", entry.RunId), ("$s", entry.Sequence), ("$b", JsonSerializer.Serialize(entry, Json)));
        foreach (var answer in records.Answers ?? [])
            Execute(connection, "INSERT INTO answers (org_id, run_id, body) VALUES ($o, $r, $b)",
                ("$o", Org), ("$r", answer.RunId), ("$b", JsonSerializer.Serialize(answer, Json)));
        foreach (var lockRecord in records.Locks ?? [])
            Execute(connection, "INSERT OR REPLACE INTO locks VALUES ($o, $r, $b)",
                ("$o", Org), ("$r", lockRecord.RunId), ("$b", JsonSerializer.Serialize(lockRecord, Json)));
        foreach (var step in records.AgentSteps ?? [])
            Keyed(connection, "step", step.StepKey, step.StepKey, step);
        foreach (var question in records.Questions ?? [])
        {
            // An answer or a timeout is final: a late "open" from a retried endpoint call does not reopen it.
            if (question.Status == QuestionRecord.Open)
                Execute(connection, "INSERT OR IGNORE INTO agent_keyed VALUES ($o, 'question', $k, $s, $b)",
                    ("$o", Org), ("$k", question.QuestionId), ("$s", question.StepKey), ("$b", JsonSerializer.Serialize(question, Json)));
            else
                Keyed(connection, "question", question.QuestionId, question.StepKey, question);
        }

        foreach (var report in records.Reports ?? [])
            Execute(connection, "INSERT INTO agent_log (org_id, kind, step_key, body) VALUES ($o, 'report', $s, $b)",
                ("$o", Org), ("$s", report.StepKey), ("$b", JsonSerializer.Serialize(report, Json)));
        foreach (var e in records.AgentEvents ?? [])
            Execute(connection, "INSERT INTO agent_log (org_id, kind, step_key, body) VALUES ($o, 'event', $s, $b)",
                ("$o", Org), ("$s", e.StepKey), ("$b", JsonSerializer.Serialize(e, Json)));
        // The artifact index: once per run, step, execution and name, so a retried append adds nothing.
        foreach (var a in records.Artifacts ?? [])
            Execute(connection, "INSERT OR IGNORE INTO artifacts (org_id, run_id, step_id, execution, name, body) VALUES ($o, $r, $s, $e, $n, $b)",
                ("$o", Org), ("$r", a.RunId), ("$s", a.StepId), ("$e", a.Execution), ("$n", a.Name), ("$b", JsonSerializer.Serialize(a, Json)));
        AppendLedger(connection, records);
        transaction.Commit();
        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    static void Keyed<T>(SqliteConnection connection, string kind, string key, string stepKey, T record) =>
        Execute(connection, "INSERT OR REPLACE INTO agent_keyed VALUES ($o, $k, $key, $s, $b)",
            ("$o", Org), ("$k", kind), ("$key", key), ("$s", stepKey), ("$b", JsonSerializer.Serialize(record, Json)));

    public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        string P(string key) => parameters.TryGetValue(key, out var value) ? value : throw new ArgumentException($"query {queryName} needs {key}");
        (string Sql, Type Type, (string, object?)[] Args) query = queryName switch
        {
            Queries.Run => ("SELECT body FROM runs WHERE org_id = $o AND run_id = $r", typeof(RunRecord), [("$r", P("run_id"))]),
            Queries.Runs when parameters.ContainsKey("parent_run_id") =>
                ("SELECT body FROM runs WHERE org_id = $o AND parent_run_id = $p ORDER BY run_id", typeof(RunRecord), [("$p", P("parent_run_id"))]),
            Queries.Runs => ("SELECT body FROM runs WHERE org_id = $o ORDER BY updated_at DESC LIMIT 200", typeof(RunRecord), []),
            Queries.Waits when parameters.GetValueOrDefault("open") == "true" =>
                ("SELECT body FROM waits WHERE org_id = $o AND run_id = $r AND open = 1 ORDER BY wait_id", typeof(WaitRecord), [("$r", P("run_id"))]),
            Queries.Waits => ("SELECT body FROM waits WHERE org_id = $o AND run_id = $r ORDER BY wait_id", typeof(WaitRecord), [("$r", P("run_id"))]),
            Queries.Timeline => ("SELECT body FROM timeline WHERE org_id = $o AND run_id = $r AND sequence > $a ORDER BY sequence", typeof(TimelineRecord),
                [("$r", P("run_id")), ("$a", long.Parse(parameters.GetValueOrDefault("after", "0"), System.Globalization.CultureInfo.InvariantCulture))]),
            Queries.Answers => ("SELECT body FROM answers WHERE org_id = $o AND run_id = $r ORDER BY id", typeof(AnswerRecord), [("$r", P("run_id"))]),
            Queries.Lock => ("SELECT body FROM locks WHERE org_id = $o AND run_id = $r", typeof(LockRecord), [("$r", P("run_id"))]),
            Queries.AgentStep => ("SELECT body FROM agent_keyed WHERE org_id = $o AND kind = 'step' AND key = $k", typeof(AgentStepRecord), [("$k", P("step_key"))]),
            Queries.Question => ("SELECT body FROM agent_keyed WHERE org_id = $o AND kind = 'question' AND key = $k", typeof(QuestionRecord), [("$k", P("question_id"))]),
            Queries.Questions => ("SELECT body FROM agent_keyed WHERE org_id = $o AND kind = 'question' AND step_key = $s ORDER BY key", typeof(QuestionRecord), [("$s", P("step_key"))]),
            Queries.Reports => ("SELECT body FROM agent_log WHERE org_id = $o AND kind = 'report' AND step_key = $s ORDER BY id", typeof(ReportRecord), [("$s", P("step_key"))]),
            // A run's artifacts and every descendant run's, by parent links, in the order appended.
            Queries.Artifacts => ("WITH RECURSIVE tree(run_id) AS (SELECT $r UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.run_id"
                + " WHERE runs.org_id = $o) SELECT body FROM artifacts WHERE org_id = $o AND run_id IN (SELECT run_id FROM tree) ORDER BY id",
                typeof(ArtifactRecord), [("$r", P("run_id"))]),
            Queries.AgentEvents => ("SELECT body FROM agent_log WHERE org_id = $o AND kind = 'event' AND step_key = $s ORDER BY id", typeof(AgentEventRecord), [("$s", P("step_key"))]),
            Queries.Findings => (RunTree + "SELECT body FROM findings WHERE org_id = $o AND run_id IN tree ORDER BY run_id, finding_id", typeof(FindingRecord), [("$r", P("run_id"))]),
            Queries.FindingEvents => ("SELECT body FROM finding_events WHERE org_id = $o AND run_id = $r AND finding_id = $f ORDER BY id", typeof(FindingEventRecord),
                [("$r", P("run_id")), ("$f", P("finding_id"))]),
            _ => throw new ArgumentException($"unknown query {queryName}"),
        };
        if (query.Type != typeof(T))
            throw new ArgumentException($"query {queryName} returns {query.Type.Name}, not {typeof(T).Name}");

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.Parameters.AddWithValue("$o", Org);
        foreach (var (name, value) in query.Args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var list = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return Task.FromResult(ProviderResult<IReadOnlyList<T>>.Success(list));
    }

    // ---- The findings ledger (docs/01 section 6, LOG 148) ----

    /// <summary>The run named <c>$r</c> and every run below it, by parent links, as the table <c>tree</c>.</summary>
    const string RunTree = """
        WITH RECURSIVE tree(id) AS (
            SELECT $r
            UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE runs.org_id = $o)
        """ + "\n";

    static void CreateLedger(SqliteConnection connection) =>
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS findings (org_id TEXT NOT NULL, run_id TEXT NOT NULL, finding_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, finding_id));
            CREATE TABLE IF NOT EXISTS finding_events (org_id TEXT NOT NULL, run_id TEXT NOT NULL, finding_id TEXT NOT NULL, key TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL,
                UNIQUE (org_id, run_id, finding_id, key));
            """);

    /// <summary>A finding record replaces the last; a finding event is appended once per run, finding and key.</summary>
    static void AppendLedger(SqliteConnection connection, RecordBatch records)
    {
        foreach (var finding in records.Findings ?? [])
            Execute(connection, "INSERT OR REPLACE INTO findings VALUES ($o, $r, $f, $b)",
                ("$o", Org), ("$r", finding.RunId), ("$f", finding.FindingId), ("$b", JsonSerializer.Serialize(finding, Json)));
        foreach (var e in records.FindingEvents ?? [])
            Execute(connection, "INSERT OR IGNORE INTO finding_events (org_id, run_id, finding_id, key, body) VALUES ($o, $r, $f, $k, $b)",
                ("$o", Org), ("$r", e.RunId), ("$f", e.FindingId), ("$k", e.Key), ("$b", JsonSerializer.Serialize(e, Json)));
    }
}
