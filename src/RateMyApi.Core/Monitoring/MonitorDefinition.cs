namespace RateMyApi.Core.Monitoring;

/// <summary>A configured HTTP check; one row of <c>monitors</c>. Timestamps are UTC.</summary>
public sealed record MonitorDefinition
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Method { get; init; } = "GET";
    public int ExpectedStatus { get; init; } = 200;
    public int IntervalSeconds { get; init; }
    public int TimeoutMs { get; init; }
    public bool Enabled { get; init; } = true;
    public int DegradedP95Ms { get; init; }
    public int DownConsecutiveFailures { get; init; }
    public DateTime NextDueAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
