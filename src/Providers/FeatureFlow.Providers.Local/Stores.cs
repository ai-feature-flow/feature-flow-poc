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
/// The record store in SQLite (docs/06 section 3.12; the solo profile). Every kind is written by its declared rule and
/// every named query built from its declaration (<see cref="RecordKinds"/>, <see cref="RecordQueries"/>); only the open
/// waits and the changes are written here. Text orders by SQLite's binary collation, which is ordinal.
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
            CREATE TABLE IF NOT EXISTS findings (org_id TEXT NOT NULL, run_id TEXT NOT NULL, finding_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, finding_id));
            CREATE TABLE IF NOT EXISTS finding_events (org_id TEXT NOT NULL, run_id TEXT NOT NULL, finding_id TEXT NOT NULL, key TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL,
                UNIQUE (org_id, run_id, finding_id, key));
            CREATE TABLE IF NOT EXISTS overrides (org_id TEXT NOT NULL, run_id TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notifications (org_id TEXT NOT NULL, key TEXT NOT NULL, run_id TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL, UNIQUE (org_id, key));
            CREATE TABLE IF NOT EXISTS sessions (org_id TEXT NOT NULL, session_id TEXT NOT NULL, run_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, session_id));
            CREATE TABLE IF NOT EXISTS session_checks (org_id TEXT NOT NULL, session_id TEXT NOT NULL, check_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, session_id, check_id));
            CREATE TABLE IF NOT EXISTS step_states (org_id TEXT NOT NULL, run_id TEXT NOT NULL, step_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, step_id));
            CREATE TABLE IF NOT EXISTS pull_requests (org_id TEXT NOT NULL, run_id TEXT NOT NULL, repository TEXT NOT NULL, number INTEGER NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY (org_id, run_id, repository, number));
            CREATE TABLE IF NOT EXISTS repositories (org_id TEXT NOT NULL, name TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, name));
            CREATE INDEX IF NOT EXISTS answers_actor ON answers (org_id, json_extract(body, '$.actor'), id);
            CREATE TABLE IF NOT EXISTS activity (org_id TEXT NOT NULL, run_id TEXT NOT NULL, step_id TEXT NOT NULL, execution INTEGER NOT NULL, session INTEGER NOT NULL,
                sequence INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (org_id, run_id, step_id, execution, session, sequence));
            CREATE INDEX IF NOT EXISTS waits_open ON waits (org_id) WHERE open = 1;
            """);
        CreateChanges(connection);
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
        command.Parameters.AddWithValue("$o", Org);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var kind in RecordKinds.All)
            foreach (var record in kind.Of(records) ?? [])
                Write(connection, kind, record);
        AppendChanges(connection, records);
        transaction.Commit();
        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    /// <summary>One record by its kind's rule (<see cref="WriteRule"/>). Times are kept as ISO 8601 text in UTC, which orders as the times do.</summary>
    static void Write(SqliteConnection connection, RecordKind kind, object record)
    {
        var rule = kind.RuleFor(record);
        var columns = kind.AllColumns.ToList();
        var names = (kind.Discriminator is null ? "" : "kind, ") + string.Concat(columns.Select(c => c.Name + ", "));
        var values = (kind.Discriminator is null ? "" : "$d, ") + string.Concat(columns.Select((_, i) => $"$c{i}, "));
        var update = string.Concat(kind.Columns.Select(c => $"{c.Name} = excluded.{c.Name}, "));
        var sql = $"INSERT INTO {kind.Table} (org_id, {names}body) VALUES ($o, {values}$b)" + rule switch
        {
            WriteRule.Append => "",
            WriteRule.KeepFirst => " ON CONFLICT DO NOTHING",
            _ => $" ON CONFLICT (org_id, {(kind.Discriminator is null ? "" : "kind, ")}{string.Join(", ", kind.Key.Select(c => c.Name))}) DO UPDATE SET {update}body = "
                 + (rule == WriteRule.CloseKeepsOpening
                     ? $"json_set({kind.Table}.body, '$.open', json('false'), '$.updatedAt', json_extract(excluded.body, '$.updatedAt'))"
                     : "excluded.body")
                 + (rule == WriteRule.ReplaceIfNotOlder ? $" WHERE excluded.updated_at >= {kind.Table}.updated_at" : ""),
        };
        Execute(connection, sql,
        [
            ("$d", kind.Discriminator), ("$b", JsonSerializer.Serialize(record, kind.Record, Json)),
            .. columns.Select((c, i) => ($"$c{i}", c.Value(record) is DateTimeOffset at ? at.ToUniversalTime().ToString("O") : c.Value(record))),
        ]);
    }

    public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var (sql, args) = queryName switch
        {
            Queries.Changes or Queries.LatestChange => Native<T, ChangeRecord>(queryName, ChangesQuery(queryName, parameters)),
            Queries.OpenWaits => Native<T, OpenWaitRecord>(queryName, (OpenWaitsSql, [])),
            _ => Select(RecordQueries.For<T>(queryName), parameters),
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$o", Org);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var list = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return Task.FromResult(ProviderResult<IReadOnlyList<T>>.Success(list));
    }

    static (string, (string, object?)[]) Native<T, TRecord>(string queryName, (string, (string, object?)[]) query) =>
        typeof(T) == typeof(TRecord) ? query : throw new ArgumentException($"query {queryName} returns {typeof(TRecord).Name}, not {typeof(T).Name}");

    /// <summary>A declared query (<see cref="RecordQueries"/>) as SQL: its variant's filters, a run tree by parent links, its order and limit.</summary>
    static (string, (string, object?)[]) Select(RecordQuery query, IReadOnlyDictionary<string, string> parameters)
    {
        var variant = query.VariantFor(parameters);
        var where = new List<string> { "org_id = $o" };
        var args = new List<(string, object?)> { ("$d", query.Kind.Discriminator) };
        var tree = "";
        if (query.Kind.Discriminator is not null)
            where.Add("kind = $d");
        foreach (var (filter, i) in variant.Filters.Select((f, i) => (f, i)))
        {
            if (query.Argument(filter, parameters) is not { } value)
                continue;
            args.Add(($"$f{i}", value));
            if (variant.RunTree && filter.Field == "run_id")
            {
                tree = $"WITH RECURSIVE tree(id) AS (SELECT $f{i} UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE runs.org_id = $o) ";
                where.Add("run_id IN (SELECT id FROM tree)");
                continue;
            }

            var field = filter.BodyField ? $"json_extract(body, '$.{filter.Field}')" : filter.Field;
            where.Add($"{field} {(filter.Op == FilterOp.After ? ">" : "=")} $f{i}");
        }

        var order = variant.OrderBy.Count == 0 ? ""
            : " ORDER BY " + string.Join(", ", variant.OrderBy.Select(c => (c == QueryVariant.Insertion ? "id" : c) + (variant.Descending ? " DESC" : "")));
        var limit = variant.LimitFor(parameters) is { } l ? $" LIMIT {l}" : "";
        return ($"{tree}SELECT body FROM {query.Kind.Table} WHERE {string.Join(" AND ", where)}{order}{limit}", [.. args]);
    }

    // ---- Changes (docs/06 section 3.12, LOG 397): what GET /events reports, written in the batch's transaction ----

    /// <summary>The changes table; a database made before <c>step_changed</c> gains its <c>step_id</c> column.</summary>
    static void CreateChanges(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS changes (org_id TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, kind TEXT NOT NULL, wait_id TEXT, at TEXT NOT NULL,
                step_id TEXT);
            """);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('changes') WHERE name = 'step_id'";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            try
            {
                Execute(connection, "ALTER TABLE changes ADD COLUMN step_id TEXT");
            }
            catch (SqliteException e) when (e.Message.Contains("duplicate column name", StringComparison.Ordinal))
            {
                // Another process opening the same old database added it first.
            }
        }
    }

    /// <summary>The batch's changes (<see cref="ChangeKinds.Of"/>). SQLite has one writer at a time, so the ids are in commit order.</summary>
    static void AppendChanges(SqliteConnection connection, RecordBatch records)
    {
        foreach (var (runId, kind, waitId, stepId) in ChangeKinds.Of(records))
            Execute(connection, "INSERT INTO changes (org_id, run_id, kind, wait_id, step_id, at) VALUES ($o, $r, $k, $w, $s, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))",
                ("$r", runId), ("$k", kind), ("$w", waitId), ("$s", stepId));
    }

    const string ChangeJson = "SELECT json_object('sequence', id, 'runId', run_id, 'kind', kind, 'waitId', wait_id, 'at', at, 'stepId', step_id) FROM changes WHERE org_id = $o";

    static (string, (string, object?)[]) ChangesQuery(string queryName, IReadOnlyDictionary<string, string> parameters) =>
        queryName == Queries.LatestChange
            ? (ChangeJson + " ORDER BY id DESC LIMIT 1", [])
            : (ChangeJson + " AND id > $a ORDER BY id LIMIT $l",
                [("$a", long.Parse(parameters.GetValueOrDefault("after", "0"), System.Globalization.CultureInfo.InvariantCulture)),
                 ("$l", QueryVariant.Page(parameters))]);

    // ---- Oversight (A3): the open waits of runs still going, for GET /attention ----

    /// <summary>Every open wait of a <c>starting</c> or <c>running</c> run, with the run, oldest first, at most 500 (<see cref="Queries.OpenWaits"/>).</summary>
    const string OpenWaitsSql = """
        SELECT json_object('wait', json(w.body), 'run', json(r.body))
        FROM waits w JOIN runs r ON r.org_id = w.org_id AND r.run_id = w.run_id
        WHERE w.org_id = $o AND w.open = 1 AND json_extract(r.body, '$.status') IN ('starting', 'running')
        ORDER BY json_extract(w.body, '$.updatedAt'), w.run_id, w.wait_id
        LIMIT 500
        """;
}
