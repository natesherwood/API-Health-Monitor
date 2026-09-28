-- An incident row carries the monitor's state machine. The latest row per monitor IS the
-- monitor's current state (no row = 'ok'):
--   opened as 'degraded'/'down' -> may move between them -> 'resolved' (resolved_at set)
--   -> 'ok' on the next healthy evaluation, or reopened (resolved_at cleared) if it breaks again.
CREATE TABLE incidents (
    id             bigserial   PRIMARY KEY,
    monitor_id     bigint      NOT NULL REFERENCES monitors (id) ON DELETE CASCADE,
    state          text        NOT NULL,
    opened_at      timestamptz NOT NULL DEFAULT now(),
    resolved_at    timestamptz NULL,
    trigger_reason text        NOT NULL,
    p95_at_open    int         NULL,
    updated_at     timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_incidents_state CHECK (state IN ('ok', 'degraded', 'down', 'resolved')),
    -- open <=> unresolved; keeps the partial unique index below meaningful.
    CONSTRAINT ck_incidents_resolution CHECK ((state IN ('ok', 'resolved')) = (resolved_at IS NOT NULL))
);

-- Dedup: at most one open incident per monitor. The alert-fire path depends on this index:
-- concurrent evaluators race to INSERT/reopen, exactly one wins, the rest get 23505 and swallow it.
CREATE UNIQUE INDEX ux_incident_open ON incidents (monitor_id) WHERE resolved_at IS NULL;

-- "Current state" lookup: latest incident per monitor.
CREATE INDEX ix_incidents_monitor_latest ON incidents (monitor_id, id DESC);
