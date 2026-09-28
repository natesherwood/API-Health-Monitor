-- Monitors are both configuration and the scheduling queue: next_due_at is the only
-- scheduler state in the system, which is what makes workers stateless and re-shardable.
CREATE TABLE monitors (
    id                        bigserial   PRIMARY KEY,
    name                      text        NOT NULL,
    url                       text        NOT NULL,
    method                    text        NOT NULL DEFAULT 'GET',
    expected_status           int         NOT NULL DEFAULT 200,
    interval_seconds          int         NOT NULL,
    timeout_ms                int         NOT NULL,
    enabled                   bool        NOT NULL DEFAULT true,
    degraded_p95_ms           int         NOT NULL,
    down_consecutive_failures int         NOT NULL,
    next_due_at               timestamptz NOT NULL DEFAULT now(),
    created_at                timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_monitors_method          CHECK (method IN ('GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS')),
    CONSTRAINT ck_monitors_expected_status CHECK (expected_status BETWEEN 100 AND 599),
    CONSTRAINT ck_monitors_interval        CHECK (interval_seconds >= 1),
    CONSTRAINT ck_monitors_timeout         CHECK (timeout_ms >= 1),
    CONSTRAINT ck_monitors_degraded        CHECK (degraded_p95_ms >= 1),
    CONSTRAINT ck_monitors_down            CHECK (down_consecutive_failures >= 1)
);

-- The claim query's access path: due, enabled rows in due order.
CREATE INDEX ix_monitors_due ON monitors (next_due_at) WHERE enabled;
