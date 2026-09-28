namespace RateMyApi.Core.Monitoring;

/// <summary>
/// Result of executing one check; one row of <c>check_results</c>.
/// <see cref="LatencyMs"/> is null when no HTTP response was received (timeout, DNS failure,
/// connection refused): failures feed the <c>down</c> path, latency feeds the <c>degraded</c> path.
/// </summary>
public sealed record CheckOutcome(
    long MonitorId,
    DateTime CheckedAt,
    int? StatusCode,
    int? LatencyMs,
    bool Success,
    string? Error)
{
    public const string TimeoutError = "timeout";
}
