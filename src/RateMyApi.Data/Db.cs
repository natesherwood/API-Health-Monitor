using Dapper;

namespace RateMyApi.Data;

internal static class Db
{
    /// <summary>Maps snake_case columns onto PascalCase properties. Idempotent.</summary>
    public static void ConfigureDapper() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    /// <summary>Command timeout (seconds) for maintenance statements: migrations, DDL, matview refresh.</summary>
    public const int MaintenanceTimeoutSeconds = 600;

    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
}

/// <summary>
/// Advisory lock keys. Everything that must not run concurrently across the API and N worker
/// replicas is serialised with PostgreSQL advisory locks, so no extra coordination service exists.
/// </summary>
public static class AdvisoryLocks
{
    // Single bigint keys ("RMAPI" + ordinal).
    public const long Migrations = 0x524D_4150_0001;
    public const long PartitionRollover = 0x524D_4150_0002;
    public const long StatsRefresh = 0x524D_4150_0003;
    public const long Seed = 0x524D_4150_0004;

    /// <summary>Two-int form namespace for worker slot leases: pg_advisory_lock(WorkerSlotNamespace, slot).</summary>
    public const int WorkerSlotNamespace = 0x524D_5753;
}
