using Npgsql;
using Testcontainers.PostgreSql;

namespace RateMyApi.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL 16 server per test run, with a fresh migrated database per test.
/// Uses Testcontainers by default. Set <c>RATEMYAPI_TEST_PG</c> to a connection string for an
/// existing server (maintenance database, superuser) to run without Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ExternalServerVariable = "RATEMYAPI_TEST_PG";

    private PostgreSqlContainer? _container;

    public string ServerConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ServerConnectionString = external;
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithCommand("-c", "max_connections=500")
            .Build();
        await _container.StartAsync();
        ServerConnectionString = _container.GetConnectionString();
    }

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "rmapi_test_" + Guid.NewGuid().ToString("N")[..16];
        await using (var admin = new NpgsqlConnection(ServerConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = name,
            MaxPoolSize = 100,
            IncludeErrorDetail = true,
        }.ConnectionString;

        var database = new TestDatabase(name, connectionString, ServerConnectionString);
        await database.MigrateAsync();
        return database;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
