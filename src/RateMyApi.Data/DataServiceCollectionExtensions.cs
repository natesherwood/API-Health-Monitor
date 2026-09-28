using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using RateMyApi.Data.Migrations;
using RateMyApi.Data.Partitions;
using RateMyApi.Data.Repositories;
using RateMyApi.Data.Seeding;

namespace RateMyApi.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddRateMyApiData(this IServiceCollection services, IConfiguration configuration)
    {
        Db.ConfigureDapper();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");

            var builder = new NpgsqlDataSourceBuilder(connectionString);
            if (sp.GetService<ILoggerFactory>() is { } loggerFactory)
                builder.UseLoggerFactory(loggerFactory);
            return builder.Build();
        });

        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));

        services.TryAddSingleton<MigrationRunner>();
        services.TryAddSingleton<PartitionManager>();
        services.TryAddSingleton<DatabaseBootstrapper>();
        services.TryAddSingleton<MonitorRepository>();
        services.TryAddSingleton<CheckResultRepository>();
        services.TryAddSingleton<IncidentRepository>();
        services.TryAddSingleton<NotificationRepository>();
        services.TryAddSingleton<StatsRepository>();
        services.TryAddSingleton<DemoDataSeeder>();
        return services;
    }
}
