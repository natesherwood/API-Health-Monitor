using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RateMyApi.Core.State;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Api.Status;

public sealed record StatusBucket(DateTime T, int? P95, double Up, long N, long F);

public sealed record StatusMonitor(
    long Id,
    string Name,
    string State,
    bool Enabled,
    DateTime? StateChangedAt,
    DateTime? LastCheckedAt,
    bool? LastSuccess,
    int? LastLatencyMs,
    int? LiveP95Ms,
    int DegradedP95Ms,
    int IntervalSeconds,
    double? UptimePct,
    IReadOnlyList<StatusBucket> Buckets);

public sealed record StatusIncident(
    long Id,
    long MonitorId,
    string MonitorName,
    string State,
    DateTime OpenedAt,
    DateTime UpdatedAt,
    string TriggerReason,
    int? P95AtOpen);

public sealed record StatusResponse(
    DateTime GeneratedAt,
    string Overall,
    int BucketHours,
    IReadOnlyList<StatusMonitor> Monitors,
    IReadOnlyList<StatusIncident> Incidents);

/// <summary>
/// Builds the public status payload: live state/last check from the base tables, uptime bar and
/// sparkline from the monitor_hourly_stats materialized view. Monitor URLs are deliberately omitted.
/// </summary>
public sealed class StatusService(
    MonitorRepository monitors,
    IncidentRepository incidents,
    StatsRepository stats,
    IMemoryCache cache,
    IOptions<ApiOptions> options,
    TimeProvider time)
{
    private const string CacheKey = "status:v1";

    public async Task<StatusResponse> GetAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (settings.StatusCacheSeconds <= 0)
            return await BuildAsync(settings, ct);

        return (await cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(settings.StatusCacheSeconds);
            return BuildAsync(settings, ct);
        }))!;
    }

    private async Task<StatusResponse> BuildAsync(ApiOptions settings, CancellationToken ct)
    {
        var hours = Math.Clamp(settings.StatusHours, 1, StatsRepository.MaxHours);
        var monitorRows = await monitors.ListWithStatusAsync(settings.LiveWindow, null, ct);
        var buckets = (await stats.GetAllSeriesAsync(hours, ct)).ToLookup(b => b.MonitorId);
        var open = await incidents.ListAsync(openOnly: true, limit: 100, ct);

        var statusMonitors = monitorRows.Select(m =>
        {
            var series = buckets[m.Id]
                .Select(b => new StatusBucket(b.Bucket, b.P95Ms, b.UptimePct, b.CheckCount, b.FailureCount))
                .ToArray();
            var total = series.Sum(b => b.N);
            double? uptime = total == 0 ? null : Math.Round(100.0 * (total - series.Sum(b => b.F)) / total, 3);

            return new StatusMonitor(m.Id, m.Name, m.State, m.Enabled, m.StateChangedAt, m.LastCheckedAt,
                m.LastSuccess, m.LastLatencyMs, m.LiveP95Ms, m.DegradedP95Ms, m.IntervalSeconds, uptime, series);
        }).ToArray();

        var enabled = statusMonitors.Where(m => m.Enabled).ToArray();
        var overall = enabled.Any(m => m.State == MonitorStateNames.Down) ? MonitorStateNames.Down
            : enabled.Any(m => m.State == MonitorStateNames.Degraded) ? MonitorStateNames.Degraded
            : MonitorStateNames.Ok;

        var statusIncidents = open
            .Select(i => new StatusIncident(i.Id, i.MonitorId, i.MonitorName, i.State, i.OpenedAt, i.UpdatedAt, i.TriggerReason, i.P95AtOpen))
            .ToArray();

        return new StatusResponse(time.GetUtcNow().UtcDateTime, overall, hours, statusMonitors, statusIncidents);
    }
}
