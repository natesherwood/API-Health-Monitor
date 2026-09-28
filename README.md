# RateMyAPI

A horizontally scalable API health and latency monitor. N stateless workers check HTTP endpoints on a schedule and compute a live p95 per monitor. A four-state incident machine drives signed webhook and email notifications, and a public status page shows the result.

**Stack:** .NET 8 Minimal APIs · Dapper + Npgsql (no EF) · PostgreSQL 16 · React 18 via CDN (no build step) · xUnit + Testcontainers · Docker Compose.

```bash
docker compose up --build --scale worker=3
```

| URL | What |
|---|---|
| http://localhost:8080/ | Status page (public) |
| http://localhost:8080/api/status | Status JSON (public) |
| http://localhost:8080/api/monitors | Management API: `-H "X-Api-Key: change-me"` (override with `API_KEY`) |
| http://localhost:8080/demo/webhook-sink | Webhooks the workers delivered, with signature verification |

First boot seeds seven demo monitors against the API's own `/demo/targets/*`, so the stack is self-contained and every state shows up: `demo-ok`, `demo-slow` (degraded), `demo-flaky` (flapping), `demo-wave` (latency crosses the threshold every 20 min: ok → degraded → resolved → ok), `demo-down`, `demo-timeout`, and `self-healthz`. It also backfills 96 h of synthetic history so the uptime bars aren't empty (`Demo__BackfillHours=0` disables that).

---

## Architecture

```
                    browsers                          operators / CI
                       |  GET /  GET /api/status          |  X-Api-Key: /api/monitors, /api/incidents
                       v                                  v
            +-----------------------------------------------------------+
            |  api (ASP.NET Core Minimal APIs)                          |
            |  migrations at startup (advisory-locked)                  |
            |  static status page (web/index.html + app.jsx)            |
            |  demo: /demo/targets/*, /demo/webhook-sink                |
            +-----------------------------+-----------------------------+
                                          | Dapper + Npgsql
   +--------------------------------------v------------------------------------------+
   |  PostgreSQL 16                                                                   |
   |                                                                                  |
   |  monitors ............ config + the schedule itself (next_due_at)                |
   |  check_results ....... PARTITION BY RANGE (checked_at), one partition per month  |
   |     +-- check_results_2026_09   (monitor_id, checked_at DESC) local index        |
   |     +-- check_results_2026_10 ...                                                |
   |  incidents ........... state machine rows; ux_incident_open = 1 open per monitor |
   |  notifications ....... transactional outbox (pending -> sent | failed)          |
   |  monitor_hourly_stats  MATERIALIZED VIEW, REFRESH ... CONCURRENTLY               |
   +------^-----------------------^-----------------------^---------------------------+
          | claim: 1 statement     |                       |
          | FOR UPDATE SKIP LOCKED |                       |
          | + reschedule           |                       |
   +------+-------+        +------+-------+        +------+-------+
   | worker 0/3   |        | worker 1/3   |        | worker 2/3   |   <- docker compose --scale worker=N
   |              |        |              |        |              |      (auto: slot lease via advisory lock,
   | claim loop   |        | claim loop   |        | claim loop   |       shard = rank among live workers)
   | evaluator    |        | evaluator    |        | evaluator    |
   | dispatcher   |        | dispatcher   |        | dispatcher   |
   | rollover     |        | rollover     |        | rollover     |
   | matview      |        | matview      |        | matview      |
   +------+-------+        +------+-------+        +------+-------+
          | Parallel.ForEachAsync (bounded DOP), CancellationTokenSource(timeout_ms) per check
          v
     monitored HTTP endpoints            webhook receivers (HMAC-SHA256)  /  SMTP
```

The life of one check:

```
claim      WITH due AS (SELECT id ... AND id % @workerCount = @workerId ... FOR UPDATE SKIP LOCKED)
           UPDATE monitors SET next_due_at = now() + interval ... RETURNING ...        (one transaction)
execute    HttpClient (IHttpClientFactory, pooled) + own CancellationTokenSource(timeout_ms)
           timeout -> success=false, error='timeout', latency_ms=NULL   (never fails the batch)
record     INSERT check_results
evaluate   live p95 = percentile_disc(0.95) over the sliding window + consecutive-failure streak
           IncidentStateMachine.Evaluate(current, signal)   (pure)
             steady state -> nothing, no writes
             transition   -> ONE transaction: incident INSERT/UPDATE + notifications INSERT
                             racing evaluators: 23505 on ux_incident_open / compare-and-set -> swallowed
deliver    NotificationDispatcher: claim (SKIP LOCKED) -> POST webhook / SMTP
           -> sent | next_attempt_at = now() + 2^attempts s | failed after 5 attempts
```

