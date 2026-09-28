-- Hourly rollups for the status page and dashboards, refreshed by StatsRefreshService with
-- REFRESH MATERIALIZED VIEW CONCURRENTLY, so readers are never blocked by a refresh.
--
-- Bounded to the last 7 days: covers the 90-hour uptime bar and ?hours= up to 168, and keeps
-- refresh cost proportional to a week of data instead of the whole retention period.
-- date_trunc(..., 'UTC') makes bucket boundaries independent of the refreshing session's TimeZone.
CREATE MATERIALIZED VIEW monitor_hourly_stats AS
SELECT
    monitor_id,
    date_trunc('hour', checked_at, 'UTC')                                  AS bucket,
    percentile_disc(0.95) WITHIN GROUP (ORDER BY latency_ms)               AS p95_ms,
    round(100.0 * count(*) FILTER (WHERE success) / count(*), 3)::float8   AS uptime_pct,
    count(*)                                                               AS check_count,
    count(*) FILTER (WHERE NOT success)                                    AS failure_count
FROM check_results
WHERE checked_at >= date_trunc('hour', now(), 'UTC') - interval '7 days'
GROUP BY monitor_id, date_trunc('hour', checked_at, 'UTC')
WITH DATA;

-- Required by REFRESH ... CONCURRENTLY (it diffs old vs new rows on this key).
CREATE UNIQUE INDEX ux_monitor_hourly_stats ON monitor_hourly_stats (monitor_id, bucket);
