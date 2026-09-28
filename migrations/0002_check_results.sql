-- Append-only time series, RANGE-partitioned by month on checked_at.
-- Partitions (check_results_YYYY_MM) are created and dropped by PartitionRolloverService;
-- this migration only defines the parent.
--
-- No foreign key to monitors on purpose: the hot insert path stays lookup-free, and rows for
-- deleted monitors age out with retention (DROP of a whole partition) instead of a cascading
-- row-by-row delete across every partition.

-- PostgreSQL 16 does not support identity columns on partitioned tables, so use a sequence.
CREATE SEQUENCE check_results_id_seq AS bigint;

CREATE TABLE check_results (
    id          bigint      NOT NULL DEFAULT nextval('check_results_id_seq'),
    monitor_id  bigint      NOT NULL,
    checked_at  timestamptz NOT NULL,
    status_code int         NULL,
    latency_ms  int         NULL,   -- NULL when no response was received (timeout, DNS, refused)
    success     bool        NOT NULL,
    error       text        NULL,
    PRIMARY KEY (id, checked_at)
) PARTITION BY RANGE (checked_at);

ALTER SEQUENCE check_results_id_seq OWNED BY check_results.id;

-- Declared on the parent, so PostgreSQL materialises it as a local index on every partition
-- (including ones created later). Serves "latest N results for a monitor" and the p95 window.
CREATE INDEX ix_check_results_monitor_time ON check_results (monitor_id, checked_at DESC);
