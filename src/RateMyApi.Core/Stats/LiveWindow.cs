namespace RateMyApi.Core.Stats;

/// <summary>
/// The sliding window used for a monitor's live p95, and how many latency samples it must hold before
/// that p95 may drive the state machine.
/// <para>
/// percentile_disc(0.95) over n &lt; 20 samples is simply the maximum, so a single outlier (a cold
/// start, one GC pause) would flip a healthy monitor to degraded. The window is therefore widened to
/// span at least <c>minSamples</c> check intervals (never narrower than the configured base window,
/// never wider than <c>maxWindow</c>), and p95 is only trusted once the window is that full.
/// Failures are unaffected: the down path uses the consecutive-failure streak.
/// </para>
/// </summary>
public readonly record struct LiveWindow(TimeSpan Window, int RequiredSamples)
{
    public static LiveWindow For(int intervalSeconds, TimeSpan baseWindow, int minSamples, TimeSpan maxWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minSamples, 1);
        if (baseWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseWindow), baseWindow, "Window must be positive.");

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var upper = maxWindow > baseWindow ? maxWindow : baseWindow;
        var window = TimeSpan.FromTicks(Math.Clamp((interval * minSamples).Ticks, baseWindow.Ticks, upper.Ticks));

        // A window capped by maxWindow cannot hold minSamples checks; require it to be as full as it can be.
        var fits = (int)Math.Min(int.MaxValue, window.Ticks / interval.Ticks);
        return new LiveWindow(window, Math.Max(1, Math.Min(minSamples, fits)));
    }

    /// <summary>The p95 to evaluate: null (no latency verdict) until the window holds enough samples.</summary>
    public int? Trust(int? p95Ms, long latencySamples) => latencySamples >= RequiredSamples ? p95Ms : null;
}
