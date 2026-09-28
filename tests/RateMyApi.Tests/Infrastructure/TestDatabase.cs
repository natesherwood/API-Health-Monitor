using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RateMyApi.Core.Monitoring;
using RateMyApi.Data;
using RateMyApi.Worker;

namespace RateMyApi.Tests.Infrastructure;

/// <summary>A throwaway migrated database plus helpers to build worker-shaped service providers against it.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _serverConnectionString;
    private readonly List<ServiceProvider> _providers = [];

    public TestDatabase(string name, string connectionString, string serverConnectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        _serverConnectionString = serverConnectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public string Name { get; }
    public string ConnectionString { get; }

    /// <summary>For test-side SQL (setup and assertions).</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// A service provider wired like one worker process (own connection pool, own options).
    /// Hosted services are NOT started; tests start the pieces they need.
    /// </summary>
    public ServiceProvider CreateWorkerServices(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            ["WORKER_ID"] = "0",
            ["WORKER_COUNT"] = "1",
            ["Worker:PollIntervalMs"] = "25",
            ["Worker:MembershipRefreshMs"] = "200",
            ["Notifications:PollIntervalMs"] = "25",
        };
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
                values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRateMyApiWorker(configuration);
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        lock (_providers)
            _providers.Add(provider);
        return provider;
    }

    public async Task MigrateAsync()
    {
        await using var provider = CreateWorkerServices();
        await provider.GetRequiredService<DatabaseBootstrapper>().RunAsync();
    }

    public async Task<long> InsertMonitorAsync(
        string url,
        int intervalSeconds = 3600,
        int timeoutMs = 2000,
        int degradedP95Ms = 500,
        int downConsecutiveFailures = 3,
        string name = "monitor")
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO monitors (name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                                  degraded_p95_ms, down_consecutive_failures, next_due_at)
            VALUES (@name, @url, 'GET', 200, @intervalSeconds, @timeoutMs, true, @degradedP95Ms, @downConsecutiveFailures, now())
            RETURNING id
            """, new { name, url, intervalSeconds, timeoutMs, degradedP95Ms, downConsecutiveFailures });
    }

    public async Task<MonitorDefinition> GetMonitorAsync(long id)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        return await conn.QuerySingleAsync<MonitorDefinition>("""
            SELECT id, name, url, method, expected_status, interval_seconds, timeout_ms, enabled,
                   degraded_p95_ms, down_consecutive_failures, next_due_at, created_at
            FROM monitors WHERE id = @id
            """, new { id });
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("Query returned null: " + sql);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? args = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        return (await conn.QueryAsync<T>(sql, args)).AsList();
    }

    public async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync(sql, args);
    }

    public async ValueTask DisposeAsync()
    {
        List<ServiceProvider> providers;
        lock (_providers)
            providers = [.. _providers];
        foreach (var provider in providers)
            await provider.DisposeAsync();

        await DataSource.DisposeAsync();

        await using var admin = new NpgsqlConnection(_serverConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
