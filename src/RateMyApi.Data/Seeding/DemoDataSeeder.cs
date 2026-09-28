using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using RateMyApi.Data.Partitions;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Data.Seeding;

/// <param name="Path">Path on the API's /demo/targets surface.</param>
/// <param name="StatusSql">SQL expression for the synthetic status code of one backfilled check (may use random()).</param>
/// <param name="LatencySql">SQL expression for its latency in ms; may reference the check time <c>ts</c>.</param>
public sealed record DemoMonitorSpec(
    string Name,
    string Path,
    int IntervalSeconds,
    int TimeoutMs,
    int DegradedP95Ms,
    int DownConsecutiveFailures,
    string StatusSql,
    string LatencySql);

/// <summary>
/// First-boot demo data: monitors pointing at the API's own /demo/targets (so the stack is
/// self-contained and exercises every state) plus optional synthetic history so the 90-hour uptime
/// bar and sparklines have something to show. Runs only when the monitors table is empty.
/// </summary>
public sealed class DemoDataSeeder(
    NpgsqlDataSource dataSource,
    PartitionManager partitions,
    StatsRepository stats,
    TimeProvider time,
    ILogger<DemoDataSeeder> logger)
{
    public static readonly IReadOnlyList<DemoMonitorSpec> Monitors =
    [
        new("demo-ok", "/demo/targets/ok", 10, 2_000, 250, 3, "200", "(8 + random() * 30)::int"),
        new("demo-slow", "/demo/targets/slow", 10, 3_000, 300, 3, "200", "(350 + random() * 550)::int"),
        new("demo-flaky", "/demo/targets/flaky", 5, 2_000, 250, 3, "CASE WHEN random() < 0.25 THEN 500 ELSE 200 END", "(20 + random() * 60)::int"),
        new("demo-wave", "/demo/targets/wave", 5, 3_000, 300, 3, "200",
            "greatest(20, 180 + 220 * sin(2 * pi() * extract(epoch FROM ts) / 1200.0) + random() * 40)::int"),
        new("demo-down", "/demo/targets/down", 15, 2_000, 500, 2, "503", "(1 + random() * 4)::int"),
        new("demo-timeout", "/demo/targets/hang", 15, 1_500, 1_000, 2, "NULL::int", "NULL::int"),
        new("self-healthz", "/healthz", 10, 2_000, 200, 3, "200", "(2 + random() * 6)::int"),
    ];

    /// <returns>True if data was seeded, false if monitors already existed.</returns>
    public async Task<bool> SeedAsync(string targetBaseUrl, int backfillHours, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        backfillHours = Math.Clamp(backfillHours, 0, 24 * 30);

        // Rollover only guarantees current + future months; the backfill may reach into the past one.
        for (var month = PartitionManager.MonthStart(now.AddHours(-backfillHours)); month <= now; month = month.AddMonths(1))
            await partitions.EnsurePartitionForAsync(month, ct);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)", new { key = AdvisoryLocks.Seed }, tx, cancellationToken: ct));

        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM monitors", transaction: tx, cancellationToken: ct)) > 0)
            return false;

        foreach (var (spec, ordinal) in Monitors.Select((s, i) => (s, i)))
        {
            // First checks start a few seconds out (staggered) so they hit a warmed-up API, not one
            // that is still seeding and JIT-compiling.
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO monitors (name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                                      degraded_p95_ms, down_consecutive_failures, next_due_at)
                VALUES (@Name, @Url, 'GET', 200, @IntervalSeconds, @TimeoutMs, true, @DegradedP95Ms, @DownConsecutiveFailures,
                        now() + make_interval(secs => 10 + @ordinal))
                RETURNING id
                """,
                new { spec.Name, Url = targetBaseUrl.TrimEnd('/') + spec.Path, spec.IntervalSeconds, spec.TimeoutMs, spec.DegradedP95Ms, spec.DownConsecutiveFailures, ordinal },
                tx, cancellationToken: ct));

            if (backfillHours == 0)
                continue;

            // Expressions come from the static table above, never from input. The volatile target list
            // keeps the subquery from being flattened, so random() is evaluated per row.
            await conn.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success, error)
                SELECT @id, g.ts, g.status_code, g.latency_ms,
                       coalesce(g.status_code = 200, false),
                       CASE WHEN g.status_code = 200 THEN NULL
                            WHEN g.status_code IS NULL THEN 'timeout'
                            ELSE 'expected status 200, got ' || g.status_code END
                FROM (
                    SELECT ts, {spec.StatusSql} AS status_code, {spec.LatencySql} AS latency_ms
                    FROM generate_series(date_trunc('minute', now()) - make_interval(hours => @hours),
                                         date_trunc('minute', now()) - interval '10 minutes',
                                         interval '1 minute') AS ts
                ) g
                """, new { id, hours = backfillHours }, tx, commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        await stats.RefreshAsync(ct);

        logger.LogInformation("Seeded {Count} demo monitors targeting {BaseUrl} with {Hours}h of history",
            Monitors.Count, targetBaseUrl, backfillHours);
        return true;
    }
}
