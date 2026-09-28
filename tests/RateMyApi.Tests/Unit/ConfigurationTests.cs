using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RateMyApi.Data.Migrations;
using RateMyApi.Data.Partitions;
using RateMyApi.Worker;
using RateMyApi.Worker.Sharding;

namespace RateMyApi.Tests.Unit;

public sealed class ConfigurationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings.TryAdd("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=none");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRateMyApiWorker(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Retention_binds_from_the_Retention_section_with_defaults()
    {
        using var defaults = Build([]);
        Assert.Equal(6, defaults.GetRequiredService<IOptions<RetentionOptions>>().Value.Months);
        Assert.Equal(2, defaults.GetRequiredService<IOptions<RetentionOptions>>().Value.PartitionsAhead);

        using var configured = Build(new() { ["Retention:Months"] = "3", ["Retention:PartitionsAhead"] = "4" });
        Assert.Equal(3, configured.GetRequiredService<IOptions<RetentionOptions>>().Value.Months);
        Assert.Equal(4, configured.GetRequiredService<IOptions<RetentionOptions>>().Value.PartitionsAhead);
    }

    [Fact]
    public async Task Explicit_worker_id_and_count_give_a_static_shard_and_env_vars_win()
    {
        using var services = Build(new() { ["Worker:WorkerId"] = "0", ["WORKER_ID"] = "2", ["WORKER_COUNT"] = "3" });
        var provider = Assert.IsType<StaticShardProvider>(services.GetRequiredService<IShardProvider>());
        Assert.Equal(new Core.Sharding.ShardAssignment(2, 3), await provider.GetCurrentAsync(default));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("auto", "3")]
    [InlineData(null, null)]
    public void Auto_worker_ids_lease_slots(string? workerId, string? workerCount)
    {
        using var services = Build(new() { ["WORKER_ID"] = workerId, ["WORKER_COUNT"] = workerCount });
        Assert.IsType<LeasedShardProvider>(services.GetRequiredService<IShardProvider>());
    }

    [Fact]
    public void Explicit_worker_id_without_a_count_is_rejected()
    {
        using var services = Build(new() { ["WORKER_ID"] = "1", ["WORKER_COUNT"] = "auto" });
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IShardProvider>());
    }

    [Fact]
    public void Migrations_are_embedded_numbered_and_ordered()
    {
        var scripts = MigrationRunner.LoadEmbedded();
        Assert.True(scripts.Count >= 5);
        Assert.Equal(scripts.Select(s => s.Version).Order(), scripts.Select(s => s.Version));
        Assert.Equal(1, scripts[0].Version);
        Assert.All(scripts, s => Assert.DoesNotContain("SELECT *", s.Sql, StringComparison.OrdinalIgnoreCase));
    }
}
