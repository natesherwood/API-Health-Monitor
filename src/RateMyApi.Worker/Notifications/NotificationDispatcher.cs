using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Notifications;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Worker.Notifications;

/// <summary>
/// Drains the notifications outbox. Rows are claimed with FOR UPDATE SKIP LOCKED (safe with N worker
/// replicas), delivered, then marked sent or rescheduled with exponential backoff
/// (next_attempt_at = now() + 2^attempts s) until MaxAttempts, after which they are marked failed.
/// </summary>
public sealed class NotificationDispatcher(
    NotificationRepository notifications,
    IEnumerable<INotificationSender> senders,
    IOptions<NotificationOptions> options,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private const int MaxErrorLength = 1_000;
    private readonly Dictionary<string, INotificationSender> _senders = senders.ToDictionary(s => s.Channel, StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(10, settings.PollIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification dispatch tick failed");
            }

            if (claimed >= settings.BatchSize)
                continue;

            try
            {
                await Task.Delay(pollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <returns>The number of notifications claimed.</returns>
    public async Task<int> DispatchOnceAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var batch = await notifications.ClaimDueAsync(settings.BatchSize, TimeSpan.FromSeconds(settings.LeaseSeconds), ct);
        if (batch.Count == 0)
            return 0;

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.MaxParallelism) },
            async (notification, _) => await DeliverAsync(notification, settings.MaxAttempts));

        return batch.Count;
    }

    private async Task DeliverAsync(NotificationRow notification, int maxAttempts)
    {
        var attempts = notification.Attempts + 1;
        try
        {
            if (!_senders.TryGetValue(notification.Channel, out var sender))
                throw new InvalidOperationException($"No sender registered for channel '{notification.Channel}'.");

            await sender.SendAsync(notification, CancellationToken.None);
            await notifications.MarkSentAsync(notification.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var error = ex.Message.Length <= MaxErrorLength ? ex.Message : ex.Message[..MaxErrorLength];
            try
            {
                if (NotificationBackoff.IsExhausted(attempts, maxAttempts))
                {
                    await notifications.MarkFailedAsync(notification.Id, attempts, error, CancellationToken.None);
                    logger.LogWarning("Notification {Id} ({Channel}) failed permanently after {Attempts} attempts: {Error}",
                        notification.Id, notification.Channel, attempts, error);
                }
                else
                {
                    var delay = NotificationBackoff.DelayAfter(attempts);
                    await notifications.ScheduleRetryAsync(notification.Id, attempts, error, delay, CancellationToken.None);
                    logger.LogInformation("Notification {Id} ({Channel}) attempt {Attempts} failed, retry in {Delay}: {Error}",
                        notification.Id, notification.Channel, attempts, delay, error);
                }
            }
            catch (Exception persistError)
            {
                // The lease will expire and the row will be retried.
                logger.LogError(persistError, "Could not record delivery failure for notification {Id}", notification.Id);
            }
        }
    }
}