## Why `FOR UPDATE SKIP LOCKED` instead of a distributed lock

* **The database already has to be right.** A check must be claimed *and* rescheduled, and that write lands in Postgres regardless. Doing both in one statement (`SELECT … FOR UPDATE SKIP LOCKED` feeding `UPDATE … SET next_due_at`) makes the claim atomic, with no second system that can disagree with the data.
* **A distributed lock guards a critical section, not the data.** Redis or ZooKeeper leases can expire while the holder is paused (GC, network partition), leaving two workers that both believe they hold the lock. Making that safe needs fencing tokens checked by the storage layer, and at that point the database is enforcing exclusivity anyway. Row locks have no lease to expire: they end at commit/rollback, or when the session dies.
* **Concurrency instead of serialization.** A global or per-shard lock queues workers behind each other. With `SKIP LOCKED`, N claimers drain the same queue in parallel, each skipping rows another transaction holds instead of blocking on them.
* **Re-sharding needs no protocol.** Ownership is a `WHERE` clause. When workers briefly disagree about `WORKER_COUNT`, the overlap is resolved by the row locks and the gaps are just rows that stay due a little longer. There is no lock hand-off to get wrong ([docs/scaling.md](docs/scaling.md)).
* **One less moving part.** No Redis or etcd to deploy, secure, monitor, or fail over. The places that do need mutual exclusion (migrations, partition rollover, matview refresh, worker slot leases) use PostgreSQL advisory locks, which are also session-scoped and released automatically on crash.
* **Trade-off, chosen deliberately:** claim-then-execute is *at most once per slot*. A worker killed mid-batch skips those checks for one interval rather than running them twice. A duplicate check can double-fire alerts; a skipped slot costs one data point.

Acceptance tests 1 and 2 prove the property. As a mutation check, deleting `FOR UPDATE SKIP LOCKED` from the claim makes the overlapping-claimers test fail, with monitors executed 2–3 times.

## Why partition `check_results` instead of one big table

