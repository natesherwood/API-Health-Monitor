using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.State;
using RateMyApi.Core.Stats;
using RateMyApi.Data.Repositories;
using RateMyApi.Worker.Notifications;

namespace RateMyApi.Worker.Evaluation;

public sealed record EvaluationResult(StateTransition? Transition, TransitionResult? Persisted)
{
    public static readonly EvaluationResult NoChange = new(null, null);
}

public interface IMonitorEvaluator
{
    Task<EvaluationResult> EvaluateAsync(MonitorDefinition monitor, CancellationToken ct = default);
}

/// <summary>
/// Reads the live signal (sliding-window p95 + failure streak) and the current state, runs the pure
/// <see cref="IncidentStateMachine"/>, and persists only when it yields a transition. Steady state is
/// read-only: no incident write, no notification.
/// </summary>
public sealed class MonitorEvaluator(
    CheckResultRepository results,
    IncidentRepository incidents,
    NotificationComposer composer,
    IOptions<EvaluationOptions> options,
    ILogger<MonitorEvaluator> logger) : IMonitorEvaluator
{
    public async Task<EvaluationResult> EvaluateAsync(MonitorDefinition monitor, CancellationToken ct = default)
    {
        var settings = options.Value;
        var window = LiveWindow.For(monitor.IntervalSeconds, settings.Window, settings.MinSamples, settings.MaxWindow);

        var inputs = await results.GetHealthInputsAsync(monitor.Id, window.Window, monitor.DownConsecutiveFailures, ct);
        var head = await incidents.GetLatestAsync(monitor.Id, ct);

        var p95 = window.Trust(inputs.P95Ms, inputs.LatencySamples);
        var current = head is null ? MonitorState.Ok : MonitorStateNames.Parse(head.State);
        var signal = HealthSignal.FromRecent(p95, inputs.RecentSuccesses);
        var thresholds = new Thresholds(monitor.DegradedP95Ms, monitor.DownConsecutiveFailures);

        var transition = IncidentStateMachine.Evaluate(current, signal, thresholds);
        if (transition is null)
            return EvaluationResult.NoChange;

        var persisted = await incidents.ApplyTransitionAsync(
            monitor.Id, head, transition, p95,
            incidentId => composer.Compose(monitor, transition, p95, incidentId),
            ct);

        if (persisted.Outcome == TransitionOutcome.Applied)
        {
            logger.LogInformation("Monitor {MonitorId} ({Name}): {From} -> {To} ({Reason}), incident {IncidentId}",
                monitor.Id, monitor.Name, transition.From.ToName(), transition.To.ToName(), transition.Reason, persisted.IncidentId);
        }
        else
        {
            logger.LogDebug("Monitor {MonitorId}: {From} -> {To} not applied: {Outcome}",
                monitor.Id, transition.From.ToName(), transition.To.ToName(), persisted.Outcome);
        }

        return new EvaluationResult(transition, persisted);
    }
}
