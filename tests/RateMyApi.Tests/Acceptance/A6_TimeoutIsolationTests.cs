using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Sharding;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Checks;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 6: a timeout on one monitor does not delay or fail the rest of the batch.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A6_TimeoutIsolationTests(PostgresFixture postgres)
{
    private const int TimeoutMs = 2_000;
    private const int FastMonitors = 40;

    [Fact]
    public async Task A_timeout_on_one_monitor_does_not_delay_or_fail_the_rest_of_the_batch()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();

        var hangingId = await db.InsertMonitorAsync(target.Url("/hang"), intervalSeconds: 60, timeoutMs: TimeoutMs, name: "hangs");
        for (var i = 0; i < FastMonitors; i++)
            await db.InsertMonitorAsync(target.Url("/delay/20"), intervalSeconds: 60, timeoutMs: TimeoutMs, name: $"fast-{i}");

        var services = db.CreateWorkerServices(new Dictionary<string, string?> { ["Worker:MaxParallelism"] = "8" });
        var claimed = await services.GetRequiredService<MonitorRepository>().ClaimDueAsync(new ShardAssignment(0, 1), 100);
        Assert.Equal(FastMonitors + 1, claimed.Count);

        // Worst case for a sequential executor: the hanging check is first in the batch.
        var batch = claimed.OrderBy(m => m.Id == hangingId ? 0 : 1).ThenBy(m => m.Id).ToList();

        var clock = Stopwatch.StartNew();
        var executed = await services.GetRequiredService<CheckBatchRunner>().RunAsync(batch);
        var batchDuration = clock.Elapsed;

        Assert.Equal(FastMonitors + 1, executed.Count);

        var hung = Assert.Single(executed, e => e.Monitor.Id == hangingId);
        Assert.False(hung.Outcome.Success);
        Assert.Equal(CheckOutcome.TimeoutError, hung.Outcome.Error);
        Assert.Null(hung.Outcome.LatencyMs);
        Assert.Null(hung.Outcome.StatusCode);
        Assert.True(hung.CompletedAfter >= TimeSpan.FromMilliseconds(TimeoutMs - 50), $"timed out too early: {hung.CompletedAfter}");

        // Not failed: every other check succeeded.
        var fast = executed.Where(e => e.Monitor.Id != hangingId).ToArray();
        Assert.All(fast, e => Assert.True(e.Outcome.Success, $"{e.Monitor.Name}: {e.Outcome.Error}"));

        // Not delayed: all of them were recorded long before the hanging check gave up ...
        var slowest = fast.Max(e => e.CompletedAfter);
        Assert.True(slowest < TimeSpan.FromMilliseconds(TimeoutMs / 2), $"slowest healthy check completed after {slowest}");
        // ... and the batch as a whole costs one timeout, not more.
        Assert.True(batchDuration < TimeSpan.FromMilliseconds(TimeoutMs + 1_000), $"batch took {batchDuration}");

        // Everything was persisted, including the timeout.
        Assert.Equal(FastMonitors + 1, await db.ScalarAsync<long>("SELECT count(*) FROM check_results"));
        Assert.Equal(1, await db.ScalarAsync<long>(
            "SELECT count(*) FROM check_results WHERE monitor_id = @hangingId AND NOT success AND error = 'timeout' AND latency_ms IS NULL",
            new { hangingId }));
    }

    [Fact]
    public async Task The_claim_loop_survives_timeouts_and_keeps_checking_other_monitors()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();

        await db.InsertMonitorAsync(target.Url("/hang"), intervalSeconds: 1, timeoutMs: 300, name: "hangs");
        var fastId = await db.InsertMonitorAsync(target.Url("/ok"), intervalSeconds: 1, timeoutMs: 300, name: "fast");

        await using var fleet = new WorkerFleet(db);
        await fleet.StartAsync("0", "1");

        // Several scheduling rounds, each containing a timeout; the loop keeps going.
        await Eventually.UntilAsync(
            async () => await db.ScalarAsync<long>("SELECT count(*) FROM check_results WHERE monitor_id = @fastId", new { fastId }) >= 3,
            TimeSpan.FromSeconds(20), "the healthy monitor has been checked in 3 rounds");
        await fleet.StopAllAsync();

        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM check_results WHERE error = 'timeout'") >= 2);
    }
}
