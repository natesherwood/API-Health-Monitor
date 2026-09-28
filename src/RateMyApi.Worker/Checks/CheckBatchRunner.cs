using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Monitoring;
using RateMyApi.Data.Repositories;
using RateMyApi.Worker.Evaluation;

namespace RateMyApi.Worker.Checks;

/// <param name="CompletedAfter">Time from batch start until this check's result was persisted and evaluated.</param>
public sealed record ExecutedCheck(MonitorDefinition Monitor, CheckOutcome Outcome, TimeSpan CompletedAfter);

/// <summary>
/// Runs one claimed batch: execute -> persist -> evaluate, concurrently with a bounded DOP.
/// Every monitor is isolated: a timeout, an HTTP error or even a failed insert affects only that monitor.
/// </summary>
public sealed class CheckBatchRunner(
    CheckExecutor executor,
    CheckResultRepository results,
    IMonitorEvaluator evaluator,
    IOptions<WorkerOptions> options,
    ILogger<CheckBatchRunner> logger)
{
    public async Task<IReadOnlyList<ExecutedCheck>> RunAsync(IReadOnlyList<MonitorDefinition> monitors, CancellationToken abortToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var executed = new ConcurrentBag<ExecutedCheck>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxParallelism) };

        await Parallel.ForEachAsync(monitors, parallel, async (monitor, _) =>
        {
            CheckOutcome outcome;
            try
            {
                outcome = await executor.ExecuteAsync(monitor, abortToken);
            }
            catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Check for monitor {MonitorId} threw", monitor.Id);
                outcome = new CheckOutcome(monitor.Id, DateTime.UtcNow, null, null, false, $"internal error: {ex.GetType().Name}");
            }

            try
            {
                // Persisting and evaluating are deliberately not cancellable: a claimed check that
                // ran must be recorded, or graceful shutdown/re-sharding would drop it.
                await results.InsertAsync(outcome, CancellationToken.None);
                await evaluator.EvaluateAsync(monitor, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Persisting/evaluating check for monitor {MonitorId} failed", monitor.Id);
            }

            executed.Add(new ExecutedCheck(monitor, outcome, Stopwatch.GetElapsedTime(started)));
        });

        return executed.ToArray();
    }
}
