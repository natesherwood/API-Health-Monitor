using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Notifications;
using RateMyApi.Core.State;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;
using RateMyApi.Worker.Evaluation;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 4: a flapping monitor produces exactly one incident row and one notification per transition, not per check.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A4_FlappingMonitorTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> BothChannels = new()
    {
        ["Notifications:Webhook:Url"] = "http://127.0.0.1:9/hook",
        ["Notifications:Email:To"] = "oncall@example.test",
        ["Notifications:Email:SmtpHost"] = "127.0.0.1",
    };

    [Fact]
    public async Task Flapping_up_and_down_yields_one_incident_row_and_one_notification_per_transition()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var services = db.CreateWorkerServices(BothChannels);
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://flappy.test/", downConsecutiveFailures: 3, degradedP95Ms: 500));
        var feed = new CheckFeed(services, monitor);

        // S = success (50ms), F = failure (no response). Threshold: 3 consecutive failures.
        const string sequence =
            "SSS" +   // ok steady
            "FFF" +   // ok -> down            (incident opened)
            "FFFF" +  // down steady           (no writes)
            "S" +     // down -> resolved
            "FF" +    // resolved, streak 2    (no writes)
            "F" +     // resolved -> down      (same incident reopened)
            "S" +     // down -> resolved
            "FFF" +   // resolved -> down      (reopened again)
            "S" +     // down -> resolved
            "S" +     // resolved -> ok        (settled)
            "SSSS";   // ok steady
        foreach (var c in sequence)
            await feed.RecordAsync(success: c == 'S', latencyMs: c == 'S' ? 50 : null);

        var expected = new (MonitorState From, MonitorState To)[]
        {
            (MonitorState.Ok, MonitorState.Down),
            (MonitorState.Down, MonitorState.Resolved),
            (MonitorState.Resolved, MonitorState.Down),
            (MonitorState.Down, MonitorState.Resolved),
            (MonitorState.Resolved, MonitorState.Down),
            (MonitorState.Down, MonitorState.Resolved),
            (MonitorState.Resolved, MonitorState.Ok),
        };
        Assert.Equal(expected, feed.Applied.Select(t => (t.From, t.To)));

        var incidents = await services.GetRequiredService<IncidentRepository>().ListForMonitorAsync(monitor.Id);
        var incident = Assert.Single(incidents);
        Assert.Equal("ok", incident.State);
        Assert.NotNull(incident.ResolvedAt);

        await AssertOneNotificationPerTransitionAsync(db, incident.Id, expected);
        Assert.True(sequence.Length > expected.Length * 3, "the scenario must have many more checks than transitions");
    }

    [Fact]
    public async Task Flapping_between_degraded_and_down_keeps_one_open_incident_and_notifies_each_change()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var services = db.CreateWorkerServices(BothChannels);
        var monitor = await db.GetMonitorAsync(await db.InsertMonitorAsync("http://flappy.test/", downConsecutiveFailures: 3, degradedP95Ms: 500));
        var feed = new CheckFeed(services, monitor);

        for (var i = 0; i < 20; i++) await feed.RecordAsync(true, 50);   // ok steady
        for (var i = 0; i < 2; i++) await feed.RecordAsync(true, 900);   // 2 slow of 22 -> p95 900 -> degraded
        for (var i = 0; i < 3; i++) await feed.RecordAsync(false, null); // -> down
        await feed.RecordAsync(true, 900);                                // responding but slow -> degraded
        for (var i = 0; i < 3; i++) await feed.RecordAsync(false, null); // -> down
        for (var i = 0; i < 60; i++) await feed.RecordAsync(true, 40);   // -> degraded, then p95 recovers -> resolved -> ok

        Assert.Equal(
            new[]
            {
                (MonitorState.Ok, MonitorState.Degraded),
                (MonitorState.Degraded, MonitorState.Down),
                (MonitorState.Down, MonitorState.Degraded),
                (MonitorState.Degraded, MonitorState.Down),
                (MonitorState.Down, MonitorState.Degraded),
                (MonitorState.Degraded, MonitorState.Resolved),
                (MonitorState.Resolved, MonitorState.Ok),
            },
            feed.Applied.Select(t => (t.From, t.To)));

        var incident = Assert.Single(await services.GetRequiredService<IncidentRepository>().ListForMonitorAsync(monitor.Id));
        await AssertOneNotificationPerTransitionAsync(db, incident.Id, feed.Applied.Select(t => (t.From, t.To)).ToArray());
        Assert.Equal(feed.Evaluations - feed.Applied.Count, feed.NoChange);
    }

    private static async Task AssertOneNotificationPerTransitionAsync(TestDatabase db, long incidentId, (MonitorState From, MonitorState To)[] transitions)
    {
        var rows = await db.QueryAsync<(string Channel, string Payload)>(
            "SELECT channel, payload::text FROM notifications WHERE incident_id = @incidentId ORDER BY id", new { incidentId });
        Assert.Equal(await db.ScalarAsync<long>("SELECT count(*) FROM notifications"), rows.Count);

        foreach (var channel in new[] { NotificationChannels.Webhook, NotificationChannels.Email })
        {
            var sequence = rows
                .Where(r => r.Channel == channel)
                .Select(r => TransitionPayload.FromJson(r.Payload))
                .Select(p => (MonitorStateNames.Parse(p.OldState), MonitorStateNames.Parse(p.NewState)))
                .ToArray();
            Assert.Equal(transitions, sequence);
        }
    }

    /// <summary>Records synthetic checks (as the batch runner would) and evaluates after each one.</summary>
    private sealed class CheckFeed(IServiceProvider services, MonitorDefinition monitor)
    {
        private readonly CheckResultRepository _results = services.GetRequiredService<CheckResultRepository>();
        private readonly IMonitorEvaluator _evaluator = services.GetRequiredService<IMonitorEvaluator>();
        private DateTime _at = DateTime.UtcNow.AddMinutes(-4);

        public List<StateTransition> Applied { get; } = [];
        public int Evaluations { get; private set; }
        public int NoChange { get; private set; }

        public async Task RecordAsync(bool success, int? latencyMs)
        {
            _at = _at.AddMilliseconds(100);
            await _results.InsertAsync(new CheckOutcome(monitor.Id, _at, success ? 200 : null, latencyMs, success, success ? null : "timeout"));

            var result = await _evaluator.EvaluateAsync(monitor);
            Evaluations++;
            if (result.Transition is null)
            {
                NoChange++;
                return;
            }

            Assert.Equal(TransitionOutcome.Applied, result.Persisted!.Outcome);
            Applied.Add(result.Transition);
        }
    }
}
