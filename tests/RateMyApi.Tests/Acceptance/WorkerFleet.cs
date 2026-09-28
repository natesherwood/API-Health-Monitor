using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Sharding;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Checks;

namespace RateMyApi.Tests.Acceptance;

public sealed record Claim(string Worker, ShardAssignment Shard, long MonitorId);

/// <summary>Runs real <see cref="CheckScheduler"/> instances, each with its own service provider (= its own process).</summary>
public sealed class WorkerFleet(TestDatabase db) : IAsyncDisposable
{
    private readonly List<(string Name, CheckScheduler Scheduler)> _running = [];

    public ConcurrentQueue<Claim> Claims { get; } = new();

    public async Task<string> StartAsync(string workerId, string workerCount, string? name = null, int batchSize = 25, int maxParallelism = 16)
    {
        name ??= $"w{workerId}/{workerCount}#{_running.Count}";
        var services = db.CreateWorkerServices(new Dictionary<string, string?>
        {
            ["WORKER_ID"] = workerId,
            ["WORKER_COUNT"] = workerCount,
            ["Worker:BatchSize"] = batchSize.ToString(CultureInfo.InvariantCulture),
            ["Worker:MaxParallelism"] = maxParallelism.ToString(CultureInfo.InvariantCulture),
        });

        var scheduler = services.GetRequiredService<CheckScheduler>();
        var workerName = name;
        scheduler.Claimed += (shard, monitors) =>
        {
            foreach (var monitor in monitors)
                Claims.Enqueue(new Claim(workerName, shard, monitor.Id));
        };

        await scheduler.StartAsync(CancellationToken.None);
        lock (_running)
            _running.Add((name, scheduler));
        return name;
    }

    /// <summary>Graceful stop: stops claiming and waits for in-flight batches to finish.</summary>
    public async Task StopAsync(string name)
    {
        CheckScheduler scheduler;
        lock (_running)
        {
            var index = _running.FindIndex(r => r.Name == name);
            scheduler = _running[index].Scheduler;
            _running.RemoveAt(index);
        }
        await scheduler.StopAsync(CancellationToken.None);
    }

    public async Task StopAllAsync()
    {
        List<string> names;
        lock (_running)
            names = _running.Select(r => r.Name).ToList();
        await Task.WhenAll(names.Select(StopAsync));
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
