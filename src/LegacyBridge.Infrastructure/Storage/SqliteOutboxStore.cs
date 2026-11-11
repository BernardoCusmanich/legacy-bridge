using System.Globalization;
using Dapper;
using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Models;
using Microsoft.Data.Sqlite;

namespace LegacyBridge.Infrastructure.Storage;

/// <summary>
/// Outbox e checkpoint em SQLite. Datas são gravadas como texto ISO-8601 UTC de
/// largura fixa, então comparação lexicográfica equivale à cronológica.
/// </summary>
public sealed class SqliteOutboxStore : IOutboxStore
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const int MaxErrorLength = 1000;

    private readonly string _connectionString;
    private readonly string _path;

    public SqliteOutboxStore(string sqlitePath)
    {
        _path = Path.GetFullPath(sqlitePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public static string FormatDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(DateFormat, CultureInfo.InvariantCulture);

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS checkpoint (
                source  TEXT PRIMARY KEY,
                last_id INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox (
                event_id        TEXT PRIMARY KEY,
                changelog_id    INTEGER NOT NULL UNIQUE,
                created_at      TEXT NOT NULL,
                payload_json    TEXT NOT NULL,
                status          TEXT NOT NULL CHECK (status IN ('pending','sent','dead')),
                attempts        INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                last_error      TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_outbox_due ON outbox (status, next_attempt_at, changelog_id);
            """, cancellationToken: ct));
    }

    public async Task<long> GetCheckpointAsync(string source, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT last_id FROM checkpoint WHERE source = @source",
            new { source }, cancellationToken: ct)) ?? 0;
    }

    public async Task<int> EnqueueAsync(string source, IReadOnlyList<OutboxMessage> messages, long newCheckpoint, CancellationToken ct)
    {
        var now = FormatDate(DateTime.UtcNow);

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var inserted = 0;
        foreach (var m in messages)
        {
            // UNIQUE(changelog_id) + OR IGNORE: reprocessar o mesmo changelog não duplica evento.
            inserted += await conn.ExecuteAsync(new CommandDefinition("""
                INSERT OR IGNORE INTO outbox
                    (event_id, changelog_id, created_at, payload_json, status, attempts, next_attempt_at)
                VALUES
                    (@EventId, @ChangelogId, @now, @PayloadJson, 'pending', 0, @now)
                """,
                new { m.EventId, m.ChangelogId, m.PayloadJson, now }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO checkpoint (source, last_id) VALUES (@source, @newCheckpoint)
            ON CONFLICT (source) DO UPDATE SET last_id = excluded.last_id
            WHERE excluded.last_id > checkpoint.last_id
            """,
            new { source, newCheckpoint }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<OutboxItem>> FetchDueAsync(int limit, DateTime utcNow, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<OutboxRow>(new CommandDefinition("""
            SELECT event_id AS EventId, changelog_id AS ChangelogId, payload_json AS PayloadJson, attempts AS Attempts
            FROM outbox
            WHERE status = 'pending' AND next_attempt_at <= @now
            ORDER BY changelog_id
            LIMIT @limit
            """,
            new { now = FormatDate(utcNow), limit }, cancellationToken: ct));

        return rows.Select(r => new OutboxItem(r.EventId, r.ChangelogId, r.PayloadJson, (int)r.Attempts)).ToList();
    }

    public Task MarkSentAsync(string eventId, int attempts, CancellationToken ct) =>
        UpdateAsync("""
            UPDATE outbox SET status = 'sent', attempts = @attempts, last_error = NULL
            WHERE event_id = @eventId
            """, new { eventId, attempts }, ct);

    public Task MarkDeadAsync(string eventId, int attempts, string error, CancellationToken ct) =>
        UpdateAsync("""
            UPDATE outbox SET status = 'dead', attempts = @attempts, last_error = @error
            WHERE event_id = @eventId
            """, new { eventId, attempts, error = Truncate(error) }, ct);

    public Task ScheduleRetryAsync(string eventId, int attempts, DateTime nextAttemptUtc, string error, CancellationToken ct) =>
        UpdateAsync("""
            UPDATE outbox SET attempts = @attempts, next_attempt_at = @next, last_error = @error
            WHERE event_id = @eventId
            """, new { eventId, attempts, next = FormatDate(nextAttemptUtc), error = Truncate(error) }, ct);

    private async Task UpdateAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("PRAGMA busy_timeout = 5000;");
        return conn;
    }

    private static string Truncate(string s) => s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];

    private sealed class OutboxRow
    {
        public string EventId { get; init; } = "";
        public long ChangelogId { get; init; }
        public string PayloadJson { get; init; } = "";
        public long Attempts { get; init; }
    }
}
