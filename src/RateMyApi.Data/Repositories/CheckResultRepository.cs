using Dapper;
using Npgsql;
using RateMyApi.Core.Monitoring;

namespace RateMyApi.Data.Repositories;

/// <summary>Inputs for one state-machine evaluation, read in a single round trip.</summary>
public sealed record HealthInputs
{
    /// <summary>Live p95 over the sliding window (null when no response latencies in the window).</summary>
    public int? P95Ms { get; init; }

    /// <summary>Number of latency samples (non-null latency_ms) the p95 was computed from.</summary>
    public long LatencySamples { get; init; }

    /// <summary>Success flags of the most recent checks, newest first.</summary>
    public bool[] RecentSuccesses { get; init; } = [];
}

public sealed class CheckResultRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CheckResultRepository(NpgsqlDataSource dataSource)
    {
        Db.ConfigureDapper();
        _dataSource = dataSource;
    }

    public async Task InsertAsync(CheckOutcome outcome, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success, error)
            VALUES (@MonitorId, @CheckedAt, @StatusCode, @LatencyMs, @Success, @Error)
            """, outcome, cancellationToken: ct));
    }

    /// <summary>Set-based insert of many results in one statement (used by seeding and tests).</summary>
    public async Task InsertManyAsync(IReadOnlyCollection<CheckOutcome> outcomes, CancellationToken ct = default)
    {
        if (outcomes.Count == 0)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success, error)
            SELECT u.monitor_id, u.checked_at, u.status_code, u.latency_ms, u.success, u.error
            FROM unnest(@monitorIds, @checkedAts, @statusCodes, @latencies, @successes, @errors)
                 AS u(monitor_id, checked_at, status_code, latency_ms, success, error)
            """,
            new
            {
                monitorIds = outcomes.Select(o => o.MonitorId).ToArray(),
                checkedAts = outcomes.Select(o => o.CheckedAt).ToArray(),
                statusCodes = outcomes.Select(o => o.StatusCode).ToArray(),
                latencies = outcomes.Select(o => o.LatencyMs).ToArray(),
                successes = outcomes.Select(o => o.Success).ToArray(),
                errors = outcomes.Select(o => o.Error).ToArray(),
            },
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>
    /// Live sliding-window p95 (the alerting signal) plus the newest <paramref name="recentCount"/>
    /// success flags, from which the consecutive-failure streak is derived.
    /// </summary>
    public async Task<HealthInputs> GetHealthInputsAsync(long monitorId, TimeSpan window, int recentCount, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<HealthInputs>(new CommandDefinition("""
            SELECT
              w.p95_ms,
              w.latency_samples,
              (SELECT coalesce(array_agg(r.success ORDER BY r.checked_at DESC), '{}')
               FROM (SELECT success, checked_at
                     FROM check_results
                     WHERE monitor_id = @id
                     ORDER BY checked_at DESC
                     LIMIT @recentCount) r) AS recent_successes
            FROM (
              SELECT percentile_disc(0.95) WITHIN GROUP (ORDER BY latency_ms) AS p95_ms,
                     count(latency_ms) AS latency_samples
              FROM check_results
              WHERE monitor_id = @id AND checked_at > now() - @window
            ) w
            """,
            new { id = monitorId, window, recentCount },
            cancellationToken: ct));
    }
}
