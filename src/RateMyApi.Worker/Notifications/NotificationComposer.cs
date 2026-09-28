using Microsoft.Extensions.Options;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Notifications;
using RateMyApi.Core.State;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Worker.Notifications;

/// <summary>Builds one outbox row per enabled channel for a transition.</summary>
public sealed class NotificationComposer(IOptions<NotificationOptions> options, TimeProvider time)
{
    public IReadOnlyList<NotificationRequest> Compose(MonitorDefinition monitor, StateTransition transition, int? p95Ms, long incidentId)
    {
        var settings = options.Value;
        var payload = new TransitionPayload(
            TransitionPayload.StateChangedEvent,
            new MonitorRef(monitor.Id, monitor.Name, monitor.Url),
            transition.From.ToName(),
            transition.To.ToName(),
            p95Ms,
            transition.Reason,
            incidentId,
            time.GetUtcNow().UtcDateTime).ToJson();

        var requests = new List<NotificationRequest>(2);
        if (!string.IsNullOrWhiteSpace(settings.Webhook.Url))
            requests.Add(new NotificationRequest(NotificationChannels.Webhook, settings.Webhook.Url, payload));
        if (settings.Email.Enabled)
            requests.Add(new NotificationRequest(NotificationChannels.Email, settings.Email.To!, payload));
        return requests;
    }
}