* **Retention becomes a metadata operation.** Expiring a month is `ALTER TABLE … DETACH PARTITION … CONCURRENTLY` plus `DROP TABLE`: instant, and no row is touched. On a single table the same retention is `DELETE … WHERE checked_at < …` over millions of rows, which means a WAL storm, replica lag, table and index bloat, and autovacuum competing with the insert workload.
* **Queries touch only what they need.** Every read constrains `checked_at` (the live p95 window, the matview's 7-day scope), so partition pruning skips old months entirely. Per-partition indexes stay small and hot, and inserts always land at the right edge of the current month's index.
* **Maintenance is per partition.** Vacuum, analyze, and reindex work happens on the active month. Older partitions become all-visible and effectively frozen.
* **Detaching doesn't stop the world.** `DETACH … CONCURRENTLY` takes only `SHARE UPDATE EXCLUSIVE` on the parent, so inserts and status reads continue during rollover.
* **Costs, accepted knowingly:**
  * The primary key must include the partition key: `(id, checked_at)`.
  * PostgreSQL 16 has no identity columns on partitioned tables, so `id` uses a sequence.
  * There is no FK to `monitors`: rows of deleted monitors age out with retention.
  * Future partitions must exist before data arrives. The rollover service keeps `PartitionsAhead=2`, and there is intentionally no DEFAULT partition, so a missing month fails loudly rather than silently filling a default partition that would later block creating the real one.
* **Monthly granularity** matches retention in months and keeps the partition count around 9, so planning overhead stays negligible.

---

## Incident state machine

`Core/IncidentStateMachine.cs` is pure (no I/O) and exhaustively unit-tested. The monitor's current state is its latest `incidents` row (`ok` if none).

| from \ signal | failing (streak ≥ `down_consecutive_failures`) | slow (p95 > `degraded_p95_ms`) | healthy (last check ok, p95 not slow) |
|---|---|---|---|
| **ok** | → down *(open incident)* | → degraded *(open incident)* | — |
| **degraded** | → down | — | → resolved |
| **down** | — | → degraded *(if the last check succeeded)* | → resolved |
| **resolved** | → down *(reopen the same incident)* | → degraded *(reopen)* | → ok |

Failing beats slow. Notifications fire **only** on transitions, one per enabled channel. A steady state performs no writes at all.

## Notifications

* Transitions write `notifications` rows (`webhook`, `email`) in the same transaction as the incident change (transactional outbox).
* `NotificationDispatcher` runs on every worker and claims due rows with `FOR UPDATE SKIP LOCKED`, pushing `next_attempt_at` out by a lease. Delivery is at-least-once.
* On failure: `attempts += 1`, `next_attempt_at = now() + 2^attempts s` (2, 4, 8, 16 s). After 5 attempts the row becomes `failed`.
* The webhook is a `POST` with this JSON body:

  ```json
  {"event":"monitor.state_changed","monitor":{"id":4,"name":"demo-wave","url":"http://api:8080/demo/targets/wave"},
   "old_state":"ok","new_state":"degraded","p95_ms":614,"reason":"p95 614ms > 300ms","incident_id":5,
   "timestamp":"2026-09-16T23:21:44.02Z"}
  ```

  Headers: `X-RateMyApi-Signature: sha256=<hex HMAC-SHA256(secret, raw body)>`, `X-RateMyApi-Delivery: <notification id>` (dedupe key), `X-RateMyApi-Event`.
* Email goes over SMTP (`System.Net.Mail`). It is enabled when both `Notifications:Email:To` and `SmtpHost` are set.

## API

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/monitors` | key | All monitors with current state, last check, live p95 |
| GET | `/api/monitors/{id}` | key | |
| POST | `/api/monitors` | key | Validated; defaults: GET, 200, 60 s, 5000 ms, p95 1000 ms, 3 failures |
| PUT | `/api/monitors/{id}` | key | Full replace; a shorter interval takes effect immediately |
| DELETE | `/api/monitors/{id}` | key | Cascades incidents/notifications; results age out |
| GET | `/api/monitors/{id}/series?hours=24` | key | Hourly p95 / uptime / count from the matview (1–168) |
| GET | `/api/incidents?open=true` | key | |
| GET | `/api/status` | public | Everything the status page needs; no monitor URLs |
| GET | `/healthz` | public | DB round trip; 503 when unhealthy |

```bash
curl -s -H "X-Api-Key: change-me" -H "Content-Type: application/json" localhost:8080/api/monitors \
  -d '{"name":"github","url":"https://api.github.com/zen","interval_seconds":30,"timeout_ms":3000,"degraded_p95_ms":800,"down_consecutive_failures":3}'
```

JSON is snake_case both ways. `/api/status` responses are cached for `Api:StatusCacheSeconds` (5 s), so a crowd of status-page viewers costs a few queries per 5 seconds.

## Status page

`web/index.html` + `web/app.jsx`: React 18 UMD + Babel standalone, served statically by the API. Dark, monospace, dense. Each monitor row shows:

* a 90-segment hourly uptime bar (green / amber / red, grey for no data)
* a hand-rolled SVG p95 sparkline over 24 h, with the degraded threshold dashed
* a state pill
* live p95
* a relative last-checked time that ticks every second

Open incidents appear in a banner at the top, and the page refreshes every 15 s. Bars and sparklines come from `monitor_hourly_stats`; state and "p95 now" are live.

## Configuration

| Key (env var form) | Default | Where |
|---|---|---|
| `ConnectionStrings__Postgres` | — | api, worker |
| `WORKER_ID` / `WORKER_COUNT` | `auto` / `auto` | worker; integers for explicit sharding |
| `Worker__BatchSize` / `Worker__MaxParallelism` / `Worker__PollIntervalMs` | 100 / 32 / 500 | worker |
| `Evaluation__WindowMinutes` / `Evaluation__MinSamples` / `Evaluation__MaxWindowMinutes` | 5 / 20 / 60 | worker |
| `Retention__Months` / `Retention__PartitionsAhead` | 6 / 2 | worker |
| `Stats__RefreshSeconds` | 60 | worker |
| `Notifications__Webhook__Url` / `__Secret` | — | worker |
| `Notifications__Email__To` / `__SmtpHost` / `__SmtpPort` / `__From` | — / — / 25 / ratemyapi@localhost | worker |
| `Notifications__MaxAttempts` | 5 | worker |
| `Api__Key` | empty (open, logs a warning) | api |
| `Api__StatusCacheSeconds` / `Api__StatusHours` | 5 / 90 | api |
| `Demo__Enabled` / `Demo__Seed` / `Demo__TargetBaseUrl` / `Demo__BackfillHours` / `Demo__WebhookSecret` | false / false / … / 96 / — | api |

## Layout

```
src/RateMyApi.Api       HTTP API, status page host, demo targets + seeding hosted service
src/RateMyApi.Worker    claim loop, evaluator, notification dispatcher, partition rollover, matview refresh
src/RateMyApi.Core      domain, IncidentStateMachine, Percentile (percentile_disc), LiveWindow, sharding, HMAC, backoff
src/RateMyApi.Data      Dapper repositories, MigrationRunner, PartitionManager, demo seeder
tests/RateMyApi.Tests   unit + integration + acceptance tests
web/                    index.html + app.jsx
migrations/             0001..0005 *.sql, embedded into RateMyApi.Data and applied at startup
docs/scaling.md         sharding, re-sharding and the guarantees behind them
```

## Tests

```bash
dotnet test                                    # needs Docker: Testcontainers starts postgres:16-alpine
RATEMYAPI_TEST_PG="Host=127.0.0.1;Port=5432;Username=postgres;Password=...;Database=postgres" dotnet test
                                               # or: any PostgreSQL 16 superuser connection; one temp DB per test
```

| Acceptance check | Test |
|---|---|
| 1. Two workers, `WORKER_COUNT=2`, never execute the same check | `A1_ShardedClaimTests` (plus a 4-overlapping-claimers variant) |
| 2. 2 → 3 mid-run: zero dropped, zero duplicated | `A2_ReshardingTests` |
| 3. Rollover creates next month, drops past retention | `A3_PartitionRolloverTests` (plus clock rollover and 8 concurrent rollovers) |
| 4. Flapping: one incident row, one notification per transition | `A4_FlappingMonitorTests` (up/down and degraded/down flapping) |
| 5. Simultaneous transition on two workers → one incident | `A5_ConcurrentTransitionTests` (8 racers: 1 applied, 7 × 23505) |
| 6. A timeout does not delay or fail the batch | `A6_TimeoutIsolationTests` |
| 7. `REFRESH … CONCURRENTLY` doesn't block `/api/status` | `A7_ConcurrentRefreshTests` (plus a control proving a plain `REFRESH` does block) |

Also covered: state machine table, `percentile_disc` parity between SQL and C#, live-window policy, dispatcher (HMAC, 2^n backoff, give-up, 4 concurrent dispatchers), API CRUD/auth/validation, and auto-mode slot leasing and rebalancing.

## Decisions beyond, or different from, the brief

1. **`resolved` reopens instead of creating a new incident.** The brief lists only `resolved → ok`, which would leave a monitor stuck in `resolved` if it broke again. `resolved` now acts as a one-evaluation flap guard: breaking again reopens the same row. That is what makes a flapping monitor produce a single incident row.
2. **`down → degraded`** when checks succeed again but p95 is still over the threshold. That state is more accurate than staying `down`.
3. **p95 needs enough samples.** Running the stack showed that `percentile_disc(0.95)` over fewer than 20 samples is just the max, so one cold-start request flipped healthy monitors to `degraded`. The live window now widens to cover `MinSamples` (20) check intervals, capped at 60 min, and p95 only counts once the window holds that many samples. Failures still drive `down` immediately. Set `Evaluation__MinSamples=1` for the literal rule.
4. **`latency_ms` is NULL when no response arrived** (timeout, DNS, refused). The p95 then measures response latency, and failures drive `down`.
5. **Additive schema:** `incidents.updated_at`, `notifications.created_at`/`sent_at`, and a `schema_migrations` bookkeeping table (with checksums). No FK from `check_results` to `monitors` (see partitioning).
6. **`WORKER_ID=auto` / `WORKER_COUNT=auto` is the compose default.** Replicas from `--scale` share one environment, so each one leases a slot through an advisory lock and shards by live membership. Explicit integers work exactly as specified.
7. **The management endpoints require `X-Api-Key`.** Only `/api/status` and `/healthz` are public, and the public payload leaves out monitor URLs.
8. **The demo surface** (`/demo/targets/*`, `/demo/webhook-sink`, seeding) exists only when `Demo__Enabled=true`, which compose sets.

## Verification status

Verified on Windows 11 with .NET SDK 8.0.425 and Docker Desktop (Engine 29.8, Compose v5.5):

* `dotnet build`: 0 warnings, with warnings treated as errors.
* **79/79 tests pass** through Testcontainers (`postgres:16-alpine`), including all 7 acceptance checks. The same suite also passes against a native PostgreSQL 16.14 via `RATEMYAPI_TEST_PG`.
* **`docker compose up --build --scale worker=3` works out of the box:**
  * Migrations apply, and 7 monitors are seeded with 96 h of history.
  * The replicas lease slots and settle on shards 0/3, 1/3, 2/3.
  * Signed webhooks reach the sink with valid signatures.
  * The status page renders at http://localhost:8080.
* **Live re-shard:** scaling to `--scale worker=5` re-sharded the replicas to 0/5–4/5, and scaling back to 3 returned them to 0/3–2/3. Checks kept flowing, with zero duplicate `(monitor_id, checked_at)`.
