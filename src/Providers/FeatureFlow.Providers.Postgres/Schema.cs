using Npgsql;

namespace FeatureFlow.Providers.Postgres;

/// <summary>
/// The record store's schema, as numbered scripts applied in order at first use: the database's <c>ff_schema</c> table
/// holds the number applied. A transaction-scoped advisory lock lets several hosts start at once. No migration framework:
/// version 1 has one script; a later change adds the next (no backward compatibility before version 1, LOG 263).
/// </summary>
static class Schema
{
    /// <summary>An arbitrary key for <c>pg_advisory_xact_lock</c>, so only one host applies a script at a time.</summary>
    const long LockKey = 0x46_46_5F_53_43_48_45_4D; // "FF_SCHEM"

    static readonly string[] Scripts =
    [
        """
        CREATE TABLE runs (org_id text NOT NULL, run_id text NOT NULL, parent_run_id text, updated_at timestamptz NOT NULL, body jsonb NOT NULL,
            PRIMARY KEY (org_id, run_id));
        CREATE INDEX runs_parent ON runs (org_id, parent_run_id);
        CREATE INDEX runs_updated ON runs (org_id, updated_at DESC);
        CREATE TABLE waits (org_id text NOT NULL, run_id text NOT NULL, wait_id text NOT NULL, open boolean NOT NULL, body jsonb NOT NULL,
            PRIMARY KEY (org_id, run_id, wait_id));
        CREATE TABLE timeline (org_id text NOT NULL, run_id text NOT NULL, sequence bigint NOT NULL, body jsonb NOT NULL, PRIMARY KEY (org_id, run_id, sequence));
        CREATE TABLE answers (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, run_id text NOT NULL, body jsonb NOT NULL);
        CREATE INDEX answers_run ON answers (org_id, run_id);
        CREATE TABLE locks (org_id text NOT NULL, run_id text NOT NULL, body jsonb NOT NULL, PRIMARY KEY (org_id, run_id));
        CREATE TABLE agent_keyed (org_id text NOT NULL, kind text NOT NULL, key text NOT NULL, step_key text NOT NULL, body jsonb NOT NULL,
            PRIMARY KEY (org_id, kind, key));
        CREATE INDEX agent_keyed_step ON agent_keyed (org_id, kind, step_key);
        CREATE TABLE agent_log (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, kind text NOT NULL, step_key text NOT NULL, body jsonb NOT NULL);
        CREATE INDEX agent_log_step ON agent_log (org_id, kind, step_key);
        CREATE TABLE artifacts (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, run_id text NOT NULL, step_id text NOT NULL,
            execution integer NOT NULL, name text NOT NULL, body jsonb NOT NULL, UNIQUE (org_id, run_id, step_id, execution, name));
        CREATE TABLE findings (org_id text NOT NULL, run_id text NOT NULL, finding_id text NOT NULL, body jsonb NOT NULL, PRIMARY KEY (org_id, run_id, finding_id));
        CREATE TABLE finding_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, run_id text NOT NULL, finding_id text NOT NULL,
            key text NOT NULL, body jsonb NOT NULL, UNIQUE (org_id, run_id, finding_id, key));
        CREATE TABLE overrides (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, run_id text NOT NULL, body jsonb NOT NULL);
        CREATE INDEX overrides_run ON overrides (org_id, run_id);
        CREATE TABLE notifications (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, org_id text NOT NULL, key text NOT NULL, run_id text NOT NULL,
            body jsonb NOT NULL, UNIQUE (org_id, key));
        CREATE INDEX notifications_run ON notifications (org_id, run_id);
        CREATE TABLE sessions (org_id text NOT NULL, session_id text NOT NULL, run_id text NOT NULL, body jsonb NOT NULL, PRIMARY KEY (org_id, session_id));
        CREATE INDEX sessions_run ON sessions (org_id, run_id);
        CREATE TABLE session_checks (org_id text NOT NULL, session_id text NOT NULL, check_id text NOT NULL, body jsonb NOT NULL,
            PRIMARY KEY (org_id, session_id, check_id));
        """,
    ];

    /// <summary>The schema version this build writes.</summary>
    public static int Latest => Scripts.Length;

    public static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Run(connection, tx, $"SELECT pg_advisory_xact_lock({LockKey})", ct);
        await Run(connection, tx, "CREATE TABLE IF NOT EXISTS ff_schema (version integer NOT NULL)", ct);
        await using var read = new NpgsqlCommand("SELECT max(version) FROM ff_schema", connection, tx);
        var applied = await read.ExecuteScalarAsync(ct) is int v ? v : 0;
        if (applied > Latest)
            throw new InvalidOperationException($"the record store's schema is version {applied}, newer than this build's {Latest}: run a newer build");
        for (var version = applied + 1; version <= Latest; version++)
        {
            await Run(connection, tx, Scripts[version - 1], ct);
            await Run(connection, tx, $"INSERT INTO ff_schema VALUES ({version})", ct);
        }

        await tx.CommitAsync(ct);
    }

    static async Task Run(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(ct);
    }
}
