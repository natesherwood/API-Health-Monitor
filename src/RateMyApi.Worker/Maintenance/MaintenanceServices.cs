using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateMyApi.Data.Partitions;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Worker.Maintenance;

/// <summary>
/// Creates upcoming check_results partitions and drops expired ones. Runs once during startup
/// (StartAsync, so it completes before the claim loop starts inserting) and then daily. Safe to
/// run on every replica: PartitionManager is idempotent and advisory-locked.
/// </summary>
public sealed class PartitionRolloverService(
    PartitionManager partitions,
    TimeProvider time,
    ILogger<PartitionRolloverService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RunOnceAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<RolloverReport?> RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            return await partitions.RunAsync(time.GetUtcNow().UtcDateTime, dropExpired: true, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Partition rollover failed; will retry at the next interval");
            return null;
        }
    }
}

public sealed class StatsOptions
{
    public const string SectionName = "Stats";

    public int RefreshSeconds { get; set; } = 60;
}

/// <summary>Periodically refreshes monitor_hourly_stats with REFRESH MATERIALIZED VIEW CONCURRENTLY.</summary>
public sealed class StatsRefreshService(
    StatsRepository stats,
    IOptions<StatsOptions> options,
    TimeProvider time,
    ILogger<StatsRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.RefreshSeconds)), time);
        try
        {
            do
            {
                try
                {
                    if (!await stats.RefreshAsync(stoppingToken))
                        logger.LogDebug("Stats refresh skipped: another instance holds the lock");
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Stats refresh failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
