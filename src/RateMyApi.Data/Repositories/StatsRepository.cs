using Dapper;
using Npgsql;

namespace RateMyApi.Data.Repositories;

public sealed record HourlyBucket
{
    public long MonitorId { get; init; }
    public DateTime Bucket { get; init; }
    public int? P95Ms { get; init; }
    public double UptimePct { get; init; }
    public long CheckCount { get; init; }
    public long FailureCount { get; init; }
}

public sealed class StatsRepository
{
    public const string ViewName = "monitor_hourly_stats";
    public const int MaxHours = 168;

    private const string Columns = "monitor_id, bucket, p95_ms, uptime_pct, check_count, failure_count";

    private readonly NpgsqlDataSource _dataSource;

    public StatsRepository(NpgsqlDataSource dataSource)
    {
        Db.ConfigureDapper();
        _dataSource = dataSource;
    }

    /// <summary>
    /// REFRESH MATERIALIZED VIEW CONCURRENTLY: builds the new contents in a temp table and applies the
    /// diff using the unique index, holding only an EXCLUSIVE lock, which does not conflict with the
    /// ACCESS SHARE lock taken by readers. Guarded by a transaction-scoped try-lock so N workers do not
    /// queue up redundant refreshes.
    /// </summary>
    /// <returns>False if another instance was already refreshing.</returns>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_xact_lock(@key)", new { key = AdvisoryLocks.StatsRefresh }, tx, cancellationToken: ct));
        if (!acquired)
            return false;

        await conn.ExecuteAsync(new CommandDefinition(
            $"REFRESH MATERIALIZED VIEW CONCURRENTLY {ViewName}", transaction: tx,
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Hourly buckets for one monitor covering the last <paramref name="hours"/> hours (current hour included).</summary>
    public async Task<IReadOnlyList<HourlyBucket>> GetSeriesAsync(long monitorId, int hours, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<HourlyBucket>(new CommandDefinition($"""
            SELECT {Columns}
            FROM {ViewName}
            WHERE monitor_id = @monitorId
              AND bucket >= date_trunc('hour', now(), 'UTC') - make_interval(hours => @hours - 1)
            ORDER BY bucket
            """, new { monitorId, hours = Math.Clamp(hours, 1, MaxHours) }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Hourly buckets for every monitor over the last <paramref name="hours"/> hours.</summary>
    public async Task<IReadOnlyList<HourlyBucket>> GetAllSeriesAsync(int hours, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<HourlyBucket>(new CommandDefinition($"""
            SELECT {Columns}
            FROM {ViewName}
            WHERE bucket >= date_trunc('hour', now(), 'UTC') - make_interval(hours => @hours - 1)
            ORDER BY monitor_id, bucket
            """, new { hours = Math.Clamp(hours, 1, MaxHours) }, cancellationToken: ct));
        return rows.AsList();
    }
}
