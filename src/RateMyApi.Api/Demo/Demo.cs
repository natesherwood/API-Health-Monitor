using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Notifications;
using RateMyApi.Data.Seeding;

namespace RateMyApi.Api.Demo;

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Maps /demo/targets/* and /demo/webhook-sink.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seed demo monitors on first boot (monitors table empty).</summary>
    public bool Seed { get; set; }

    /// <summary>Base URL the workers use to reach this API (http://api:8080 inside compose).</summary>
    public string TargetBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Hours of synthetic history to backfill when seeding; 0 disables.</summary>
    public int BackfillHours { get; set; } = 96;

    /// <summary>Key used by the sink to verify X-RateMyApi-Signature (the workers' Notifications:Webhook:Secret).</summary>
    public string? WebhookSecret { get; set; }
}

public sealed record WebhookDelivery(DateTime ReceivedAt, string? DeliveryId, bool? SignatureValid, JsonElement Payload);

/// <summary>Keeps the last 50 webhook deliveries in memory for inspection.</summary>
public sealed class WebhookSink
{
    private const int Capacity = 50;
    private readonly ConcurrentQueue<WebhookDelivery> _deliveries = new();

    public void Add(WebhookDelivery delivery)
    {
        _deliveries.Enqueue(delivery);
        while (_deliveries.Count > Capacity)
            _deliveries.TryDequeue(out _);
    }

    public IReadOnlyList<WebhookDelivery> Recent() => _deliveries.Reverse().ToArray();
}

public sealed class DemoSeedService(IOptions<DemoOptions> options, DemoDataSeeder seeder, ILogger<DemoSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.Seed)
            return;

        try
        {
            if (!await seeder.SeedAsync(settings.TargetBaseUrl, settings.BackfillHours, cancellationToken))
                logger.LogInformation("Demo seed skipped: monitors already exist");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Demo seeding failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DemoEndpoints
{
    /// <summary>
    /// Synthetic upstreams for the seeded monitors, covering every state: ok, slow (degraded),
    /// flaky (flapping), down, hang (timeouts) and wave (latency oscillating across the threshold
    /// every 20 minutes: ok -> degraded -> resolved -> ok).
    /// </summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/demo");

        demo.Map("/targets/{kind}", async (string kind, CancellationToken ct) =>
        {
            var random = Random.Shared;
            switch (kind)
            {
                case "ok":
                    await Task.Delay(random.Next(5, 40), ct);
                    return Results.Ok(new { status = "ok" });
                case "slow":
                    await Task.Delay(random.Next(350, 900), ct);
                    return Results.Ok(new { status = "ok", note = "slow" });
                case "flaky":
                    await Task.Delay(random.Next(20, 80), ct);
                    return random.NextDouble() < 0.25 ? Results.StatusCode(StatusCodes.Status500InternalServerError) : Results.Ok(new { status = "ok" });
                case "down":
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                case "hang":
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return Results.Ok(new { status = "eventually" });
                case "wave":
                    var seconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    var latency = Math.Max(20, 180 + 220 * Math.Sin(2 * Math.PI * seconds / 1200.0) + random.Next(0, 40));
                    await Task.Delay((int)latency, ct);
                    return Results.Ok(new { status = "ok", latency_ms = (int)latency });
                default:
                    return Results.NotFound();
            }
        });

        demo.MapPost("/webhook-sink", async (HttpRequest request, WebhookSink sink, IOptions<DemoOptions> options, ILogger<WebhookSink> logger) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();

            var secret = options.Value.WebhookSecret;
            bool? signatureValid = string.IsNullOrEmpty(secret)
                ? null
                : WebhookSignature.Verify(secret, body, request.Headers[WebhookSignature.HeaderName]);
            if (signatureValid == false)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid signature");

            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            sink.Add(new WebhookDelivery(DateTime.UtcNow, request.Headers["X-RateMyApi-Delivery"], signatureValid, payload));
            logger.LogInformation("Webhook delivery {Delivery}: {Body}", request.Headers["X-RateMyApi-Delivery"].ToString(), payload.GetRawText());
            return Results.NoContent();
        });

        demo.MapGet("/webhook-sink", (WebhookSink sink) => Results.Ok(sink.Recent()));
        return app;
    }
}
