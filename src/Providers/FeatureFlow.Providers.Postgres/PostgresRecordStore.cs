using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;
using Npgsql;

namespace FeatureFlow.Providers.Postgres;

/// <summary>
/// The record store on Postgres (docs/06 section 3.12; the small-company profile, docs/07 section 6). The same tables and
/// rules as the SQLite store: runs (the newer record kept, by <c>updated_at</c>), waits, locks, agent steps, questions, findings, sessions and their checks are kept by
/// key, the last write winning (a closing wait keeps what its opening record said; an answered or timed-out question is
/// not reopened); the timeline, finding events, the artifact index and notifications are kept once per key, so a retried
/// append adds nothing; answers, reports, agent events and overrides are appended. Every key starts with
/// <c>org_id</c> (docs/02 section 5). Each batch also writes its changes, numbered in commit order (<see cref="AppendChangesAsync"/>).
/// The connection string is a secret, resolved when the store is first used; the
/// schema is created or migrated then (<see cref="Schema"/>).
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

    static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("o", Org);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A record as JSON for a <c>jsonb</c> column, which refuses the NUL character (SQLSTATE 22P05): an escaped NUL becomes
    /// U+FFFD, so text from outside (an agent's report, a reviewer's finding) never leaves a record unwritable (LOG 290).
    /// A literal backslash before <c>u0000</c> is left alone.
    /// </summary>
    static string Body<T>(T record) => EscapedNul.Replace(JsonSerializer.Serialize(record, Json), @"$1\uFFFD");

    static readonly Regex EscapedNul = new(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.Compiled);

    public Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken) =>
        Guarded(async ct =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            Task Run(string sql, params (string, object?)[] p) => Execute(connection, tx, sql, ct, p);

            foreach (var run in records.Runs ?? [])
                await Run("""
                    INSERT INTO runs VALUES (@o, @r, @p, @u, @b::jsonb)
                    ON CONFLICT (org_id, run_id) DO UPDATE SET parent_run_id = excluded.parent_run_id, updated_at = excluded.updated_at, body = excluded.body
                    WHERE runs.updated_at <= excluded.updated_at
                    """, ("r", run.RunId), ("p", run.ParentRunId), ("u", run.UpdatedAt), ("b", Body(run)));
            foreach (var wait in records.Waits ?? [])
            {
                // A record that closes a wait keeps what the opening record said about it.
                if (wait.Open)
                    await Run("""
                        INSERT INTO waits VALUES (@o, @r, @w, true, @b::jsonb)
                        ON CONFLICT (org_id, run_id, wait_id) DO UPDATE SET open = true, body = excluded.body
                        """, ("r", wait.RunId), ("w", wait.WaitId), ("b", Body(wait)));
                else
                    await Run("""
                        INSERT INTO waits VALUES (@o, @r, @w, false, @b::jsonb)
                        ON CONFLICT (org_id, run_id, wait_id) DO UPDATE SET open = false,
                            body = jsonb_set(jsonb_set(waits.body, '{open}', 'false'), '{updatedAt}', @u::jsonb)
                        """, ("r", wait.RunId), ("w", wait.WaitId), ("b", Body(wait)), ("u", Body(wait.UpdatedAt)));
            }

            foreach (var entry in records.Timeline ?? [])
                await Run("INSERT INTO timeline VALUES (@o, @r, @s, @b::jsonb) ON CONFLICT DO NOTHING", ("r", entry.RunId), ("s", entry.Sequence), ("b", Body(entry)));
            foreach (var answer in records.Answers ?? [])
                await Run("INSERT INTO answers (org_id, run_id, body) VALUES (@o, @r, @b::jsonb)", ("r", answer.RunId), ("b", Body(answer)));
            foreach (var l in records.Locks ?? [])
                await Run("INSERT INTO locks VALUES (@o, @r, @b::jsonb) ON CONFLICT (org_id, run_id) DO UPDATE SET body = excluded.body", ("r", l.RunId), ("b", Body(l)));
            foreach (var step in records.AgentSteps ?? [])
                await Keyed(Run, "step", step.StepKey, step.StepKey, Body(step));
            foreach (var question in records.Questions ?? [])
            {
                // An answer or a timeout is final: a late "open" from a retried endpoint call does not reopen it.
                if (question.Status == QuestionRecord.Open)
                    await Run("INSERT INTO agent_keyed VALUES (@o, 'question', @k, @s, @b::jsonb) ON CONFLICT DO NOTHING",
                        ("k", question.QuestionId), ("s", question.StepKey), ("b", Body(question)));
                else
                    await Keyed(Run, "question", question.QuestionId, question.StepKey, Body(question));
            }

            foreach (var report in records.Reports ?? [])
                await Run("INSERT INTO agent_log (org_id, kind, step_key, body) VALUES (@o, 'report', @s, @b::jsonb)", ("s", report.StepKey), ("b", Body(report)));
            foreach (var e in records.AgentEvents ?? [])
                await Run("INSERT INTO agent_log (org_id, kind, step_key, body) VALUES (@o, 'event', @s, @b::jsonb)", ("s", e.StepKey), ("b", Body(e)));
            // The artifact index: once per run, step, execution and name, so a retried append adds nothing.
            foreach (var a in records.Artifacts ?? [])
                await Run("INSERT INTO artifacts (org_id, run_id, step_id, execution, name, body) VALUES (@o, @r, @s, @e, @n, @b::jsonb) ON CONFLICT DO NOTHING",
                    ("r", a.RunId), ("s", a.StepId), ("e", a.Execution), ("n", a.Name), ("b", Body(a)));

            // The findings ledger: a finding replaces the last; an event is appended once per run, finding and key.
            foreach (var f in records.Findings ?? [])
                await Run("INSERT INTO findings VALUES (@o, @r, @f, @b::jsonb) ON CONFLICT (org_id, run_id, finding_id) DO UPDATE SET body = excluded.body",
                    ("r", f.RunId), ("f", f.FindingId), ("b", Body(f)));
            foreach (var e in records.FindingEvents ?? [])
                await Run("INSERT INTO finding_events (org_id, run_id, finding_id, key, body) VALUES (@o, @r, @f, @k, @b::jsonb) ON CONFLICT DO NOTHING",
                    ("r", e.RunId), ("f", e.FindingId), ("k", e.Key), ("b", Body(e)));

            // People and steering: overrides appended; a notification kept once per key, the first write winning; sessions and checks replaced.
            foreach (var o in records.Overrides ?? [])
                await Run("INSERT INTO overrides (org_id, run_id, body) VALUES (@o, @r, @b::jsonb)", ("r", o.RunId), ("b", Body(o)));
            foreach (var n in records.Notifications ?? [])
                await Run("INSERT INTO notifications (org_id, key, run_id, body) VALUES (@o, @k, @r, @b::jsonb) ON CONFLICT DO NOTHING",
                    ("k", n.Key), ("r", n.RunId), ("b", Body(n)));
            foreach (var s in records.Sessions ?? [])
                await Run("INSERT INTO sessions VALUES (@o, @s, @r, @b::jsonb) ON CONFLICT (org_id, session_id) DO UPDATE SET run_id = excluded.run_id, body = excluded.body",
                    ("s", s.SessionId), ("r", s.RunId), ("b", Body(s)));
            foreach (var c in records.SessionChecks ?? [])
                await Run("INSERT INTO session_checks VALUES (@o, @s, @c, @b::jsonb) ON CONFLICT (org_id, session_id, check_id) DO UPDATE SET body = excluded.body",
                    ("s", c.SessionId), ("c", c.CheckId), ("b", Body(c)));

            await AppendChangesAsync(connection, tx, records, ct);
            await tx.CommitAsync(ct);
            return default(Unit);
        }, cancellationToken);

    static Task Keyed(Func<string, (string, object?)[], Task> run, string kind, string key, string stepKey, string body) =>
        run("INSERT INTO agent_keyed VALUES (@o, @kind, @k, @s, @b::jsonb) ON CONFLICT (org_id, kind, key) DO UPDATE SET step_key = excluded.step_key, body = excluded.body",
            [("kind", kind), ("k", key), ("s", stepKey), ("b", body)]);

    /// <summary>The run named <c>@r</c> and every run below it, by parent links, as the table <c>tree</c>.</summary>
    const string RunTree = """
        WITH RECURSIVE tree(id) AS (
            SELECT @r::text
            UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE runs.org_id = @o)
        """ + "\n";

    public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        string P(string key) => parameters.TryGetValue(key, out var value) ? value : throw new ArgumentException($"query {queryName} needs {key}");
        (string Sql, Type Type, (string, object?)[] Args) query = queryName switch
        {
            Queries.Changes or Queries.LatestChange => ChangesQuery(queryName, parameters),
            Queries.Run => ("SELECT body FROM runs WHERE org_id = @o AND run_id = @r", typeof(RunRecord), [("r", P("run_id"))]),
            Queries.Runs when parameters.ContainsKey("parent_run_id") =>
                ("SELECT body FROM runs WHERE org_id = @o AND parent_run_id = @p ORDER BY run_id", typeof(RunRecord), [("p", P("parent_run_id"))]),
            Queries.Runs => ("SELECT body FROM runs WHERE org_id = @o ORDER BY updated_at DESC LIMIT 200", typeof(RunRecord), []),
            Queries.Waits when parameters.GetValueOrDefault("open") == "true" =>
                ("SELECT body FROM waits WHERE org_id = @o AND run_id = @r AND open ORDER BY wait_id", typeof(WaitRecord), [("r", P("run_id"))]),
            Queries.Waits => ("SELECT body FROM waits WHERE org_id = @o AND run_id = @r ORDER BY wait_id", typeof(WaitRecord), [("r", P("run_id"))]),
            Queries.Timeline => ("SELECT body FROM timeline WHERE org_id = @o AND run_id = @r AND sequence > @a ORDER BY sequence", typeof(TimelineRecord),
                [("r", P("run_id")), ("a", long.Parse(parameters.GetValueOrDefault("after", "0"), CultureInfo.InvariantCulture))]),
            Queries.Answers => ("SELECT body FROM answers WHERE org_id = @o AND run_id = @r ORDER BY id", typeof(AnswerRecord), [("r", P("run_id"))]),
            Queries.Lock => ("SELECT body FROM locks WHERE org_id = @o AND run_id = @r", typeof(LockRecord), [("r", P("run_id"))]),
            Queries.AgentStep => ("SELECT body FROM agent_keyed WHERE org_id = @o AND kind = 'step' AND key = @k", typeof(AgentStepRecord), [("k", P("step_key"))]),
            Queries.Question => ("SELECT body FROM agent_keyed WHERE org_id = @o AND kind = 'question' AND key = @k", typeof(QuestionRecord), [("k", P("question_id"))]),
            Queries.Questions => ("SELECT body FROM agent_keyed WHERE org_id = @o AND kind = 'question' AND step_key = @s ORDER BY key", typeof(QuestionRecord), [("s", P("step_key"))]),
            Queries.Reports => ("SELECT body FROM agent_log WHERE org_id = @o AND kind = 'report' AND step_key = @s ORDER BY id", typeof(ReportRecord), [("s", P("step_key"))]),
            Queries.AgentEvents => ("SELECT body FROM agent_log WHERE org_id = @o AND kind = 'event' AND step_key = @s ORDER BY id", typeof(AgentEventRecord), [("s", P("step_key"))]),
            // A run's artifacts and every descendant run's, by parent links, in the order appended.
            Queries.Artifacts => (RunTree + "SELECT body FROM artifacts WHERE org_id = @o AND run_id IN (SELECT id FROM tree) ORDER BY id", typeof(ArtifactRecord), [("r", P("run_id"))]),
            Queries.Findings => (RunTree + "SELECT body FROM findings WHERE org_id = @o AND run_id IN (SELECT id FROM tree) ORDER BY run_id, finding_id", typeof(FindingRecord),
                [("r", P("run_id"))]),
            Queries.FindingEvents => ("SELECT body FROM finding_events WHERE org_id = @o AND run_id = @r AND finding_id = @f ORDER BY id", typeof(FindingEventRecord),
                [("r", P("run_id")), ("f", P("finding_id"))]),
            Queries.Overrides => ("SELECT body FROM overrides WHERE org_id = @o AND run_id = @r ORDER BY id", typeof(OverrideRecord), [("r", P("run_id"))]),
            Queries.Notification => ("SELECT body FROM notifications WHERE org_id = @o AND key = @k", typeof(NotificationRecord), [("k", P("key"))]),
            Queries.Notifications => ("SELECT body FROM notifications WHERE org_id = @o AND run_id = @r ORDER BY id", typeof(NotificationRecord), [("r", P("run_id"))]),
            Queries.Session => ("SELECT body FROM sessions WHERE org_id = @o AND session_id = @s", typeof(SessionRecord), [("s", P("session_id"))]),
            Queries.Sessions => ("SELECT body FROM sessions WHERE org_id = @o AND run_id = @r ORDER BY session_id", typeof(SessionRecord), [("r", P("run_id"))]),
            Queries.SessionChecks => ("SELECT body FROM session_checks WHERE org_id = @o AND session_id = @s ORDER BY check_id", typeof(SessionCheckRecord),
                [("s", P("session_id"))]),
            _ => throw new ArgumentException($"unknown query {queryName}"),
        };
        if (query.Type != typeof(T))
            throw new ArgumentException($"query {queryName} returns {query.Type.Name}, not {typeof(T).Name}");

        return Guarded<IReadOnlyList<T>>(async ct =>
        {
            await using var connection = await OpenAsync(ct);
            await using var command = new NpgsqlCommand(query.Sql, connection);
            command.Parameters.AddWithValue("o", Org);
            foreach (var (name, value) in query.Args)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var list = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
            return list;
        }, cancellationToken);
    }

    // ---- Changes (docs/06 section 3.12, LOG A3-0j): what GET /events reports, written in the batch's transaction ----

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
        foreach (var (runId, kind, waitId) in changes)
            await Execute(connection, tx, "INSERT INTO changes (org_id, run_id, kind, wait_id) VALUES (@o, @r, @k, @w)", ct, ("r", runId), ("k", kind), ("w", waitId));
    }

    const string ChangeJson = "SELECT jsonb_build_object('sequence', id, 'runId', run_id, 'kind', kind, 'waitId', wait_id, 'at', at) FROM changes WHERE org_id = @o";

    static (string Sql, Type Type, (string, object?)[] Args) ChangesQuery(string queryName, IReadOnlyDictionary<string, string> parameters) =>
        queryName == Queries.LatestChange
            ? (ChangeJson + " ORDER BY id DESC LIMIT 1", typeof(ChangeRecord), [])
            : (ChangeJson + " AND id > @a ORDER BY id LIMIT @l", typeof(ChangeRecord),
                [("a", long.Parse(parameters.GetValueOrDefault("after", "0"), CultureInfo.InvariantCulture)),
                 ("l", Math.Clamp(int.Parse(parameters.GetValueOrDefault("limit", "500"), CultureInfo.InvariantCulture), 1, 500))]);
}
