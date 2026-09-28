namespace RateMyApi.Worker.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public WebhookChannelOptions Webhook { get; set; } = new();
    public EmailChannelOptions Email { get; set; } = new();

    /// <summary>Delivery attempts before a notification is marked failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    public int BatchSize { get; set; } = 50;
    public int MaxParallelism { get; set; } = 8;
    public int PollIntervalMs { get; set; } = 1_000;

    /// <summary>How long a claimed notification is hidden from other dispatchers while being sent.</summary>
    public int LeaseSeconds { get; set; } = 60;
}

public sealed class WebhookChannelOptions
{
    /// <summary>Target URL; the channel is disabled when empty.</summary>
    public string? Url { get; set; }

    /// <summary>HMAC-SHA256 key for the X-RateMyApi-Signature header.</summary>
    public string? Secret { get; set; }

    public int TimeoutMs { get; set; } = 10_000;
}

public sealed class EmailChannelOptions
{
    /// <summary>Recipient; the channel is disabled when this or SmtpHost is empty.</summary>
    public string? To { get; set; }

    public string From { get; set; } = "ratemyapi@localhost";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    public bool EnableSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int TimeoutMs { get; set; } = 10_000;

    public bool Enabled => !string.IsNullOrWhiteSpace(To) && !string.IsNullOrWhiteSpace(SmtpHost);
}
