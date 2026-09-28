using System.Globalization;

namespace RateMyApi.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";
    public const string Auto = "auto";

    /// <summary>0-based shard id (env <c>WORKER_ID</c>), or "auto" to lease a free slot from PostgreSQL.</summary>
    public string? WorkerId { get; set; }

    /// <summary>Total shards (env <c>WORKER_COUNT</c>), or "auto" to use the number of live workers.</summary>
    public string? WorkerCount { get; set; }

    /// <summary>Max monitors claimed per tick.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Bound on concurrently executing checks (Parallel.ForEachAsync DOP).</summary>
    public int MaxParallelism { get; set; } = 32;

    /// <summary>Sleep between ticks when the last claim did not fill a batch.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Auto mode: highest slot number a worker will try to lease (when WORKER_COUNT is auto).</summary>
    public int MaxSlots { get; set; } = 256;

    /// <summary>Auto mode: how often live membership is re-read from pg_locks.</summary>
    public int MembershipRefreshMs { get; set; } = 2_000;

    public static bool IsAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    public static int ParseInt(string value, string name) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be an integer or '{Auto}', got '{value}'.");
}

public sealed class EvaluationOptions
{
    public const string SectionName = "Evaluation";

    /// <summary>Base sliding window for the live p95 used by the state machine.</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>
    /// Latency samples required before p95 can move a monitor into or out of degraded. The window is
    /// widened to cover this many check intervals (see <see cref="Core.Stats.LiveWindow"/>).
    /// 20 is the smallest n for which percentile_disc(0.95) is not simply the maximum.
    /// </summary>
    public int MinSamples { get; set; } = 20;

    /// <summary>Upper bound for the widened window.</summary>
    public int MaxWindowMinutes { get; set; } = 60;

    public TimeSpan Window => TimeSpan.FromMinutes(WindowMinutes);
    public TimeSpan MaxWindow => TimeSpan.FromMinutes(MaxWindowMinutes);
}
