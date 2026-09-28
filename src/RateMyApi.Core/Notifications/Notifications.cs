using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RateMyApi.Core.Notifications;

public static class NotificationChannels
{
    public const string Webhook = "webhook";
    public const string Email = "email";
}

public static class NotificationStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

public sealed record MonitorRef(long Id, string Name, string Url);

/// <summary>Body of a state-change notification (webhook JSON and email content).</summary>
public sealed record TransitionPayload(
    [property: JsonPropertyName("event")] string Event,
    MonitorRef Monitor,
    string OldState,
    string NewState,
    int? P95Ms,
    string Reason,
    long IncidentId,
    DateTime Timestamp)
{
    public const string StateChangedEvent = "monitor.state_changed";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TransitionPayload FromJson(string json) =>
        JsonSerializer.Deserialize<TransitionPayload>(json, JsonOptions)
        ?? throw new JsonException("Empty notification payload.");
}

/// <summary>HMAC-SHA256 over the exact request body bytes: <c>X-RateMyApi-Signature: sha256=&lt;hex&gt;</c>.</summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-RateMyApi-Signature";
    private const string Prefix = "sha256=";

    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Prefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(header))
            return false;

        var expected = Encoding.ASCII.GetBytes(Compute(secret, body));
        var actual = Encoding.ASCII.GetBytes(header.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// <summary>Delivery retry policy: after the n-th failed attempt wait 2^n seconds; give up after <c>maxAttempts</c>.</summary>
public static class NotificationBackoff
{
    public const int DefaultMaxAttempts = 5;

    /// <param name="attempts">Attempts made so far, including the one that just failed (>= 1).</param>
    public static TimeSpan DelayAfter(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        return TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempts, 30)));
    }

    public static bool IsExhausted(int attempts, int maxAttempts = DefaultMaxAttempts) => attempts >= maxAttempts;
}
