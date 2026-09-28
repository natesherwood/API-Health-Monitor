using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.State;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Evaluation;
using RateMyApi.Worker.Notifications;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 5: workers evaluating the same transition simultaneously produce one incident.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A5_ConcurrentTransitionTests(PostgresFixture postgres)
{
    private const int Evaluators = 8;

    private static readonly Dictionary<string, string?> WebhookOnly = new()
    {
        ["Notifications:Webhook:Url"] = "http://127.0.0.1:9/hook",
    };

    [Fact]
    public async Task Evaluators_racing_to_open_the_same_incident_yield_one_incident_and_the_losers_hit_23505()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://racy.test/"));
        var transition = new StateTransition(MonitorState.Ok, MonitorState.Down, TransitionKind.Open, "3 consecutive failures (threshold 3)");

        // Eight independent "worker processes" (own pools) all observe state=ok before anyone writes,
        // then fire the same transition at once. Nothing but the partial unique index arbitrates.
        var workers = Enumerable.Range(0, Evaluators).Select(_ => db.CreateWorkerServices(WebhookOnly)).ToArray();
        var allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;

        var attempts = workers.Select(async services =>
        {
            var incidents = services.GetRequiredService<IncidentRepository>();
            var composer = services.GetRequiredService<NotificationComposer>();

            var head = await incidents.GetLatestAsync(monitor.Id);
            Assert.Null(head);
            if (Interlocked.Increment(ref readers) == Evaluators)
                allRead.SetResult();
            await go.Task;

            return await incidents.ApplyTransitionAsync(
                monitor.Id, head, transition, 1234, id => composer.Compose(monitor, transition, 1234, id));
        }).ToArray();

        await allRead.Task;
        go.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Outcome == TransitionOutcome.Applied);
        Assert.Equal(Evaluators - 1, results.Count(r => r.Outcome == TransitionOutcome.Deduplicated));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM incidents WHERE monitor_id = @id", new { id = monitor.Id }));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM notifications"));
    }

    [Fact]
    public async Task Full_evaluations_running_simultaneously_on_many_workers_open_exactly_one_incident()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://racy.test/", downConsecutiveFailures: 3));
        await InsertFailuresAsync(db, monitor.Id, 3);

        var evaluators = Enumerable.Range(0, Evaluators)
            .Select(_ => db.CreateWorkerServices(WebhookOnly).GetRequiredService<IMonitorEvaluator>())
            .ToArray();
        var results = await Task.WhenAll(evaluators.Select(e => Task.Run(() => e.EvaluateAsync(monitor))));

        Assert.Single(results, r => r.Persisted?.Outcome == TransitionOutcome.Applied);
        Assert.All(results.Where(r => r.Persisted?.Outcome != TransitionOutcome.Applied), r =>
            Assert.True(r.Transition is null || r.Persisted!.Outcome is TransitionOutcome.Deduplicated or TransitionOutcome.Stale));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM incidents"));
        Assert.Equal("down", await db.ScalarAsync<string>("SELECT state FROM incidents"));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM notifications"));
    }

    [Fact]
    public async Task Evaluators_racing_to_resolve_the_same_incident_apply_it_once()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://racy.test/", downConsecutiveFailures: 3));
        await InsertFailuresAsync(db, monitor.Id, 3);
        var opener = db.CreateWorkerServices(WebhookOnly).GetRequiredService<IMonitorEvaluator>();
        Assert.Equal(TransitionOutcome.Applied, (await opener.EvaluateAsync(monitor)).Persisted!.Outcome);

        var resolve = new StateTransition(MonitorState.Down, MonitorState.Resolved, TransitionKind.Resolve, "recovered");
        var workers = Enumerable.Range(0, Evaluators).Select(_ => db.CreateWorkerServices(WebhookOnly)).ToArray();
        var allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;

        var attempts = workers.Select(async services =>
        {
            var incidents = services.GetRequiredService<IncidentRepository>();
            var composer = services.GetRequiredService<NotificationComposer>();

            var head = await incidents.GetLatestAsync(monitor.Id);
            Assert.Equal("down", head!.State);
            if (Interlocked.Increment(ref readers) == Evaluators)
                allRead.SetResult();
            await go.Task;

            return await incidents.ApplyTransitionAsync(
                monitor.Id, head, resolve, 40, id => composer.Compose(monitor, resolve, 40, id));
        }).ToArray();

        await allRead.Task;
        go.SetResult();
        var results = await Task.WhenAll(attempts);

        // Compare-and-set on the state that was read: exactly one down -> resolved is applied.
        Assert.Single(results, r => r.Outcome == TransitionOutcome.Applied);
        Assert.Equal(Evaluators - 1, results.Count(r => r.Outcome == TransitionOutcome.Stale));
        Assert.Equal("resolved", await db.ScalarAsync<string>("SELECT state FROM incidents"));
        Assert.Equal(2, await db.ScalarAsync<long>("SELECT count(*) FROM notifications")); // open + resolve
    }

    private static async Task InsertFailuresAsync(TestDatabase db, long monitorId, int count)
    {
        var repository = new CheckResultRepository(db.DataSource);
        var start = DateTime.UtcNow.AddSeconds(-count);
        for (var i = 0; i < count; i++)
            await repository.InsertAsync(new CheckOutcome(monitorId, start.AddSeconds(i), null, null, false, CheckOutcome.TimeoutError));
    }
}
