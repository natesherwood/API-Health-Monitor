namespace RateMyApi.Core.Sharding;

/// <summary>
/// A worker's slice of the monitor keyspace: it owns every monitor with
/// <c>id % WorkerCount == WorkerId</c>. Ownership is a pure function evaluated inside the claim
/// query at claim time, so changing it needs no hand-off: rows nobody claimed keep a past
/// <c>next_due_at</c> and are picked up by whichever worker owns their bucket next.
/// </summary>
public readonly record struct ShardAssignment
{
    public ShardAssignment(int workerId, int workerCount)
    {
        if (workerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(workerCount), workerCount, "WORKER_COUNT must be >= 1.");
        if (workerId < 0 || workerId >= workerCount)
            throw new ArgumentOutOfRangeException(nameof(workerId), workerId, $"WORKER_ID must be in [0, {workerCount}).");

        WorkerId = workerId;
        WorkerCount = workerCount;
    }

    public int WorkerId { get; }
    public int WorkerCount { get; }

    public bool Owns(long monitorId) => monitorId % WorkerCount == WorkerId;

    public override string ToString() => $"{WorkerId}/{WorkerCount}";
}
