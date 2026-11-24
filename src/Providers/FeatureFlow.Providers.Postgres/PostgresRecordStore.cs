using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;
using Npgsql;

namespace FeatureFlow.Providers.Postgres;

/// <summary>
/// The record store on Postgres (docs/06 section 3.12; the small-company profile, docs/07 section 6). The same tables as
/// the SQLite store: every kind is written by its declared rule and every named query built from its declaration
/// (<see cref="RecordKinds"/>, <see cref="RecordQueries"/>); only the open waits and the changes are written here. Text
/// orders by <c>COLLATE "C"</c>, which is ordinal. Every key starts with <c>org_id</c> (docs/02 section 5). Each batch also
/// writes its changes, numbered in commit order (<see cref="AppendChangesAsync"/>). The connection string is a secret,
/// resolved when the store is first used; the schema is created or migrated then (<see cref="Schema"/>).
/// </summary>
public sealed class PostgresRecordStore(Func<CancellationToken, Task<ProviderResult<string>>> connectionString) : IRecordStoreProvider, IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One organisation per installation; the key is there from the start (docs/02 section 5).</summary>
    const string Org = "default";

    readonly SemaphoreSlim opening = new(1, 1);
    NpgsqlDataSource? source;

    public ProviderDescription Describe() => new("record_store", "postgres", "0.1.0", new NoCapabilities());

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) =>
        await Guarded(async ct =>
        {
            await using var connection = await OpenAsync(ct);
            await Execute(connection, null, "SELECT 1", ct);
            return default(Unit);
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (source is not null)
            await source.DisposeAsync();
    }

    async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (source is null)
        {
            await opening.WaitAsync(ct);
            try
            {
                if (source is null)
                {
                    var resolved = await connectionString(ct);
                    if (!resolved.Ok)
                        throw new ProviderException(resolved.Error!);
                    var created = NpgsqlDataSource.Create(resolved.Value!);
                    await using (var connection = await created.OpenConnectionAsync(ct))
                        await Schema.ApplyAsync(connection, ct);
                    source = created;
                }
            }
            finally
            {
                opening.Release();
            }
        }

        return await source.OpenConnectionAsync(ct);
    }

    /// <summary>
    /// docs/06 section 1.1: a database that cannot be reached is <c>unreachable</c> and one that refuses the statement
    /// <c>server_error</c>, both infrastructure, so the engine retries without using an attempt. A missing or wrong
    /// connection secret is the secrets provider's error, passed on.
    /// </summary>
    static async Task<ProviderResult<T>> Guarded<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        try
        {
            return ProviderResult<T>.Success(await work(ct));
        }
        catch (ProviderException e)
        {
            return new ProviderResult<T>(default, e.Error);
        }
        catch (PostgresException e) when (e.SqlState is "28P01" or "28000")
        {
            return ProviderResult<T>.Failure("unauthenticated", $"Postgres refused the login: {e.MessageText}");
        }
        catch (PostgresException e)
        {
            return ProviderResult<T>.Failure("server_error", $"Postgres: {e.SqlState} {e.MessageText}");
        }
        catch (NpgsqlException e)
        {
            return ProviderResult<T>.Failure("unreachable", $"Postgres: {e.Message}");
        }
    }

    static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, IEnumerable<(string, object?)> parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("o", Org);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A record as JSON for a <c>jsonb</c> column, which refuses the NUL character (SQLSTATE 22P05): an escaped NUL becomes
    /// U+FFFD, so text from outside (an agent's report, a reviewer's finding) never leaves a record unwritable (LOG 290).
    /// A literal backslash before <c>u0000</c> is left alone.
    /// </summary>
    static string Body(object record, Type type) => EscapedNul.Replace(JsonSerializer.Serialize(record, type, Json), @"$1\uFFFD");

    static readonly Regex EscapedNul = new(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.Compiled);

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken) =>
        Guarded(async ct =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            foreach (var kind in RecordKinds.All)
                foreach (var record in kind.Of(records) ?? [])
                    await Write(connection, tx, kind, record, ct);
            await AppendChangesAsync(connection, tx, records, ct);
            await tx.CommitAsync(ct);
            return default(Unit);
        }, cancellationToken);

    /// <summary>One record by its kind's rule (<see cref="WriteRule"/>).</summary>
    static Task Write(NpgsqlConnection connection, NpgsqlTransaction tx, RecordKind kind, object record, CancellationToken ct)
    {
        var rule = kind.RuleFor(record);
        var columns = kind.AllColumns.ToList();
        var names = (kind.Discriminator is null ? "" : "kind, ") + string.Concat(columns.Select(c => c.Name + ", "));
        var values = (kind.Discriminator is null ? "" : "@d, ") + string.Concat(columns.Select((_, i) => $"@c{i}, "));
        var update = string.Concat(kind.Columns.Select(c => $"{c.Name} = excluded.{c.Name}, "));
        var sql = $"INSERT INTO {kind.Table} (org_id, {names}body) VALUES (@o, {values}@b::jsonb)" + rule switch
        {
            WriteRule.Append => "",
            WriteRule.KeepFirst => " ON CONFLICT DO NOTHING",
            _ => $" ON CONFLICT (org_id, {(kind.Discriminator is null ? "" : "kind, ")}{string.Join(", ", kind.Key.Select(c => c.Name))}) DO UPDATE SET {update}body = "
                 + (rule == WriteRule.CloseKeepsOpening
                     ? $"jsonb_set(jsonb_set({kind.Table}.body, '{{open}}', 'false'), '{{updatedAt}}', excluded.body->'updatedAt')"
                     : "excluded.body")
                 + (rule == WriteRule.ReplaceIfNotOlder ? $" WHERE {kind.Table}.updated_at <= excluded.updated_at" : ""),
        };
        return Execute(connection, tx, sql, ct,
        [
            ("b", Body(record, kind.Record)),
            .. kind.Discriminator is { } d ? [("d", (object?)d)] : Array.Empty<(string, object?)>(),
            .. columns.Select((c, i) => ($"c{i}", c.Value(record))),
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
        return Guarded<IReadOnlyList<T>>(async ct =>
        {
            await using var connection = await OpenAsync(ct);
            await using var command = Command(connection, null, sql, args);
            var list = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
            return list;
        }, cancellationToken);
    }

    static (string, (string, object?)[]) Native<T, TRecord>(string queryName, (string, (string, object?)[]) query) =>
        typeof(T) == typeof(TRecord) ? query : throw new ArgumentException($"query {queryName} returns {typeof(TRecord).Name}, not {typeof(T).Name}");

    /// <summary>A declared query (<see cref="RecordQueries"/>) as SQL: its variant's filters, a run tree by parent links, its order and limit.</summary>
    static (string, (string, object?)[]) Select(RecordQuery query, IReadOnlyDictionary<string, string> parameters)
    {
        var variant = query.VariantFor(parameters);
        var where = new List<string> { "org_id = @o" };
        var args = new List<(string, object?)>();
        var tree = "";
        if (query.Kind.Discriminator is { } d)
        {
            where.Add("kind = @d");
            args.Add(("d", d));
        }

        foreach (var (filter, i) in variant.Filters.Select((f, i) => (f, i)))
        {
            if (query.Argument(filter, parameters) is not { } value)
                continue;
            args.Add(($"f{i}", value));
            if (variant.RunTree && filter.Field == "run_id")
            {
                tree = $"WITH RECURSIVE tree(id) AS (SELECT @f{i}::text UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE runs.org_id = @o) ";
                where.Add("run_id IN (SELECT id FROM tree)");
                continue;
            }

            var field = filter.BodyField ? $"body->>'{filter.Field}'" : filter.Field;
            where.Add($"{field} {(filter.Op == FilterOp.After ? ">" : "=")} @f{i}");
        }

        string Order(string column) =>
            (column == QueryVariant.Insertion ? "id" : query.Kind.Column(column).Type == typeof(string) ? column + " COLLATE \"C\"" : column)
            + (variant.Descending ? " DESC" : "");
        var order = variant.OrderBy.Count == 0 ? "" : " ORDER BY " + string.Join(", ", variant.OrderBy.Select(Order));
        var limit = variant.LimitFor(parameters) is { } l ? $" LIMIT {l}" : "";
        return ($"{tree}SELECT body FROM {query.Kind.Table} WHERE {string.Join(" AND ", where)}{order}{limit}", [.. args]);
    }

    // ---- Changes (docs/06 section 3.12, LOG 397): what GET /events reports, written in the batch's transaction ----

    /// <summary>An arbitrary key for <c>pg_advisory_xact_lock</c>, held from the first change row a transaction writes until it commits.</summary>
    const long ChangesLockKey = 0x46_46_5F_43_48_41_4E_47; // "FF_CHANG"

    /// <summary>
    /// The batch's changes (<see cref="ChangeKinds.Of"/>), last in its transaction. The advisory lock makes identity order
    /// commit order: without it a transaction that took id 10 could commit after one that took 11, and a reader that had
    /// read 11 would never see 10. The lock is held only from here to the commit.
    /// </summary>
    internal static async Task AppendChangesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RecordBatch records, CancellationToken ct)
    {
        var changes = ChangeKinds.Of(records);
        if (changes.Count == 0)
            return;
        await Execute(connection, tx, $"SELECT pg_advisory_xact_lock({ChangesLockKey})", ct);
        foreach (var (runId, kind, waitId, stepId) in changes)
            await Execute(connection, tx, "INSERT INTO changes (org_id, run_id, kind, wait_id, step_id) VALUES (@o, @r, @k, @w, @s)", ct,
                ("r", runId), ("k", kind), ("w", waitId), ("s", stepId));
    }

    const string ChangeJson = "SELECT jsonb_build_object('sequence', id, 'runId', run_id, 'kind', kind, 'waitId', wait_id, 'at', at, 'stepId', step_id) FROM changes WHERE org_id = @o";

    static (string, (string, object?)[]) ChangesQuery(string queryName, IReadOnlyDictionary<string, string> parameters) =>
        queryName == Queries.LatestChange
            ? (ChangeJson + " ORDER BY id DESC LIMIT 1", [])
            : (ChangeJson + " AND id > @a ORDER BY id LIMIT @l",
                [("a", long.Parse(parameters.GetValueOrDefault("after", "0"), CultureInfo.InvariantCulture)), ("l", QueryVariant.Page(parameters))]);

    // ---- Oversight (A3): the open waits of runs still going, for GET /attention (index waits_open, schema script 2) ----

    /// <summary>Every open wait of a <c>starting</c> or <c>running</c> run, with the run, oldest first, at most 500 (<see cref="Queries.OpenWaits"/>).</summary>
    const string OpenWaitsSql = """
        SELECT jsonb_build_object('wait', w.body, 'run', r.body)
        FROM waits w JOIN runs r USING (org_id, run_id)
        WHERE w.org_id = @o AND w.open AND r.body->>'status' IN ('starting', 'running')
        ORDER BY w.body->>'updatedAt', w.run_id, w.wait_id
        LIMIT 500
        """;
}
