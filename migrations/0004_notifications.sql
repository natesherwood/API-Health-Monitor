-- Transactional outbox: rows are inserted in the same transaction as the incident transition,
-- then delivered by NotificationDispatcher (FOR UPDATE SKIP LOCKED, exponential backoff).
CREATE TABLE notifications (
    id              bigserial   PRIMARY KEY,
    incident_id     bigint      NOT NULL REFERENCES incidents (id) ON DELETE CASCADE,
    channel         text        NOT NULL,
    target          text        NOT NULL,
    payload         jsonb       NOT NULL,
    status          text        NOT NULL DEFAULT 'pending',
    attempts        int         NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    sent_at         timestamptz NULL,

    CONSTRAINT ck_notifications_channel CHECK (channel IN ('webhook', 'email')),
    CONSTRAINT ck_notifications_status  CHECK (status IN ('pending', 'sent', 'failed'))
);

-- Dispatcher claim path.
CREATE INDEX ix_notifications_due ON notifications (next_attempt_at) WHERE status = 'pending';
CREATE INDEX ix_notifications_incident ON notifications (incident_id);
