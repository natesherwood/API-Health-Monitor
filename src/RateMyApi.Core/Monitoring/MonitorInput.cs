namespace RateMyApi.Core.Monitoring;

/// <summary>Create/update payload for a monitor. Unset optional fields take the defaults below.</summary>
public sealed record MonitorInput
{
    public static readonly IReadOnlyList<string> AllowedMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    public string? Name { get; init; }
    public string? Url { get; init; }
    public string? Method { get; init; }
    public int? ExpectedStatus { get; init; }
    public int? IntervalSeconds { get; init; }
    public int? TimeoutMs { get; init; }
    public bool? Enabled { get; init; }
    public int? DegradedP95Ms { get; init; }
    public int? DownConsecutiveFailures { get; init; }

    public MonitorInput WithDefaults() => this with
    {
        Name = Name?.Trim(),
        Url = Url?.Trim(),
        Method = (Method ?? "GET").Trim().ToUpperInvariant(),
        ExpectedStatus = ExpectedStatus ?? 200,
        IntervalSeconds = IntervalSeconds ?? 60,
        TimeoutMs = TimeoutMs ?? 5_000,
        Enabled = Enabled ?? true,
        DegradedP95Ms = DegradedP95Ms ?? 1_000,
        DownConsecutiveFailures = DownConsecutiveFailures ?? 3,
    };

    /// <summary>Validates an input that already had <see cref="WithDefaults"/> applied.</summary>
    /// <returns>Field name (snake_case, as on the wire) to error messages; empty when valid.</returns>
    public IReadOnlyDictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
            errors["name"] = ["Required, at most 200 characters."];

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors["url"] = ["Required, must be an absolute http(s) URL."];

        if (Method is null || !AllowedMethods.Contains(Method))
            errors["method"] = [$"Must be one of {string.Join(", ", AllowedMethods)}."];

        if (ExpectedStatus is not (>= 100 and <= 599))
            errors["expected_status"] = ["Must be between 100 and 599."];

        if (IntervalSeconds is not (>= 1 and <= 86_400))
            errors["interval_seconds"] = ["Must be between 1 and 86400."];

        if (TimeoutMs is not (>= 50 and <= 120_000))
            errors["timeout_ms"] = ["Must be between 50 and 120000."];
        else if (IntervalSeconds is >= 1 && TimeoutMs > IntervalSeconds * 1000)
            // Otherwise a slow check could still be running when the monitor is claimed again.
            errors["timeout_ms"] = ["Must not exceed interval_seconds * 1000."];

        if (DegradedP95Ms is not (>= 1 and <= 600_000))
            errors["degraded_p95_ms"] = ["Must be between 1 and 600000."];

        if (DownConsecutiveFailures is not (>= 1 and <= 100))
            errors["down_consecutive_failures"] = ["Must be between 1 and 100."];

        return errors;
    }
}
