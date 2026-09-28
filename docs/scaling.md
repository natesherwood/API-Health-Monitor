# Scaling and re-sharding workers

RateMyAPI scales checks horizontally by running more `worker` processes. Changing the number of workers never requires a coordinator, a hand-off, or downtime. This document explains why, and what the exact guarantees are.

## The two ideas everything rests on

**1. Scheduling state lives in the rows, not in the workers.**
The only scheduler state is `monitors.next_due_at`. A worker holds nothing that another worker would need to take over: no in-memory queue, no lease on a range of monitors, no timer wheel. A worker that disappears leaves behind exactly the same rows it found.

**2. Ownership is a pure function evaluated at claim time.**
A worker configured with `WORKER_ID` / `WORKER_COUNT` owns the monitors where `id % WORKER_COUNT = WORKER_ID`. That predicate is evaluated *inside* the claim statement:

```sql
WITH due AS (
  SELECT id FROM monitors
  WHERE enabled
    AND next_due_at <= now()
    AND (id % @workerCount) = @workerId
  ORDER BY next_due_at
  LIMIT @batchSize
  FOR UPDATE SKIP LOCKED
)
UPDATE monitors m
SET next_due_at = now() + make_interval(secs => m.interval_seconds)
FROM due WHERE m.id = due.id
RETURNING m.id, m.name, ...;
```

Selecting the row, locking it, and pushing `next_due_at` into the future happen in one statement and one transaction. When the transaction commits, the row is no longer due, so no other claimer can pick it up for that slot, whatever it believes it owns.

## Why changing WORKER_COUNT is zero-downtime

During a change (for example 2 → 3), workers briefly disagree about the layout: some still use `% 2`, others already use `% 3`. That disagreement can only take two forms, and both are safe.

| Situation | Example | What happens | Outcome |
|---|---|---|---|
| **Overlap**: two workers both think they own a row | old `1/2` and new `1/3` both match id 7 | Both run the claim. Row locks make one of them skip the row (`SKIP LOCKED`), and the winner's `UPDATE` moves `next_due_at` forward before commit. | Exactly one execution. No duplicate. |
| **Gap**: nobody owns a row for a moment | id 4 matched the stopped `0/2`, and no `% 3` worker has started yet | Nobody claims it, so `next_due_at` simply stays in the past. | The first worker that owns it claims it. Late, but not lost. |

The recommended rollout makes gaps disappear entirely: **start the new generation, then stop the old one**. While both run, every row is owned by at least one worker, and overlap is exactly the case the claim statement already handles.

Stopping a worker is graceful. It stops *claiming* immediately, but a batch it has already claimed runs to completion (each check is bounded by its own `timeout_ms`), and the results are persisted. Scaling down therefore never abandons claimed work.

Acceptance tests that prove this:

* `A1_ShardedClaimTests`: two workers with `WORKER_COUNT=2` execute each of 400 due monitors exactly once. A harsher variant has four workers all claiming as `0/1`, so exclusivity rests on `SKIP LOCKED` alone. As a mutation check, removing `FOR UPDATE SKIP LOCKED` makes this test fail with monitors executed 2–3 times.
* `A2_ReshardingTests`: 450 monitors become due continuously over 6 seconds. Two `%2` workers run, three `%3` workers join mid-run, and the `%2` workers then drain and stop. Every monitor ends with exactly one result, and both generations did real work.

## Semantics and trade-offs

* **At most once per slot.** Claiming reschedules *before* the check runs. If a worker is killed (SIGKILL, OOM, power loss) after claiming but before recording results, that slot's check is skipped and the next slot runs normally. We chose this over at-least-once because a duplicated check can double-fire alerts, while a skipped slot costs one data point.
* **Catch-up runs once.** A monitor that was unowned for several intervals runs once when claimed, then continues on its normal cadence. It never replays the missed slots.
* **Timeouts are bounded by validation.** `timeout_ms <= interval_seconds * 1000`, so a slow check is never still running when its monitor becomes due again.

## Auto mode: `WORKER_ID=auto`

Replicas started by `docker compose up --scale worker=N` share one environment, so they cannot each be handed a distinct `WORKER_ID`. With `WORKER_ID=auto`, a worker leases an id from PostgreSQL itself:

1. On startup it takes the lowest free slot with `pg_try_advisory_lock(ns, slot)` on a dedicated connection. The lock lives as long as that database session, so a crashed worker's slot frees itself.
2. With **`WORKER_COUNT=auto`** (the compose default), it reads the live slot holders from `pg_locks` every `Worker:MembershipRefreshMs` (default 2s). The shard becomes `count = number of holders` and `id = rank of my slot`, so the fleet always covers `0..count-1` densely, even after a replica in the middle dies.
3. With a **fixed `WORKER_COUNT`**, the slot *is* the shard id. Extra replicas beyond `WORKER_COUNT` find no free slot and stand by until a slot is released.

Membership changes are observed by different workers up to one refresh period apart. That is exactly the overlap/gap window described above, so auto mode inherits the same guarantees without any additional coordination. `LeasedShardProviderTests` covers distinct shards for 3 replicas, rebalancing to 2 when one leaves, and standby promotion.

## Everything else is safe with N replicas too

| Component | Runs on | Concurrency control |
|---|---|---|
| Check claim loop | every worker | `FOR UPDATE SKIP LOCKED` + atomic reschedule |
| Incident transitions | every worker | partial unique index `ux_incident_open` (catch `23505`) and compare-and-set updates |
| Notification dispatch | every worker | same claim pattern on `notifications` with a send lease |
| Partition rollover | every worker, on start + daily | idempotent DDL under `pg_advisory_lock` |
| Matview refresh | every worker, periodically | `pg_try_advisory_xact_lock`; a busy lock means another replica is refreshing |
| Migrations | API + every worker, at start | `pg_advisory_lock` around the whole run |

## Sizing

* **Throughput per worker** is roughly `Worker:MaxParallelism / average check latency`. With DOP 32 and 200 ms checks, that is about 160 checks/s, or about 9,600 monitors on a 60 s interval.
* **Connections**: a worker uses up to about `MaxParallelism` pooled connections (insert + evaluate per check), plus one for its slot lease. Keep `workers × pool size + API pool` under PostgreSQL's `max_connections`, or put PgBouncer in front. Session advisory locks need session pooling for the lease connection.
* **Claim cost** is `O(batch)` via the partial index on `next_due_at WHERE enabled`. The modulo filter is applied after the index scan, so with very many workers and a large backlog, each claimer skips other shards' rows. If that ever shows up in profiles, add a stored `shard_key` column with an index on `(shard_key, next_due_at)`.

## Operating it

```bash
# compose: auto mode, any replica count
docker compose up --build --scale worker=3
docker compose up --scale worker=5 --no-recreate   # scale out; replicas re-shard within ~2s

# explicit mode, rolling 2 -> 3 (start new generation first, then stop the old one)
WORKER_ID=0 WORKER_COUNT=3 dotnet RateMyApi.Worker.dll &
WORKER_ID=1 WORKER_COUNT=3 dotnet RateMyApi.Worker.dll &
WORKER_ID=2 WORKER_COUNT=3 dotnet RateMyApi.Worker.dll &
# ...then stop the WORKER_COUNT=2 processes (SIGTERM; they drain their current batch)
```
