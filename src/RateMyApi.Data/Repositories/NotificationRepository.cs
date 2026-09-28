using Dapper;
using Npgsql;

namespace RateMyApi.Data.Repositories;

public sealed record NotificationRow
{
    public long Id { get; init; }
    public long IncidentId { get; init; }
    public string Channel { get; init; } = "";
    public string Target { get; init; } = "";
    public string Payload { get; init; } = "";
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public string? LastError { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? SentAt { get; init; }
}

public sealed class NotificationRepository
{
    private const string Columns =
        "id, incident_id, channel, target, payload::text AS payload, status, attempts, next_attempt_at, last_error, created_at, sent_at";

    private readonly NpgsqlDataSource _dataSource;

    public NotificationRepository(NpgsqlDataSource dataSource)
    {
        Db.ConfigureDapper();
        _dataSource = dataSource;
    }

    /// <summary>
    /// Same claim pattern as monitors: pick due rows with FOR UPDATE SKIP LOCKED and push
    /// next_attempt_at out by <paramref name="lease"/> in the same statement. Concurrent dispatchers
    /// never pick the same row; if a dispatcher dies mid-send the lease expires and the row is retried
    /// (at-least-once delivery; receivers dedupe on X-RateMyApi-Delivery).
    /// </summary>
    public async Task<IReadOnlyList<NotificationRow>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = await conn.QueryAsync<NotificationRow>(new CommandDefinition("""
            WITH due AS (
              SELECT id FROM notifications
              WHERE status = 'pending'
                AND next_attempt_at <= now()
              ORDER BY next_attempt_at
              LIMIT @batchSize
              FOR UPDATE SKIP LOCKED
            )
            UPDATE notifications n
            SET next_attempt_at = now() + @lease
            FROM due WHERE n.id = due.id
            RETURNING n.id, n.incident_id, n.channel, n.target, n.payload::text AS payload, n.status,
                      n.attempts, n.next_attempt_at, n.last_error, n.created_at, n.sent_at
            """, new { batchSize, lease }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows.AsList();
    }

    public async Task MarkSentAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notifications
            SET status = 'sent', attempts = attempts + 1, sent_at = now(), last_error = NULL
            WHERE id = @id AND status = 'pending'
            """, new { id }, cancellationToken: ct));
    }

    /// <param name="attempts">Total attempts including the one that just failed.</param>
    public async Task ScheduleRetryAsync(long id, int attempts, string error, TimeSpan delay, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notifications
            SET attempts = @attempts, last_error = @error, next_attempt_at = now() + @delay
            WHERE id = @id AND status = 'pending'
            """, new { id, attempts, error, delay }, cancellationToken: ct));
    }

    /// <param name="attempts">Total attempts including the one that just failed.</param>
    public async Task MarkFailedAsync(long id, int attempts, string error, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notifications
            SET status = 'failed', attempts = @attempts, last_error = @error
            WHERE id = @id AND status = 'pending'
            """, new { id, attempts, error }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<NotificationRow>> ListForIncidentAsync(long incidentId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NotificationRow>(new CommandDefinition(
            $"SELECT {Columns} FROM notifications WHERE incident_id = @incidentId ORDER BY id",
            new { incidentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<NotificationRow?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NotificationRow>(new CommandDefinition(
            $"SELECT {Columns} FROM notifications WHERE id = @id", new { id }, cancellationToken: ct));
    }
}
