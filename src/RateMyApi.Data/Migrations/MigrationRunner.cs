using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RateMyApi.Data.Migrations;

public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);

/// <summary>
/// Applies the numbered <c>migrations/NNNN_name.sql</c> files (embedded at build time) in order.
/// Each file runs in its own transaction together with its <c>schema_migrations</c> row; the whole
/// run holds a session advisory lock so the API and every worker replica can start simultaneously.
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    private const string ResourcePrefix = "migrations/";

    [GeneratedRegex(@"^(\d{4})_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    public static IReadOnlyList<MigrationScript> LoadEmbedded()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            var fileName = resource[ResourcePrefix.Length..];
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
                throw new InvalidOperationException($"Migration '{fileName}' must be named NNNN_name.sql.");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

            scripts.Add(new MigrationScript(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), fileName, sql, checksum));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key:D4}.");

        return scripts;
    }

    /// <returns>The number of migrations applied by this call.</returns>
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var scripts = LoadEmbedded();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@key)", new { key = AdvisoryLocks.Migrations },
            commandTimeout: Db.MaintenanceTimeoutSeconds, cancellationToken: ct));
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    int         PRIMARY KEY,
                    name       text        NOT NULL,
                    checksum   text        NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, cancellationToken: ct));

            var applied = (await conn.QueryAsync<(int Version, string Checksum)>(new CommandDefinition(
                    "SELECT version, checksum FROM schema_migrations", cancellationToken: ct)))
                .ToDictionary(r => r.Version, r => r.Checksum);

            var count = 0;
            foreach (var script in scripts)
            {
                if (applied.TryGetValue(script.Version, out var checksum))
                {
                    if (!string.Equals(checksum, script.Checksum, StringComparison.OrdinalIgnoreCase))
                        logger.LogWarning("Migration {Migration} was modified after being applied; it will not be re-run.", script.Name);
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(
                    script.Sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES (@Version, @Name, @Checksum)",
                    new { script.Version, script.Name, script.Checksum }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);

                logger.LogInformation("Applied migration {Migration}", script.Name);
                count++;
            }

            return count;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLocks.Migrations });
        }
    }
}
