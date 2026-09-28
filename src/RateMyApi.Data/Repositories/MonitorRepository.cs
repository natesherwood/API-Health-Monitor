using Dapper;
using Npgsql;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Sharding;

namespace RateMyApi.Data.Repositories;

/// <summary>A monitor plus its derived runtime status (current state, last check, live p95).</summary>
public sealed record MonitorStatusRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Method { get; init; } = "";
    public int ExpectedStatus { get; init; }
    public int IntervalSeconds { get; init; }
    public int TimeoutMs { get; init; }
    public bool Enabled { get; init; }
    public int DegradedP95Ms { get; init; }
    public int DownConsecutiveFailures { get; init; }
    public DateTime NextDueAt { get; init; }
    public DateTime CreatedAt { get; init; }

    public string State { get; init; } = "ok";
    public DateTime? StateChangedAt { get; init; }
    public DateTime? LastCheckedAt { get; init; }
    public bool? LastSuccess { get; init; }
    public int? LastLatencyMs { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public int? LiveP95Ms { get; init; }
}

public sealed class MonitorRepository
{
    private const string Columns =
        "id, name, url, method, expected_status, interval_seconds, timeout_ms, enabled, " +
        "degraded_p95_ms, down_consecutive_failures, next_due_at, created_at";

    /// <summary>
    /// Claim + reschedule in ONE statement. Row locks from FOR UPDATE SKIP LOCKED make concurrent
    /// claimers skip each other's rows instead of blocking, and because next_due_at moves into the
    /// future before the transaction commits, a row can never be claimed twice for the same slot.
    /// </summary>
    private const string ClaimSql = """
        WITH due AS (
          SELECT id FROM monitors
          WHERE enabled
            AND next_due_at <= now()
            AND (id % @workerCount) = @workerId
          ORDER BY next_due_at
          LIMIT @batchSize
          FOR UPDATE SKIP LOCKED
        )
        UPDATE monitors m
        SET next_due_at = now() + make_interval(secs => m.interval_seconds)
        FROM due WHERE m.id = due.id
        RETURNING m.id, m.name, m.url, m.method, m.expected_status, m.interval_seconds, m.timeout_ms,
                  m.enabled, m.degraded_p95_ms, m.down_consecutive_failures, m.next_due_at, m.created_at
        """;

    private readonly NpgsqlDataSource _dataSource;

    public MonitorRepository(NpgsqlDataSource dataSource)
    {
        Db.ConfigureDapper();
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<MonitorDefinition>> ClaimDueAsync(ShardAssignment shard, int batchSize, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var claimed = await conn.QueryAsync<MonitorDefinition>(new CommandDefinition(
            ClaimSql,
            new { workerCount = shard.WorkerCount, workerId = shard.WorkerId, batchSize },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return claimed.AsList();
    }

    public async Task<MonitorDefinition> CreateAsync(MonitorInput input, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<MonitorDefinition>(new CommandDefinition($"""
            INSERT INTO monitors (name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                                  degraded_p95_ms, down_consecutive_failures, next_due_at)
            VALUES (@Name, @Url, @Method, @ExpectedStatus, @IntervalSeconds, @TimeoutMs, @Enabled,
                    @DegradedP95Ms, @DownConsecutiveFailures, now())
            RETURNING {Columns}
            """, input, cancellationToken: ct));
    }

    /// <returns>The updated monitor, or null if it does not exist.</returns>
    public async Task<MonitorDefinition?> UpdateAsync(long id, MonitorInput input, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MonitorDefinition>(new CommandDefinition($"""
            UPDATE monitors
            SET name = @Name, url = @Url, method = @Method, expected_status = @ExpectedStatus,
                interval_seconds = @IntervalSeconds, timeout_ms = @TimeoutMs, enabled = @Enabled,
                degraded_p95_ms = @DegradedP95Ms, down_consecutive_failures = @DownConsecutiveFailures,
                -- a shorter interval takes effect now rather than after the old one elapses
                next_due_at = LEAST(next_due_at, now() + make_interval(secs => @IntervalSeconds))
            WHERE id = @Id
            RETURNING {Columns}
            """,
            new
            {
                Id = id, input.Name, input.Url, input.Method, input.ExpectedStatus, input.IntervalSeconds,
                input.TimeoutMs, input.Enabled, input.DegradedP95Ms, input.DownConsecutiveFailures,
            },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM monitors WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<MonitorDefinition?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MonitorDefinition>(new CommandDefinition(
            $"SELECT {Columns} FROM monitors WHERE id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Monitors with their current state (latest incident row, 'ok' if none), last check and live
    /// p95 over <paramref name="window"/>. Pass <paramref name="id"/> to fetch a single monitor.
    /// </summary>
    public async Task<IReadOnlyList<MonitorStatusRow>> ListWithStatusAsync(TimeSpan window, long? id = null, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MonitorStatusRow>(new CommandDefinition("""
            SELECT m.id, m.name, m.url, m.method, m.expected_status, m.interval_seconds, m.timeout_ms, m.enabled,
                   m.degraded_p95_ms, m.down_consecutive_failures, m.next_due_at, m.created_at,
                   coalesce(li.state, 'ok') AS state,
                   li.updated_at            AS state_changed_at,
                   lc.checked_at            AS last_checked_at,
                   lc.success               AS last_success,
                   lc.latency_ms            AS last_latency_ms,
                   lc.status_code           AS last_status_code,
                   lc.error                 AS last_error,
                   lp.p95_ms                AS live_p95_ms
            FROM monitors m
            LEFT JOIN LATERAL (
                SELECT i.state, i.updated_at
                FROM incidents i
                WHERE i.monitor_id = m.id
                ORDER BY i.id DESC
                LIMIT 1
            ) li ON true
            LEFT JOIN LATERAL (
                SELECT c.checked_at, c.success, c.latency_ms, c.status_code, c.error
                FROM check_results c
                WHERE c.monitor_id = m.id
                ORDER BY c.checked_at DESC
                LIMIT 1
            ) lc ON true
            LEFT JOIN LATERAL (
                SELECT percentile_disc(0.95) WITHIN GROUP (ORDER BY c.latency_ms) AS p95_ms
                FROM check_results c
                WHERE c.monitor_id = m.id AND c.checked_at > now() - @window
            ) lp ON true
            WHERE @id::bigint IS NULL OR m.id = @id::bigint
            ORDER BY m.id
            """, new { window, id }, cancellationToken: ct));
        return rows.AsList();
    }
}
