using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RateMyApi.Data.Partitions;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Maintenance;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 3: the rollover service creates the next-month partition and drops a partition past retention.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A3_PartitionRolloverTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Rollover_creates_next_month_partition_and_drops_partitions_past_retention()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var now = DateTime.UtcNow;
        var month = PartitionManager.MonthStart(now);
        var services = db.CreateWorkerServices(new Dictionary<string, string?>
        {
            ["Retention:Months"] = "6",
            ["Retention:PartitionsAhead"] = "2",
        });
        var manager = services.GetRequiredService<PartitionManager>();

        // Start from a state where the upcoming partitions are missing ...
        await db.ExecuteAsync($"DROP TABLE {PartitionManager.PartitionName(month.AddMonths(1))}");
        await db.ExecuteAsync($"DROP TABLE {PartitionManager.PartitionName(month.AddMonths(2))}");

        // ... and old partitions exist, with data, on both sides of the retention cutoff.
        // cutoff = month - 6: a partition is dropped when its upper bound <= cutoff.
        var expired8 = month.AddMonths(-8);  // upper bound month-7 < cutoff   -> drop
        var expired7 = month.AddMonths(-7);  // upper bound month-6 == cutoff  -> drop
        var retained6 = month.AddMonths(-6); // upper bound month-5 > cutoff   -> keep
        foreach (var old in new[] { expired8, expired7, retained6 })
        {
            await manager.EnsurePartitionForAsync(old);
            await db.ExecuteAsync(
                "INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success) VALUES (1, @at, 200, 10, true)",
                new { at = old.AddDays(3) });
        }

        var service = services.GetRequiredService<PartitionRolloverService>();
        var report = await service.RunOnceAsync();

        Assert.NotNull(report);
        Assert.Equal(
            new[] { PartitionManager.PartitionName(month.AddMonths(1)), PartitionManager.PartitionName(month.AddMonths(2)) },
            report.Created);
        Assert.Equal(
            new[] { PartitionManager.PartitionName(expired8), PartitionManager.PartitionName(expired7) }.Order(),
            report.Dropped.Order());

        var partitions = (await manager.ListPartitionsAsync()).Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { retained6, month, month.AddMonths(1), month.AddMonths(2) }.Select(PartitionManager.PartitionName),
            partitions);
        Assert.False(await db.ScalarAsync<bool>("SELECT to_regclass(@n) IS NOT NULL", new { n = PartitionManager.PartitionName(expired8) }));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM check_results"));

        // Every partition carries its own (monitor_id, checked_at DESC) index.
        foreach (var partition in partitions)
        {
            var indexDefs = await db.QueryAsync<string>("SELECT indexdef FROM pg_indexes WHERE tablename = @partition", new { partition });
            Assert.Contains(indexDefs, d => d.Contains("(monitor_id, checked_at DESC)", StringComparison.Ordinal));
        }

        // Idempotent.
        var again = await service.RunOnceAsync();
        Assert.Empty(again!.Created);
        Assert.Empty(again.Dropped);
    }

    [Fact]
    public async Task Rollover_follows_the_clock_into_the_next_month()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var services = db.CreateWorkerServices();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new PartitionRolloverService(
            services.GetRequiredService<PartitionManager>(), clock, NullLogger<PartitionRolloverService>.Instance);

        var month = PartitionManager.MonthStart(DateTime.UtcNow);
        clock.SetUtcNow(new DateTimeOffset(month.AddMonths(1).AddDays(1)));

        var report = await service.RunOnceAsync();

        // Bootstrap already made month..month+2; a month later, month+3 becomes "2 ahead".
        Assert.Equal(new[] { PartitionManager.PartitionName(month.AddMonths(3)) }, report!.Created);
    }

    [Fact]
    public async Task Concurrent_rollovers_from_many_workers_do_not_race()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var month = PartitionManager.MonthStart(DateTime.UtcNow);
        await db.ExecuteAsync($"DROP TABLE {PartitionManager.PartitionName(month.AddMonths(2))}");

        var managers = Enumerable.Range(0, 8)
            .Select(_ => db.CreateWorkerServices().GetRequiredService<PartitionManager>())
            .ToArray();
        var reports = await Task.WhenAll(managers.Select(m => m.RunAsync(DateTime.UtcNow)));

        // Exactly one of the eight created it; nobody failed.
        Assert.Equal(1, reports.Count(r => r.Created.Count == 1));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass(@n) IS NOT NULL", new { n = PartitionManager.PartitionName(month.AddMonths(2)) }));
    }
}
