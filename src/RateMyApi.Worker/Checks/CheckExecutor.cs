using System.Diagnostics;
using System.Net.Sockets;
using RateMyApi.Core.Monitoring;

namespace RateMyApi.Worker.Checks;

/// <summary>Executes one HTTP check. Never throws for check failures; they become outcomes.</summary>
public sealed class CheckExecutor(IHttpClientFactory httpClientFactory, TimeProvider time)
{
    public const string HttpClientName = "checks";
    private const int MaxErrorLength = 500;

    /// <param name="abortToken">Hard abort only (not graceful shutdown); graceful drains let checks finish.</param>
    public async Task<CheckOutcome> ExecuteAsync(MonitorDefinition monitor, CancellationToken abortToken = default)
    {
        var checkedAt = time.GetUtcNow().UtcDateTime;
        var started = Stopwatch.GetTimestamp();

        // Each check gets its own deadline; the named client's HttpClient.Timeout is infinite so this is the only one.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(monitor.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, abortToken);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(new HttpMethod(monitor.Method), monitor.Url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            var latencyMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var status = (int)response.StatusCode;
            var success = status == monitor.ExpectedStatus;
            return new CheckOutcome(monitor.Id, checkedAt, status, latencyMs, success,
                success ? null : $"expected status {monitor.ExpectedStatus}, got {status}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !abortToken.IsCancellationRequested)
        {
            return Failure(monitor, checkedAt, CheckOutcome.TimeoutError);
        }
        catch (HttpRequestException ex)
        {
            return Failure(monitor, checkedAt, Describe(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Malformed URL at runtime, TLS failures surfaced differently per platform, etc.
            return Failure(monitor, checkedAt, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CheckOutcome Failure(MonitorDefinition monitor, DateTime checkedAt, string error) =>
        new(monitor.Id, checkedAt, StatusCode: null, LatencyMs: null, Success: false, Truncate(error));

    private static string Describe(HttpRequestException ex)
    {
        var socket = ex.InnerException as SocketException ?? ex.InnerException?.InnerException as SocketException;
        return socket is null ? ex.Message : $"{ex.Message} ({socket.SocketErrorCode})";
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
