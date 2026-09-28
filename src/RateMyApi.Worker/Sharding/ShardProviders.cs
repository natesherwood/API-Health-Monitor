using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using RateMyApi.Core.Sharding;
using RateMyApi.Data;

namespace RateMyApi.Worker.Sharding;

/// <summary>Supplies the shard this worker should claim for on the next tick (null = standby).</summary>
public interface IShardProvider
{
    ValueTask<ShardAssignment?> GetCurrentAsync(CancellationToken ct);
}

/// <summary>Explicit WORKER_ID / WORKER_COUNT.</summary>
public sealed class StaticShardProvider(ShardAssignment shard) : IShardProvider
{
    public ValueTask<ShardAssignment?> GetCurrentAsync(CancellationToken ct) => ValueTask.FromResult<ShardAssignment?>(shard);
}

/// <summary>
/// WORKER_ID=auto: lease the lowest free slot with a session-level advisory lock held on a dedicated
/// connection, so replicas started by <c>docker compose --scale</c> get distinct ids with no
/// per-replica config. The lease dies with the connection (crash, network partition) and the slot
/// becomes available to a standby.
/// <para>
/// With a fixed WORKER_COUNT the slot is the shard id. With WORKER_COUNT=auto the shard is derived
/// from live membership read from <c>pg_locks</c>: count = live slot holders, id = this slot's rank.
/// Membership changes can briefly produce overlapping or unowned buckets while workers observe the
/// change at slightly different times; both are safe (see docs/scaling.md): overlap is resolved by
/// SKIP LOCKED + atomic reschedule, and unowned rows just wait with a past next_due_at.
/// </para>
/// </summary>
public sealed class LeasedShardProvider(
    NpgsqlDataSource dataSource,
    int? fixedCount,
    int maxSlots,
    TimeSpan membershipRefresh,
    TimeProvider time,
    ILogger<LeasedShardProvider> logger) : IShardProvider, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlConnection? _lease;
    private int? _slot;
    private ShardAssignment? _cached;
    private long _refreshedAt;

    public async ValueTask<ShardAssignment?> GetCurrentAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_slot is null && !await TryAcquireAsync(ct))
                return null;

            if (fixedCount is { } count)
                return new ShardAssignment(_slot!.Value, count);

            if (_cached is not null && time.GetElapsedTime(_refreshedAt) < membershipRefresh)
                return _cached;

            _cached = await ReadMembershipAsync(ct);
            _refreshedAt = time.GetTimestamp();
            return _cached;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Lost the lease connection: drop the slot and go standby until it can be re-acquired.
            logger.LogWarning(ex, "Worker slot lease lost; re-acquiring");
            await ReleaseAsync();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        var conn = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var limit = fixedCount ?? maxSlots;
            for (var slot = 0; slot < limit; slot++)
            {
                var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT pg_try_advisory_lock(@ns, @slot)",
                    new { ns = AdvisoryLocks.WorkerSlotNamespace, slot }, cancellationToken: ct));
                if (!acquired)
                    continue;

                _lease = conn;
                _slot = slot;
                _cached = null;
                logger.LogInformation("Leased worker slot {Slot} (WORKER_COUNT={Count})", slot, fixedCount?.ToString() ?? "auto");
                return true;
            }

            logger.LogInformation("All {Limit} worker slots are leased; standing by", limit);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        await conn.DisposeAsync();
        return false;
    }

    private async Task<ShardAssignment?> ReadMembershipAsync(CancellationToken ct)
    {
        // Runs on the lease connection, so it doubles as a liveness check of the lease itself.
        var slots = (await _lease!.QueryAsync<long>(new CommandDefinition("""
            SELECT l.objid::bigint
            FROM pg_locks l
            WHERE l.locktype = 'advisory'
              AND l.granted
              AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND l.classid = @ns::oid
              AND l.objsubid = 2
            ORDER BY l.objid
            """, new { ns = AdvisoryLocks.WorkerSlotNamespace }, cancellationToken: ct))).AsList();

        var rank = slots.IndexOf(_slot!.Value);
        if (rank < 0)
            throw new InvalidOperationException($"Slot {_slot} is no longer held by this worker.");

        var shard = new ShardAssignment(rank, slots.Count);
        if (_cached != shard)
            logger.LogInformation("Membership: slot {Slot} -> shard {Shard} ({Count} live workers)", _slot, shard, slots.Count);
        return shard;
    }

    // The lock belongs to the *physical* connection. Disposing a pooled NpgsqlConnection only returns
    // it to the pool, so unlock explicitly first; if the connection is broken the server has already
    // dropped the lock together with the session.
    private const string UnlockSql = "SELECT pg_advisory_unlock(@ns, @slot)";

    private async Task ReleaseAsync()
    {
        var (lease, slot) = (_lease, _slot);
        (_lease, _slot, _cached) = (null, null, null);
        if (lease is null)
            return;

        try
        {
            if (slot is not null && lease.State == System.Data.ConnectionState.Open)
                await lease.ExecuteAsync(UnlockSql, new { ns = AdvisoryLocks.WorkerSlotNamespace, slot });
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
        }

        try { await lease.DisposeAsync(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        _gate.Dispose();
    }

    public void Dispose()
    {
        var (lease, slot) = (_lease, _slot);
        (_lease, _slot, _cached) = (null, null, null);
        if (lease is not null)
        {
            try
            {
                if (slot is not null && lease.State == System.Data.ConnectionState.Open)
                    lease.Execute(UnlockSql, new { ns = AdvisoryLocks.WorkerSlotNamespace, slot });
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
            }

            try { lease.Dispose(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { }
        }

        _gate.Dispose();
    }
}
