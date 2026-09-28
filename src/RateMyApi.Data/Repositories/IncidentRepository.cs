using Dapper;
using Npgsql;
using RateMyApi.Core.State;

namespace RateMyApi.Data.Repositories;

/// <summary>The latest incident row for a monitor, which is the monitor's current state.</summary>
public sealed record IncidentHead(long Id, string State);

public sealed record IncidentRow
{
    public long Id { get; init; }
    public long MonitorId { get; init; }
    public string MonitorName { get; init; } = "";
    public string State { get; init; } = "";
    public DateTime OpenedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string TriggerReason { get; init; } = "";
    public int? P95AtOpen { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record NotificationRequest(string Channel, string Target, string PayloadJson);

public enum TransitionOutcome
{
    /// <summary>This caller persisted the transition and enqueued its notifications.</summary>
    Applied,

    /// <summary>Another evaluator already opened/reopened an incident (23505 on ux_incident_open).</summary>
    Deduplicated,

    /// <summary>The incident moved on since it was read (compare-and-set matched no row).</summary>
    Stale,

    /// <summary>The monitor was deleted mid-evaluation.</summary>
    MonitorGone,
}

public sealed record TransitionResult(TransitionOutcome Outcome, long? IncidentId);

public sealed class IncidentRepository
{
    public const string OpenIncidentIndex = "ux_incident_open";

    private const string Columns =
        "i.id, i.monitor_id, m.name AS monitor_name, i.state, i.opened_at, i.resolved_at, i.trigger_reason, i.p95_at_open, i.updated_at";

    private readonly NpgsqlDataSource _dataSource;

    public IncidentRepository(NpgsqlDataSource dataSource)
    {
        Db.ConfigureDapper();
        _dataSource = dataSource;
    }

    public async Task<IncidentHead?> GetLatestAsync(long monitorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<IncidentHead>(new CommandDefinition("""
            SELECT id, state FROM incidents WHERE monitor_id = @monitorId ORDER BY id DESC LIMIT 1
            """, new { monitorId }, cancellationToken: ct));
    }

    /// <summary>
    /// Persists a state transition and its notifications atomically. Concurrency is resolved by the
    /// database, never by in-process locks: opening/reopening relies on the partial unique index
    /// (a losing racer gets 23505, which is swallowed), every other change is a compare-and-set on
    /// the state that was read. Only the winner's transaction inserts notifications, so N workers
    /// evaluating the same transition produce exactly one incident and one set of notifications.
    /// </summary>
    public async Task<TransitionResult> ApplyTransitionAsync(
        long monitorId,
        IncidentHead? head,
        StateTransition transition,
        int? p95Ms,
        Func<long, IReadOnlyList<NotificationRequest>> notificationsFor,
        CancellationToken ct = default)
    {
        if (transition.Kind != TransitionKind.Open && head is null)
            throw new ArgumentException($"A {transition.Kind} transition requires the current incident.", nameof(head));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var args = new
            {
                monitorId,
                incidentId = head?.Id ?? 0,
                from = transition.From.ToName(),
                to = transition.To.ToName(),
                reason = transition.Reason,
                p95Ms,
            };

            long incidentId;
            if (transition.Kind == TransitionKind.Open)
            {
                incidentId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO incidents (monitor_id, state, opened_at, trigger_reason, p95_at_open, updated_at)
                    VALUES (@monitorId, @to, now(), @reason, @p95Ms, now())
                    RETURNING id
                    """, args, tx, cancellationToken: ct));
            }
            else
            {
                var sql = transition.Kind switch
                {
                    TransitionKind.Change => """
                        UPDATE incidents SET state = @to, updated_at = now()
                        WHERE id = @incidentId AND state = @from AND resolved_at IS NULL
                        """,
                    TransitionKind.Resolve => """
                        UPDATE incidents SET state = 'resolved', resolved_at = now(), updated_at = now()
                        WHERE id = @incidentId AND state = @from AND resolved_at IS NULL
                        """,
                    // Reopen and settle only apply to the monitor's latest incident.
                    TransitionKind.Reopen => """
                        UPDATE incidents SET state = @to, resolved_at = NULL, updated_at = now()
                        WHERE id = @incidentId AND state = 'resolved'
                          AND NOT EXISTS (SELECT 1 FROM incidents n WHERE n.monitor_id = @monitorId AND n.id > @incidentId)
                        """,
                    TransitionKind.Settle => """
                        UPDATE incidents SET state = 'ok', updated_at = now()
                        WHERE id = @incidentId AND state = 'resolved'
                          AND NOT EXISTS (SELECT 1 FROM incidents n WHERE n.monitor_id = @monitorId AND n.id > @incidentId)
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(transition), transition.Kind, null),
                };

                if (await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct)) == 0)
                    return new TransitionResult(TransitionOutcome.Stale, head!.Id);

                incidentId = head!.Id;
            }

            var notifications = notificationsFor(incidentId);
            if (notifications.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO notifications (incident_id, channel, target, payload, status, attempts, next_attempt_at)
                    VALUES (@incidentId, @Channel, @Target, @PayloadJson::jsonb, 'pending', 0, now())
                    """,
                    notifications.Select(n => new { incidentId, n.Channel, n.Target, n.PayloadJson }),
                    tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return new TransitionResult(TransitionOutcome.Applied, incidentId);
        }
        catch (PostgresException ex) when (ex.SqlState == Db.UniqueViolation && ex.ConstraintName == OpenIncidentIndex)
        {
            // Lost the race: someone else's transaction opened the incident and owns its notifications.
            return new TransitionResult(TransitionOutcome.Deduplicated, null);
        }
        catch (PostgresException ex) when (ex.SqlState == Db.ForeignKeyViolation)
        {
            return new TransitionResult(TransitionOutcome.MonitorGone, null);
        }
    }

    public async Task<IReadOnlyList<IncidentRow>> ListAsync(bool openOnly, int limit, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IncidentRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM incidents i
            JOIN monitors m ON m.id = i.monitor_id
            WHERE NOT @openOnly OR i.resolved_at IS NULL
            ORDER BY i.opened_at DESC, i.id DESC
            LIMIT @limit
            """, new { openOnly, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<IncidentRow>> ListForMonitorAsync(long monitorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IncidentRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM incidents i
            JOIN monitors m ON m.id = i.monitor_id
            WHERE i.monitor_id = @monitorId
            ORDER BY i.id
            """, new { monitorId }, cancellationToken: ct));
        return rows.AsList();
    }
}
