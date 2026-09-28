using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.State;
using RateMyApi.Core.Stats;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Evaluation;

namespace RateMyApi.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class EvaluationWindowTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_single_slow_outlier_never_degrades_a_monitor_but_sustained_slowness_does()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var services = db.CreateWorkerServices();
        // interval 10s -> window max(5 min, 20 x 10s) = 5 min, 20 latency samples required.
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://svc.test/", intervalSeconds: 10, timeoutMs: 2000, degradedP95Ms: 250));
        var results = services.GetRequiredService<CheckResultRepository>();
        var evaluator = services.GetRequiredService<IMonitorEvaluator>();
        var at = DateTime.UtcNow.AddMinutes(-4);

        async Task<EvaluationResult> Record(int latencyMs)
        {
            at = at.AddSeconds(1);
            await results.InsertAsync(new CheckOutcome(monitor.Id, at, 200, latencyMs, true, null));
            return await evaluator.EvaluateAsync(monitor);
        }

        // A cold-start outlier first: with fewer than 20 samples p95 is just the max, so no verdict yet.
        Assert.Null((await Record(900)).Transition);
        for (var i = 0; i < 18; i++)
            Assert.Null((await Record(30)).Transition);

        // 20 samples: p95 is the 19th smallest, so one outlier is ignored.
        Assert.Null((await Record(30)).Transition);

        // A second slow response makes p95 slow: now it is a real signal.
        var degraded = await Record(800);
        Assert.Equal((MonitorState.Ok, MonitorState.Degraded), (degraded.Transition!.From, degraded.Transition.To));
        Assert.Equal(TransitionOutcome.Applied, degraded.Persisted!.Outcome);
    }

    [Fact]
    public void Live_window_widens_to_hold_min_samples_within_bounds()
    {
        var @base = TimeSpan.FromMinutes(5);
        var max = TimeSpan.FromMinutes(60);

        Assert.Equal(new LiveWindow(TimeSpan.FromMinutes(5), 20), LiveWindow.For(5, @base, 20, max));   // 100s needed, base wins
        Assert.Equal(new LiveWindow(TimeSpan.FromMinutes(20), 20), LiveWindow.For(60, @base, 20, max)); // widened to 20 intervals
        Assert.Equal(new LiveWindow(TimeSpan.FromMinutes(60), 12), LiveWindow.For(300, @base, 20, max)); // capped: 12 checks fit
        Assert.Equal(new LiveWindow(TimeSpan.FromMinutes(60), 1), LiveWindow.For(3600, @base, 20, max));
        Assert.Equal(new LiveWindow(TimeSpan.FromMinutes(5), 1), LiveWindow.For(10, @base, 1, max));    // MinSamples=1: spec-literal

        var window = LiveWindow.For(10, @base, 20, max);
        Assert.Null(window.Trust(900, 19));
        Assert.Equal(900, window.Trust(900, 20));
    }
}
