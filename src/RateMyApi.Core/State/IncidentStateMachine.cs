using System.Globalization;

namespace RateMyApi.Core.State;

/// <summary>What the evaluator observed for one monitor.</summary>
/// <param name="P95Ms">Live sliding-window p95 of response latency; null when there are no samples.</param>
/// <param name="ConsecutiveFailures">Length of the current failure streak (newest checks first).</param>
/// <param name="LastCheckSucceeded">Outcome of the newest check; null when the monitor has never run.</param>
public readonly record struct HealthSignal(int? P95Ms, int ConsecutiveFailures, bool? LastCheckSucceeded)
{
    public static HealthSignal FromRecent(int? p95Ms, IReadOnlyList<bool> recentNewestFirst)
    {
        var streak = 0;
        while (streak < recentNewestFirst.Count && !recentNewestFirst[streak])
            streak++;

        return new HealthSignal(p95Ms, streak, recentNewestFirst.Count == 0 ? null : recentNewestFirst[0]);
    }
}

public readonly record struct Thresholds(int DegradedP95Ms, int DownConsecutiveFailures);

/// <summary>How a transition is persisted against the incident row.</summary>
public enum TransitionKind
{
    /// <summary>ok -> degraded|down: INSERT a new incident (deduplicated by ux_incident_open).</summary>
    Open,

    /// <summary>degraded &lt;-&gt; down: update the open incident in place.</summary>
    Change,

    /// <summary>degraded|down -> resolved: close the incident (resolved_at = now()).</summary>
    Resolve,

    /// <summary>resolved -> degraded|down: breaking again before the next healthy evaluation reopens the SAME incident.</summary>
    Reopen,

    /// <summary>resolved -> ok: the incident is final; the next problem opens a new one.</summary>
    Settle,
}

public sealed record StateTransition(MonitorState From, MonitorState To, TransitionKind Kind, string Reason);

/// <summary>
/// Pure four-state machine. Returns null for steady state, so callers do no writes and send nothing.
/// <code>
///   from \ signal   failing(streak>=N)   slow(p95>threshold)          healthy(last ok, !slow)
///   ok              down  [Open]         degraded [Open]              -
///   degraded        down  [Change]       -                            resolved [Resolve]
///   down            -                    degraded if last ok [Change] resolved [Resolve]
///   resolved        down  [Reopen]       degraded [Reopen]            ok [Settle]
/// </code>
/// Failing wins over slow. <c>resolved</c> is a one-evaluation flap guard: a monitor that breaks again
/// before its next healthy evaluation reopens its incident instead of creating a new one, so a
/// flapping monitor yields one incident row with one notification per transition.
/// </summary>
public static class IncidentStateMachine
{
    public static StateTransition? Evaluate(MonitorState current, HealthSignal signal, Thresholds thresholds)
    {
        if (thresholds.DegradedP95Ms < 1)
            throw new ArgumentOutOfRangeException(nameof(thresholds), "DegradedP95Ms must be >= 1.");
        if (thresholds.DownConsecutiveFailures < 1)
            throw new ArgumentOutOfRangeException(nameof(thresholds), "DownConsecutiveFailures must be >= 1.");

        var failing = signal.ConsecutiveFailures >= thresholds.DownConsecutiveFailures;
        var slow = signal.P95Ms is { } p95 && p95 > thresholds.DegradedP95Ms;
        var lastOk = signal.LastCheckSucceeded == true;
        var healthy = lastOk && !slow;

        MonitorState? next = current switch
        {
            MonitorState.Ok or MonitorState.Resolved or MonitorState.Degraded when failing => MonitorState.Down,
            MonitorState.Ok or MonitorState.Resolved when slow => MonitorState.Degraded,
            MonitorState.Resolved when healthy => MonitorState.Ok,
            MonitorState.Degraded or MonitorState.Down when healthy => MonitorState.Resolved,
            MonitorState.Down when lastOk && slow => MonitorState.Degraded,
            _ => null,
        };

        if (next is not { } to || to == current)
            return null;

        return new StateTransition(current, to, KindOf(current, to), Reason(current, to, signal, thresholds));
    }

    public static TransitionKind KindOf(MonitorState from, MonitorState to) => (from, to) switch
    {
        (MonitorState.Ok, _) => TransitionKind.Open,
        (MonitorState.Resolved, MonitorState.Ok) => TransitionKind.Settle,
        (MonitorState.Resolved, _) => TransitionKind.Reopen,
        (_, MonitorState.Resolved) => TransitionKind.Resolve,
        _ => TransitionKind.Change,
    };

    private static string Reason(MonitorState from, MonitorState to, HealthSignal signal, Thresholds thresholds)
    {
        var p95 = signal.P95Ms is { } v ? v.ToString(CultureInfo.InvariantCulture) + "ms" : "n/a";
        return to switch
        {
            MonitorState.Down => string.Create(CultureInfo.InvariantCulture,
                $"{signal.ConsecutiveFailures} consecutive failures (threshold {thresholds.DownConsecutiveFailures})"),
            MonitorState.Degraded when from == MonitorState.Down => string.Create(CultureInfo.InvariantCulture,
                $"responding again but p95 {p95} > {thresholds.DegradedP95Ms}ms"),
            MonitorState.Degraded => string.Create(CultureInfo.InvariantCulture,
                $"p95 {p95} > {thresholds.DegradedP95Ms}ms"),
            MonitorState.Resolved => string.Create(CultureInfo.InvariantCulture,
                $"recovered: last check succeeded, p95 {p95} <= {thresholds.DegradedP95Ms}ms"),
            _ => "healthy since resolution",
        };
    }
}
