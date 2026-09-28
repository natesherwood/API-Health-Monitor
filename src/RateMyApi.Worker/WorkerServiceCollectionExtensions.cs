using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RateMyApi.Core.Sharding;
using RateMyApi.Data;
using RateMyApi.Worker.Checks;
using RateMyApi.Worker.Evaluation;
using RateMyApi.Worker.Maintenance;
using RateMyApi.Worker.Notifications;
using RateMyApi.Worker.Sharding;

namespace RateMyApi.Worker;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>Registers everything the worker needs. Hosted services are added separately by <see cref="AddRateMyApiWorkerHostedServices"/>.</summary>
    public static IServiceCollection AddRateMyApiWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateMyApiData(configuration);

        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.PostConfigure<WorkerOptions>(o =>
        {
            // The documented knobs are plain env vars; they win over the Worker section.
            o.WorkerId = configuration["WORKER_ID"] ?? o.WorkerId;
            o.WorkerCount = configuration["WORKER_COUNT"] ?? o.WorkerCount;
        });
        services.Configure<EvaluationOptions>(configuration.GetSection(EvaluationOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<StatsOptions>(configuration.GetSection(StatsOptions.SectionName));

        services.AddHttpClient(CheckExecutor.HttpClientName, client =>
            {
                // Per-check CancellationTokenSource(timeout_ms) is the only deadline.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RateMyApi/1.0 (+health-check)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Pooled handler; recycle connections so DNS changes of monitored hosts are picked up.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 64,
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddHttpClient(WebhookSender.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<IShardProvider>(CreateShardProvider);
        services.AddSingleton<CheckExecutor>();
        services.AddSingleton<NotificationComposer>();
        services.AddSingleton<IMonitorEvaluator, MonitorEvaluator>();
        services.AddSingleton<CheckBatchRunner>();
        services.AddSingleton<INotificationSender, WebhookSender>();
        services.AddSingleton<INotificationSender, EmailSender>();

        services.AddSingleton<PartitionRolloverService>();
        services.AddSingleton<CheckScheduler>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<StatsRefreshService>();
        return services;
    }

    public static IServiceCollection AddRateMyApiWorkerHostedServices(this IServiceCollection services)
    {
        // Start order matters: rollover's StartAsync runs to completion before the claim loop starts.
        services.AddHostedService(sp => sp.GetRequiredService<PartitionRolloverService>());
        services.AddHostedService(sp => sp.GetRequiredService<CheckScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<StatsRefreshService>());
        return services;
    }

    private static IShardProvider CreateShardProvider(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
        var idAuto = WorkerOptions.IsAuto(o.WorkerId);
        var countAuto = WorkerOptions.IsAuto(o.WorkerCount);

        if (!idAuto && !countAuto)
        {
            var shard = new ShardAssignment(
                WorkerOptions.ParseInt(o.WorkerId!, "WORKER_ID"),
                WorkerOptions.ParseInt(o.WorkerCount!, "WORKER_COUNT"));
            return new StaticShardProvider(shard);
        }

        if (!idAuto)
            throw new InvalidOperationException("WORKER_ID requires an explicit WORKER_COUNT (or set both to 'auto').");

        int? fixedCount = countAuto ? null : WorkerOptions.ParseInt(o.WorkerCount!, "WORKER_COUNT");
        if (fixedCount is < 1)
            throw new InvalidOperationException("WORKER_COUNT must be >= 1.");

        return new LeasedShardProvider(
            sp.GetRequiredService<NpgsqlDataSource>(),
            fixedCount,
            Math.Max(1, o.MaxSlots),
            TimeSpan.FromMilliseconds(Math.Max(100, o.MembershipRefreshMs)),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LeasedShardProvider>>());
    }
}
