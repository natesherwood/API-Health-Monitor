using System.Diagnostics;
using RateMyApi.Tests.Infrastructure;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 2: scaling WORKER_COUNT 2 -> 3 mid-run drops zero checks and duplicates none.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A2_ReshardingTests(PostgresFixture postgres)
{
    private const int MonitorCount = 450;
    private static readonly TimeSpan DueSpread = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task Scaling_worker_count_from_2_to_3_mid_run_drops_zero_checks_and_duplicates_none()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();

        // Monitors become due continuously over ~6s so the re-shard happens while work is flowing.
        // interval 3600s: each monitor must be executed exactly once during the test.
        await db.ExecuteAsync("""
            INSERT INTO monitors (name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                                  degraded_p95_ms, down_consecutive_failures, next_due_at)
            SELECT 'm' || g, @url, 'GET', 200, 3600, 2000, true, 1000, 3,
                   now() + make_interval(secs => g * @spreadSeconds / @count)
            FROM generate_series(1, @count) g
            """, new { url = target.Url("/ok"), count = MonitorCount, spreadSeconds = DueSpread.TotalSeconds });

        await using var fleet = new WorkerFleet(db);
        var clock = Stopwatch.StartNew();

        // Generation 1: WORKER_COUNT=2.
        var gen1 = await Task.WhenAll(fleet.StartAsync("0", "2"), fleet.StartAsync("1", "2"));
        await Task.Delay(DueSpread / 2);

        // Roll out generation 2 (WORKER_COUNT=3) before retiring generation 1, like a rolling deploy:
        // for a while both modulo layouts claim concurrently (overlap), then the old one drains.
        var gen2 = await Task.WhenAll(fleet.StartAsync("0", "3"), fleet.StartAsync("1", "3"), fleet.StartAsync("2", "3"));
        await Task.Delay(500);
        await Task.WhenAll(gen1.Select(fleet.StopAsync));
        var switchedAt = clock.Elapsed;

        await Eventually.UntilAsync(
            async () => await db.ScalarAsync<long>("SELECT count(*) FROM check_results") >= MonitorCount,
            TimeSpan.FromSeconds(60), $"all {MonitorCount} monitors have been checked");
        await Task.Delay(1000);
        await fleet.StopAllAsync();

        // Zero dropped + zero duplicated: exactly one result per monitor, no (monitor_id, checked_at) twice.
        await A1_ShardedClaimTests.AssertExactlyOnceAsync(db);

        var claims = fleet.Claims.ToArray();
        Assert.Equal(MonitorCount, claims.Length);
        Assert.Equal(MonitorCount, claims.Select(c => c.MonitorId).Distinct().Count());

        // The re-shard really happened mid-run: both layouts did a meaningful share of the work,
        // and each claim respected the layout of the worker that made it.
        var byGen1 = claims.Count(c => gen1.Contains(c.Worker));
        var byGen2 = claims.Count(c => gen2.Contains(c.Worker));
        Assert.True(byGen1 > MonitorCount / 10, $"generation 1 claimed only {byGen1} (switch at {switchedAt})");
        Assert.True(byGen2 > MonitorCount / 10, $"generation 2 claimed only {byGen2} (switch at {switchedAt})");
        Assert.All(claims, c => Assert.Equal(c.Shard.WorkerId, (int)(c.MonitorId % c.Shard.WorkerCount)));
    }
}
