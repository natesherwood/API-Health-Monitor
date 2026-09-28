using RateMyApi.Core.State;

namespace RateMyApi.Tests.Unit;

public sealed class IncidentStateMachineTests
{
    private static readonly Thresholds Limits = new(DegradedP95Ms: 500, DownConsecutiveFailures: 3);

    private static readonly HealthSignal Healthy = new(P95Ms: 120, ConsecutiveFailures: 0, LastCheckSucceeded: true);
    private static readonly HealthSignal Slow = new(P95Ms: 900, ConsecutiveFailures: 0, LastCheckSucceeded: true);
    private static readonly HealthSignal Failing = new(P95Ms: null, ConsecutiveFailures: 3, LastCheckSucceeded: false);
    private static readonly HealthSignal FailingSlowly = new(P95Ms: 900, ConsecutiveFailures: 4, LastCheckSucceeded: false);
    private static readonly HealthSignal NotYetFailing = new(P95Ms: 120, ConsecutiveFailures: 2, LastCheckSucceeded: false);
    private static readonly HealthSignal NoData = new(P95Ms: null, ConsecutiveFailures: 0, LastCheckSucceeded: null);

    public static IEnumerable<object?[]> Table()
    {
        object?[] Row(MonitorState from, HealthSignal signal, MonitorState? to, TransitionKind? kind) => [from, signal, to, kind];

        // ok
        yield return Row(MonitorState.Ok, Healthy, null, null);
        yield return Row(MonitorState.Ok, NoData, null, null);
        yield return Row(MonitorState.Ok, NotYetFailing, null, null);
        yield return Row(MonitorState.Ok, Slow, MonitorState.Degraded, TransitionKind.Open);
        yield return Row(MonitorState.Ok, Failing, MonitorState.Down, TransitionKind.Open);
        yield return Row(MonitorState.Ok, FailingSlowly, MonitorState.Down, TransitionKind.Open); // failing wins
        // degraded
        yield return Row(MonitorState.Degraded, Slow, null, null);
        yield return Row(MonitorState.Degraded, NotYetFailing, null, null);
        yield return Row(MonitorState.Degraded, NoData, null, null);
        yield return Row(MonitorState.Degraded, Failing, MonitorState.Down, TransitionKind.Change);
        yield return Row(MonitorState.Degraded, Healthy, MonitorState.Resolved, TransitionKind.Resolve);
        // down
        yield return Row(MonitorState.Down, Failing, null, null);
        yield return Row(MonitorState.Down, FailingSlowly, null, null);
        yield return Row(MonitorState.Down, NotYetFailing, null, null);
        yield return Row(MonitorState.Down, Healthy, MonitorState.Resolved, TransitionKind.Resolve);
        yield return Row(MonitorState.Down, new HealthSignal(null, 0, true), MonitorState.Resolved, TransitionKind.Resolve);
        yield return Row(MonitorState.Down, Slow, MonitorState.Degraded, TransitionKind.Change);
        // resolved
        yield return Row(MonitorState.Resolved, Healthy, MonitorState.Ok, TransitionKind.Settle);
        yield return Row(MonitorState.Resolved, NotYetFailing, null, null);
        yield return Row(MonitorState.Resolved, NoData, null, null);
        yield return Row(MonitorState.Resolved, Failing, MonitorState.Down, TransitionKind.Reopen);
        yield return Row(MonitorState.Resolved, Slow, MonitorState.Degraded, TransitionKind.Reopen);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Follows_the_transition_table(MonitorState from, HealthSignal signal, MonitorState? to, TransitionKind? kind)
    {
        var transition = IncidentStateMachine.Evaluate(from, signal, Limits);

        if (to is null)
        {
            Assert.Null(transition);
            return;
        }

        Assert.NotNull(transition);
        Assert.Equal(from, transition.From);
        Assert.Equal(to, transition.To);
        Assert.Equal(kind, transition.Kind);
        Assert.False(string.IsNullOrWhiteSpace(transition.Reason));
    }

    [Fact]
    public void Same_state_twice_is_a_no_op()
    {
        // Feeding the signal that caused a transition again, from the resulting state, changes nothing.
        foreach (var (from, signal) in new[] { (MonitorState.Ok, Slow), (MonitorState.Ok, Failing), (MonitorState.Degraded, Failing), (MonitorState.Down, Healthy), (MonitorState.Resolved, Healthy) })
        {
            var first = IncidentStateMachine.Evaluate(from, signal, Limits)!;
            var second = IncidentStateMachine.Evaluate(first.To, signal, Limits);
            if (first.To == MonitorState.Resolved)
                Assert.Equal(MonitorState.Ok, second?.To); // resolved -> ok is its own, single, next step
            else
                Assert.Null(second);
        }

        Assert.Null(IncidentStateMachine.Evaluate(MonitorState.Ok, Healthy, Limits));
    }

    [Fact]
    public void Thresholds_are_p95_strictly_above_and_streak_at_least()
    {
        Assert.Null(IncidentStateMachine.Evaluate(MonitorState.Ok, new HealthSignal(500, 0, true), Limits));
        Assert.Equal(MonitorState.Degraded, IncidentStateMachine.Evaluate(MonitorState.Ok, new HealthSignal(501, 0, true), Limits)!.To);
        Assert.Null(IncidentStateMachine.Evaluate(MonitorState.Ok, new HealthSignal(null, 2, false), Limits));
        Assert.Equal(MonitorState.Down, IncidentStateMachine.Evaluate(MonitorState.Ok, new HealthSignal(null, 3, false), Limits)!.To);
    }

    [Fact]
    public void Resolution_requires_the_last_check_to_succeed_and_p95_back_under_threshold()
    {
        Assert.Null(IncidentStateMachine.Evaluate(MonitorState.Degraded, new HealthSignal(501, 0, true), Limits));
        Assert.Null(IncidentStateMachine.Evaluate(MonitorState.Degraded, new HealthSignal(100, 1, false), Limits));
        Assert.Equal(MonitorState.Resolved, IncidentStateMachine.Evaluate(MonitorState.Degraded, new HealthSignal(500, 0, true), Limits)!.To);
    }

    [Fact]
    public void A_flapping_signal_opens_once_and_then_reopens_the_same_incident()
    {
        var state = MonitorState.Ok;
        var kinds = new List<TransitionKind>();
        foreach (var signal in new[] { Failing, Failing, Healthy, NotYetFailing, Failing, Healthy, Failing, Healthy, Healthy, Healthy })
        {
            if (IncidentStateMachine.Evaluate(state, signal, Limits) is { } t)
            {
                kinds.Add(t.Kind);
                state = t.To;
            }
        }

        Assert.Equal(
            [TransitionKind.Open, TransitionKind.Resolve, TransitionKind.Reopen, TransitionKind.Resolve, TransitionKind.Reopen, TransitionKind.Resolve, TransitionKind.Settle],
            kinds);
        Assert.Equal(1, kinds.Count(k => k == TransitionKind.Open));
        Assert.Equal(MonitorState.Ok, state);
    }

    [Fact]
    public void Health_signal_counts_only_the_leading_failure_streak()
    {
        Assert.Equal(new HealthSignal(7, 2, false), HealthSignal.FromRecent(7, [false, false, true, false]));
        Assert.Equal(new HealthSignal(null, 0, true), HealthSignal.FromRecent(null, [true, false, false]));
        Assert.Equal(new HealthSignal(null, 3, false), HealthSignal.FromRecent(null, [false, false, false]));
        Assert.Equal(new HealthSignal(null, 0, null), HealthSignal.FromRecent(null, []));
    }

    [Fact]
    public void Reasons_name_the_trigger()
    {
        Assert.Equal("3 consecutive failures (threshold 3)", IncidentStateMachine.Evaluate(MonitorState.Ok, Failing, Limits)!.Reason);
        Assert.Equal("p95 900ms > 500ms", IncidentStateMachine.Evaluate(MonitorState.Ok, Slow, Limits)!.Reason);
        Assert.Equal("responding again but p95 900ms > 500ms", IncidentStateMachine.Evaluate(MonitorState.Down, Slow, Limits)!.Reason);
        Assert.Equal("recovered: last check succeeded, p95 120ms <= 500ms", IncidentStateMachine.Evaluate(MonitorState.Down, Healthy, Limits)!.Reason);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(500, 0)]
    public void Invalid_thresholds_are_rejected(int degradedP95Ms, int downConsecutiveFailures) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncidentStateMachine.Evaluate(MonitorState.Ok, Healthy, new Thresholds(degradedP95Ms, downConsecutiveFailures)));

    [Fact]
    public void State_names_round_trip()
    {
        foreach (var state in Enum.GetValues<MonitorState>())
            Assert.Equal(state, MonitorStateNames.Parse(state.ToName()));
        Assert.Throws<FormatException>(() => MonitorStateNames.Parse("unknown"));
    }
}
