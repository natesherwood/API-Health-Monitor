using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Sharding;
using RateMyApi.Data.Repositories;
using RateMyApi.Worker.Sharding;

namespace RateMyApi.Worker.Checks;

/// <summary>
/// The claim loop. Each tick: resolve the shard, claim+reschedule a batch in one transaction, run it.
/// Stopping only stops claiming: a batch that was claimed always runs to completion (bounded by the
/// monitors' own timeouts), so shutdown and re-sharding never drop a claimed check.
/// </summary>
public sealed class CheckScheduler(
    IShardProvider shards,
    MonitorRepository monitors,
    CheckBatchRunner runner,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<CheckScheduler> logger) : BackgroundService
{
    /// <summary>Raised after a batch is claimed and before it runs (diagnostics and tests).</summary>
    public event Action<ShardAssignment, IReadOnlyList<MonitorDefinition>>? Claimed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(10, settings.PollIntervalMs));
        ShardAssignment? lastShard = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimedCount = 0;
            try
            {
                var shard = await shards.GetCurrentAsync(stoppingToken);
                if (shard != lastShard)
                {
                    logger.LogInformation("Claiming as shard {Shard}", shard?.ToString() ?? "(standby)");
                    lastShard = shard;
                }

                if (shard is { } current && !stoppingToken.IsCancellationRequested)
                {
                    // Not cancellable: if the claim commits we must run what we claimed.
                    var claimed = await monitors.ClaimDueAsync(current, settings.BatchSize, CancellationToken.None);
                    claimedCount = claimed.Count;
                    if (claimedCount > 0)
                    {
                        Claimed?.Invoke(current, claimed);
                        await runner.RunAsync(claimed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Claim tick failed; retrying after {Delay}", pollInterval);
            }

            // A full batch means more work is probably due: go again immediately.
            if (claimedCount >= settings.BatchSize)
                continue;

            try
            {
                await Task.Delay(pollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
