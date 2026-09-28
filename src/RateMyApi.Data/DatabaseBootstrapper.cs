using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Npgsql;
using RateMyApi.Data.Migrations;
using RateMyApi.Data.Partitions;

namespace RateMyApi.Data;

/// <summary>
/// Startup sequence shared by the API and the worker: apply migrations, then make sure the
/// partitions for "now" exist so the first insert cannot fail. Retries while the database boots.
/// </summary>
public sealed class DatabaseBootstrapper(
    MigrationRunner migrations,
    PartitionManager partitions,
    TimeProvider time,
    ILogger<DatabaseBootstrapper> logger)
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task RunAsync(CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await migrations.ApplyAsync(ct);
                await partitions.RunAsync(time.GetUtcNow().UtcDateTime, dropExpired: false, ct);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Database not ready ({Reason}); attempt {Attempt}/{Max}, retrying in {Delay}",
                    ex.Message, attempt, MaxAttempts, RetryDelay);
                await Task.Delay(RetryDelay, time, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is NpgsqlException { IsTransient: true } or SocketException or TimeoutException;
}
