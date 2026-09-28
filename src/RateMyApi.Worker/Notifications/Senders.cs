using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;
using RateMyApi.Core.Notifications;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Worker.Notifications;

public interface INotificationSender
{
    string Channel { get; }

    /// <summary>Delivers the notification; throws on any failure so the dispatcher can retry.</summary>
    Task SendAsync(NotificationRow notification, CancellationToken ct);
}

public sealed class WebhookSender(IHttpClientFactory httpClientFactory, IOptions<NotificationOptions> options) : INotificationSender
{
    public const string HttpClientName = "webhooks";
    public const string DeliveryHeader = "X-RateMyApi-Delivery";
    public const string EventHeader = "X-RateMyApi-Event";

    public string Channel => NotificationChannels.Webhook;

    public async Task SendAsync(NotificationRow notification, CancellationToken ct)
    {
        var settings = options.Value.Webhook;
        var body = Encoding.UTF8.GetBytes(notification.Payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, notification.Target);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add(DeliveryHeader, notification.Id.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(EventHeader, TransitionPayload.StateChangedEvent);
        if (!string.IsNullOrEmpty(settings.Secret))
            request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Compute(settings.Secret, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.TimeoutMs);

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"webhook returned {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }
}

public sealed class EmailSender(IOptions<NotificationOptions> options) : INotificationSender
{
    public string Channel => NotificationChannels.Email;

    public async Task SendAsync(NotificationRow notification, CancellationToken ct)
    {
        var settings = options.Value.Email;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            throw new InvalidOperationException("Notifications:Email:SmtpHost is not configured.");

        var payload = TransitionPayload.FromJson(notification.Payload);
        using var message = new MailMessage(settings.From, notification.Target)
        {
            Subject = $"[RateMyApi] {payload.Monitor.Name}: {payload.OldState} -> {payload.NewState}",
            Body = string.Create(CultureInfo.InvariantCulture, $"""
                Monitor:   {payload.Monitor.Name} (#{payload.Monitor.Id})
                URL:       {payload.Monitor.Url}
                Change:    {payload.OldState} -> {payload.NewState}
                Reason:    {payload.Reason}
                p95:       {(payload.P95Ms is { } p95 ? p95 + " ms" : "n/a")}
                Incident:  #{payload.IncidentId}
                At (UTC):  {payload.Timestamp:yyyy-MM-dd HH:mm:ss}
                """),
            IsBodyHtml = false,
        };

        using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.EnableSsl,
            Timeout = settings.TimeoutMs,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(settings.Username))
            smtp.Credentials = new NetworkCredential(settings.Username, settings.Password);

        await smtp.SendMailAsync(message, ct);
    }
}
