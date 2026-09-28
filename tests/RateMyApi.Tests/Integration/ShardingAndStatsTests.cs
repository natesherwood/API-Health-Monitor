using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Sharding;
using RateMyApi.Core.Stats;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Sharding;

namespace RateMyApi.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class LeasedShardProviderTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Auto_mode_gives_replicas_distinct_shards_and_rebalances_when_one_leaves()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var workers = Enumerable.Range(0, 3).Select(_ => db.CreateWorkerServices(Auto("auto"))).ToArray();
        var providers = workers.Select(w => w.GetRequiredService<IShardProvider>()).ToArray();

        foreach (var provider in providers)
            Assert.NotNull(await provider.GetCurrentAsync(default));

        await Task.Delay(300); // past MembershipRefreshMs, so everyone re-reads membership
        Assert.Equal(
            new[] { new ShardAssignment(0, 3), new ShardAssignment(1, 3), new ShardAssignment(2, 3) },
            await CurrentAsync(providers));

        // A replica dies: its session (and advisory lock) goes away and the survivors re-shard.
        await workers[1].DisposeAsync();
        await Task.Delay(300);
        Assert.Equal(
            new[] { new ShardAssignment(0, 2), new ShardAssignment(1, 2) },
            await CurrentAsync([providers[0], providers[2]]));
    }

    [Fact]
    public async Task Fixed_worker_count_with_auto_ids_puts_extra_replicas_on_standby()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var first = db.CreateWorkerServices(Auto("2"));
        var second = db.CreateWorkerServices(Auto("2"));
        var spare = db.CreateWorkerServices(Auto("2")).GetRequiredService<IShardProvider>();

        Assert.Equal(new ShardAssignment(0, 2), await first.GetRequiredService<IShardProvider>().GetCurrentAsync(default));
        Assert.Equal(new ShardAssignment(1, 2), await second.GetRequiredService<IShardProvider>().GetCurrentAsync(default));
        Assert.Null(await spare.GetCurrentAsync(default));

        await first.DisposeAsync();
        Assert.Equal(new ShardAssignment(0, 2), await spare.GetCurrentAsync(default));
    }

    private static Dictionary<string, string?> Auto(string workerCount) => new()
    {
        ["WORKER_ID"] = "auto",
        ["WORKER_COUNT"] = workerCount,
        ["Worker:MembershipRefreshMs"] = "100",
    };

    private static async Task<ShardAssignment[]> CurrentAsync(IEnumerable<IShardProvider> providers)
    {
        var shards = new List<ShardAssignment>();
        foreach (var provider in providers)
            shards.Add((await provider.GetCurrentAsync(default))!.Value);
        return [.. shards.OrderBy(s => s.WorkerId)];
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PercentileParityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sql_live_window_p95_matches_core_percentile_disc()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var repository = new CheckResultRepository(db.DataSource);
        var random = new Random(1234);

        foreach (var n in new[] { 1, 2, 3, 7, 19, 20, 21, 39, 40, 59, 60, 61, 100, 257 })
        {
            var monitorId = await db.InsertMonitorAsync("http://p95.test/", name: $"n{n}");
            var latencies = Enumerable.Range(0, n).Select(_ => random.Next(10) == 0 ? (int?)null : random.Next(1, 2_000)).ToArray();
            var outcomes = latencies
                .Select((latency, i) => new CheckOutcome(monitorId, DateTime.UtcNow.AddSeconds(-i - 1), 200, latency, latency is not null, null))
                .ToArray();
            await repository.InsertManyAsync(outcomes);

            var sql = await repository.GetHealthInputsAsync(monitorId, TimeSpan.FromHours(1), recentCount: 3);
            Assert.Equal(Percentile.P95(latencies), sql.P95Ms);
        }
    }
}
