using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Notifications;
using RateMyApi.Core.State;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Notifications;

namespace RateMyApi.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class NotificationDispatcherTests(PostgresFixture postgres)
{
    private const string Secret = "whsec-test";

    [Fact]
    public async Task Delivers_an_hmac_signed_webhook_and_marks_it_sent()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();
        var services = db.CreateWorkerServices(Webhook(target));
        var (monitorId, incidentId) = await OpenIncidentAsync(db, services);

        var dispatcher = services.GetRequiredService<NotificationDispatcher>();
        Assert.Equal(1, await dispatcher.DispatchOnceAsync());

        var request = Assert.Single(target.Received);
        Assert.True(WebhookSignature.Verify(Secret, request.Body, request.Headers[WebhookSignature.HeaderName]));
        Assert.Equal("monitor.state_changed", request.Headers[WebhookSender.EventHeader]);

        var payload = TransitionPayload.FromJson(Encoding.UTF8.GetString(request.Body));
        Assert.Equal(monitorId, payload.Monitor.Id);
        Assert.Equal(("ok", "down", 321, incidentId), (payload.OldState, payload.NewState, payload.P95Ms, payload.IncidentId));
        Assert.True(DateTime.UtcNow - payload.Timestamp < TimeSpan.FromMinutes(1));

        var row = Assert.Single(await services.GetRequiredService<NotificationRepository>().ListForIncidentAsync(incidentId));
        Assert.Equal(row.Id.ToString(), request.Headers[WebhookSender.DeliveryHeader]);
        Assert.Equal(("sent", 1), (row.Status, row.Attempts));
        Assert.NotNull(row.SentAt);
        Assert.Equal(0, await dispatcher.DispatchOnceAsync());
    }

    [Fact]
    public async Task Failed_deliveries_back_off_two_to_the_attempts_seconds_and_fail_after_five_attempts()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();
        target.HookStatus = 500;
        var services = db.CreateWorkerServices(Webhook(target));
        var (_, incidentId) = await OpenIncidentAsync(db, services);
        var repository = services.GetRequiredService<NotificationRepository>();
        var dispatcher = services.GetRequiredService<NotificationDispatcher>();
        var id = Assert.Single(await repository.ListForIncidentAsync(incidentId)).Id;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (attempt > 1) // skip the actual wait; the scheduled delay itself is asserted below
                await db.ExecuteAsync("UPDATE notifications SET next_attempt_at = now() WHERE id = @id", new { id });

            Assert.Equal(1, await dispatcher.DispatchOnceAsync());
            var row = (await repository.GetAsync(id))!;
            Assert.Equal(attempt, row.Attempts);
            Assert.Contains("500", row.LastError);

            if (attempt < 5)
            {
                Assert.Equal("pending", row.Status);
                var delaySeconds = await db.ScalarAsync<double>(
                    "SELECT extract(epoch FROM next_attempt_at - now())::float8 FROM notifications WHERE id = @id", new { id });
                Assert.InRange(delaySeconds, Math.Pow(2, attempt) - 1.5, Math.Pow(2, attempt) + 0.1);
                Assert.Equal(0, await dispatcher.DispatchOnceAsync()); // not due yet
            }
            else
            {
                Assert.Equal("failed", row.Status);
            }
        }

        await db.ExecuteAsync("UPDATE notifications SET next_attempt_at = now() WHERE id = @id", new { id });
        Assert.Equal(0, await dispatcher.DispatchOnceAsync()); // failed rows are never retried
        Assert.Equal(5, target.Received.Count);
    }

    [Fact]
    public async Task Concurrent_dispatchers_deliver_each_notification_exactly_once()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var target = await TestHttpTarget.StartAsync();
        const int notifications = 60;
        var setup = db.CreateWorkerServices(Webhook(target));
        for (var i = 0; i < notifications; i++)
            await OpenIncidentAsync(db, setup);

        var dispatchers = Enumerable.Range(0, 4)
            .Select(_ => db.CreateWorkerServices(Webhook(target, batchSize: 5)).GetRequiredService<NotificationDispatcher>())
            .ToArray();
        await Task.WhenAll(dispatchers.Select(async d =>
        {
            while (await d.DispatchOnceAsync() > 0) { }
        }));

        Assert.Equal(notifications, target.Received.Count);
        Assert.Equal(notifications, target.Received.Select(r => r.Headers[WebhookSender.DeliveryHeader]).Distinct().Count());
        Assert.Equal(notifications, await db.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE status = 'sent' AND attempts = 1"));
    }

    private static Dictionary<string, string?> Webhook(TestHttpTarget target, int batchSize = 50) => new()
    {
        ["Notifications:Webhook:Url"] = target.Url("/hook"),
        ["Notifications:Webhook:Secret"] = Secret,
        ["Notifications:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<(long MonitorId, long IncidentId)> OpenIncidentAsync(TestDatabase db, IServiceProvider services)
    {
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://svc.test/health", name: "svc"));
        var transition = new StateTransition(MonitorState.Ok, MonitorState.Down, TransitionKind.Open, "3 consecutive failures (threshold 3)");
        var composer = services.GetRequiredService<NotificationComposer>();
        var result = await services.GetRequiredService<IncidentRepository>()
            .ApplyTransitionAsync(monitor.Id, null, transition, 321, id => composer.Compose(monitor, transition, 321, id));
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        return (monitor.Id, result.IncidentId!.Value);
    }
}
