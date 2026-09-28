using RateMyApi.Tests.Infrastructure;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 1: two workers with WORKER_COUNT=2 never execute the same check.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A1_ShardedClaimTests(PostgresFixture postgres)
{
    private const int MonitorCount = 400;

    [Fact]
    public async Task Two_workers_with_worker_count_2_never_execute_the_same_check()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();
        await SeedDueMonitorsAsync(db, target.Url("/ok"));

        await using var fleet = new WorkerFleet(db);
        await Task.WhenAll(fleet.StartAsync("0", "2"), fleet.StartAsync("1", "2"));

        await Eventually.UntilAsync(
            async () => await db.ScalarAsync<long>("SELECT count(*) FROM check_results") >= MonitorCount,
            TimeSpan.FromSeconds(60), $"all {MonitorCount} monitors have been checked");
        await Task.Delay(1000); // give a hypothetical duplicate time to show up
        await fleet.StopAllAsync();

        await AssertExactlyOnceAsync(db);

        // Each worker only ever claimed its own modulo bucket, and no monitor was claimed twice.
        var claims = fleet.Claims.ToArray();
        Assert.All(claims, c => Assert.Equal(c.Shard.WorkerId, (int)(c.MonitorId % 2)));
        Assert.Equal(MonitorCount, claims.Length);
        Assert.Equal(MonitorCount, claims.Select(c => c.MonitorId).Distinct().Count());
        Assert.Equal(2, claims.Select(c => c.Worker).Distinct().Count());
    }

    [Fact]
    public async Task Overlapping_claimers_on_the_same_shard_still_never_execute_a_check_twice()
    {
        // Worst case for the claim query alone: four workers all believe they own everything (0/1),
        // e.g. mid re-shard. Exclusivity must come from FOR UPDATE SKIP LOCKED + atomic reschedule.
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();
        await SeedDueMonitorsAsync(db, target.Url("/ok"));

        await using var fleet = new WorkerFleet(db);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => fleet.StartAsync("0", "1", name: $"overlap-{i}", batchSize: 10)));

        await Eventually.UntilAsync(
            async () => await db.ScalarAsync<long>("SELECT count(*) FROM check_results") >= MonitorCount,
            TimeSpan.FromSeconds(60), $"all {MonitorCount} monitors have been checked");
        await Task.Delay(1000);
        await fleet.StopAllAsync();

        await AssertExactlyOnceAsync(db);
        var claims = fleet.Claims.ToArray();
        Assert.Equal(MonitorCount, claims.Select(c => c.MonitorId).Distinct().Count());
        Assert.Equal(MonitorCount, claims.Length);
        Assert.True(claims.Select(c => c.Worker).Distinct().Count() > 1, "expected the claims to be spread over several workers");
    }

    internal static Task SeedDueMonitorsAsync(TestDatabase db, string url) =>
        // interval 3600s: within the test each monitor may legitimately run exactly once.
        db.ExecuteAsync("""
            INSERT INTO monitors (name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                                  degraded_p95_ms, down_consecutive_failures, next_due_at)
            SELECT 'm' || g, @url, 'GET', 200, 3600, 2000, true, 1000, 3, now()
            FROM generate_series(1, @count) g
            """, new { url, count = MonitorCount });

    internal static async Task AssertExactlyOnceAsync(TestDatabase db)
    {
        var duplicates = await db.ScalarAsync<long>("""
            SELECT count(*) FROM (
                SELECT monitor_id, checked_at FROM check_results GROUP BY monitor_id, checked_at HAVING count(*) > 1
            ) d
            """);
        Assert.Equal(0, duplicates);

        var perMonitor = await db.QueryAsync<(long MonitorId, long Results)>("""
            SELECT m.id, count(c.id)
            FROM monitors m LEFT JOIN check_results c ON c.monitor_id = m.id
            GROUP BY m.id
            HAVING count(c.id) <> 1
            """);
        Assert.True(perMonitor.Count == 0,
            "every monitor must have exactly one result; offenders: " +
            string.Join(", ", perMonitor.Take(10).Select(p => $"#{p.MonitorId}={p.Results}")));
    }
}
