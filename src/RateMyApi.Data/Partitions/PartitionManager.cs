using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace RateMyApi.Data.Partitions;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Partitions whose upper bound is at or before (start of current month - Months) are dropped.</summary>
    public int Months { get; set; } = 6;

    /// <summary>Partitions kept ready beyond the current month.</summary>
    public int PartitionsAhead { get; set; } = 2;
}

public sealed record PartitionInfo(string Name, DateTime From, DateTime To, bool DetachPending);

public sealed record RolloverReport(IReadOnlyList<string> Created, IReadOnlyList<string> Dropped);

/// <summary>
/// Owns the monthly <c>check_results_YYYY_MM</c> partitions. Every operation is idempotent and runs
/// under a session advisory lock, so any number of workers can run it at once.
/// </summary>
public sealed partial class PartitionManager(
    NpgsqlDataSource dataSource,
    IOptions<RetentionOptions> options,
    ILogger<PartitionManager> logger)
{
    public const string ParentTable = "check_results";

    [GeneratedRegex(@"^check_results_(\d{4})_(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionNamePattern();

    public static string PartitionName(DateTime monthStart) =>
        string.Create(CultureInfo.InvariantCulture, $"{ParentTable}_{monthStart.Year:D4}_{monthStart.Month:D2}");

    public static DateTime MonthStart(DateTime utc) => new(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Ensures partitions for the current month + <see cref="RetentionOptions.PartitionsAhead"/>, then
    /// (optionally) DETACHes and DROPs partitions whose upper bound is older than the retention cutoff.
    /// </summary>
    public async Task<RolloverReport> RunAsync(DateTime utcNow, bool dropExpired = true, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (settings.Months < 1) throw new InvalidOperationException("Retention:Months must be >= 1.");
        if (settings.PartitionsAhead < 0) throw new InvalidOperationException("Retention:PartitionsAhead must be >= 0.");

        var currentMonth = MonthStart(utcNow);
        var created = new List<string>();
        var dropped = new List<string>();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Blocking (not try-) lock: a worker that starts while another is rolling over waits, and
        // then finds the partitions it needs before its first insert.
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@key)", new { key = AdvisoryLocks.PartitionRollover },
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        try
        {
            for (var i = 0; i <= settings.PartitionsAhead; i++)
            {
                if (await EnsurePartitionAsync(conn, currentMonth.AddMonths(i), ct) is { } name)
                    created.Add(name);
            }

            if (dropExpired)
            {
                var cutoff = currentMonth.AddMonths(-settings.Months);
                foreach (var partition in await ListPartitionsAsync(conn, ct))
                {
                    if (partition.To > cutoff)
                        continue;

                    await DetachAndDropAsync(conn, partition, ct);
                    dropped.Add(partition.Name);
                }
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLocks.PartitionRollover });
        }

        if (created.Count > 0 || dropped.Count > 0)
        {
            logger.LogInformation("Partition rollover: created [{Created}], dropped [{Dropped}]",
                string.Join(", ", created), string.Join(", ", dropped));
        }

        return new RolloverReport(created, dropped);
    }

    /// <summary>Creates the partition for the month containing <paramref name="utc"/> if missing.</summary>
    public async Task<bool> EnsurePartitionForAsync(DateTime utc, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@key)", new { key = AdvisoryLocks.PartitionRollover },
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        try
        {
            return await EnsurePartitionAsync(conn, MonthStart(utc), ct) is not null;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLocks.PartitionRollover });
        }
    }

    public async Task<IReadOnlyList<PartitionInfo>> ListPartitionsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ListPartitionsAsync(conn, ct);
    }

    /// <returns>The partition name if this call created it, otherwise null.</returns>
    private static async Task<string?> EnsurePartitionAsync(NpgsqlConnection conn, DateTime monthStart, CancellationToken ct)
    {
        var name = PartitionName(monthStart);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass(@name) IS NOT NULL", new { name }, cancellationToken: ct));
        if (exists)
            return null;

        // Identifiers and partition bounds cannot be bound parameters. Both are derived from a
        // DateTime (never from user input); bounds carry an explicit UTC offset so month edges do
        // not depend on the session TimeZone.
        var from = monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = monthStart.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await conn.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF {ParentTable} " +
            $"FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00')",
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        return name;
    }

    private async Task DetachAndDropAsync(NpgsqlConnection conn, PartitionInfo partition, CancellationToken ct)
    {
        // CONCURRENTLY only takes SHARE UPDATE EXCLUSIVE on the parent, so inserts into the live
        // partitions and status-page reads keep flowing. If a previous run was interrupted mid-way
        // the partition is left "detach pending" and must be finalized instead.
        var detach = partition.DetachPending
            ? $"ALTER TABLE {ParentTable} DETACH PARTITION {partition.Name} FINALIZE"
            : $"ALTER TABLE {ParentTable} DETACH PARTITION {partition.Name} CONCURRENTLY";

        await conn.ExecuteAsync(new CommandDefinition(detach, commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            $"DROP TABLE IF EXISTS {partition.Name}", commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));

        logger.LogInformation("Dropped expired partition {Partition} (upper bound {UpperBound:yyyy-MM-dd})", partition.Name, partition.To);
    }

    private async Task<IReadOnlyList<PartitionInfo>> ListPartitionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string Name, bool DetachPending)>(new CommandDefinition("""
            SELECT c.relname AS name, i.inhdetachpending AS detach_pending
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'check_results'::regclass
            ORDER BY c.relname
            """, cancellationToken: ct));

        var partitions = new List<PartitionInfo>();
        foreach (var (name, detachPending) in rows)
        {
            var match = PartitionNamePattern().Match(name);
            if (!match.Success)
            {
                logger.LogWarning("Ignoring partition {Partition}: it does not follow the check_results_YYYY_MM convention", name);
                continue;
            }

            var from = new DateTime(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                1, 0, 0, 0, DateTimeKind.Utc);
            partitions.Add(new PartitionInfo(name, from, from.AddMonths(1), detachPending));
        }

        return partitions;
    }
}
