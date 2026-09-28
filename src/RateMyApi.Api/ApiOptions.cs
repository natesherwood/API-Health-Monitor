namespace RateMyApi.Api;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Shared key required in <c>X-Api-Key</c> for the management endpoints. When empty, they are open
    /// (development only; a warning is logged). /api/status and /healthz are always public.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Window for the live p95 reported by the monitor endpoints.</summary>
    public int LiveWindowMinutes { get; set; } = 5;

    /// <summary>Hourly buckets returned per monitor by /api/status (the uptime bar has 90 segments).</summary>
    public int StatusHours { get; set; } = 90;

    /// <summary>Server-side cache for the public /api/status payload; 0 disables it.</summary>
    public int StatusCacheSeconds { get; set; } = 5;

    public TimeSpan LiveWindow => TimeSpan.FromMinutes(LiveWindowMinutes);
}
