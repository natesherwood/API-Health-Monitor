using System.Text;
using System.Text.Json;
using RateMyApi.Core.Monitoring;
using RateMyApi.Core.Notifications;
using RateMyApi.Core.Sharding;
using RateMyApi.Core.Stats;

namespace RateMyApi.Tests.Unit;

public sealed class PercentileTests
{
    [Fact]
    public void Matches_percentile_disc_on_known_inputs()
    {
        Assert.Equal(19, Percentile.Disc(Enumerable.Range(1, 20), 0.95));   // ceil(19.0) -> 19th
        Assert.Equal(95, Percentile.Disc(Enumerable.Range(1, 100), 0.95));
        Assert.Equal(10, Percentile.Disc(Enumerable.Range(1, 10), 0.95));   // ceil(9.5) -> 10th
        Assert.Equal(7, Percentile.Disc(new[] { 7 }, 0.95));
        Assert.Equal(50, Percentile.Disc(new[] { 90, 10, 50, 30, 70 }, 0.5)); // unsorted input
        Assert.Equal(1, Percentile.Disc(new[] { 3, 1, 2 }, 0.0));             // p=0 -> minimum
        Assert.Equal(3, Percentile.Disc(new[] { 3, 1, 2 }, 1.0));             // p=1 -> maximum
    }

    [Fact]
    public void Ignores_nulls_like_the_sql_aggregate()
    {
        Assert.Equal(40, Percentile.P95([null, 10, null, 40, 20, null]));
        Assert.Null(Percentile.P95([null, null]));
        Assert.Null(Percentile.Disc(Array.Empty<int>(), 0.95));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Rejects_fractions_outside_zero_to_one(double fraction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentile.Disc(new[] { 1, 2, 3 }, fraction));
}

public sealed class NotificationPrimitivesTests
{
    [Fact]
    public void Backoff_is_two_to_the_attempts_seconds_and_gives_up_at_max_attempts()
    {
        Assert.Equal(
            new[] { 2, 4, 8, 16 }.Select(s => TimeSpan.FromSeconds(s)),
            Enumerable.Range(1, 4).Select(NotificationBackoff.DelayAfter));
        Assert.False(NotificationBackoff.IsExhausted(4));
        Assert.True(NotificationBackoff.IsExhausted(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationBackoff.DelayAfter(0));
    }

    [Fact]
    public void Signature_is_hex_hmac_sha256_of_the_body()
    {
        var body = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        var signature = WebhookSignature.Compute("key", body);

        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", signature);
        Assert.True(WebhookSignature.Verify("key", body, signature));
        Assert.False(WebhookSignature.Verify("other-key", body, signature));
        Assert.False(WebhookSignature.Verify("key", Encoding.UTF8.GetBytes("tampered"), signature));
        Assert.False(WebhookSignature.Verify("key", body, null));
    }

    [Fact]
    public void Payload_serializes_snake_case_with_monitor_states_p95_and_timestamp()
    {
        var payload = new TransitionPayload(TransitionPayload.StateChangedEvent, new MonitorRef(7, "api", "https://api.test/health"),
            "ok", "down", 812, "3 consecutive failures (threshold 3)", 42, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));

        using var json = JsonDocument.Parse(payload.ToJson());
        var root = json.RootElement;
        Assert.Equal("monitor.state_changed", root.GetProperty("event").GetString());
        Assert.Equal(7, root.GetProperty("monitor").GetProperty("id").GetInt64());
        Assert.Equal("ok", root.GetProperty("old_state").GetString());
        Assert.Equal("down", root.GetProperty("new_state").GetString());
        Assert.Equal(812, root.GetProperty("p95_ms").GetInt32());
        Assert.Equal(42, root.GetProperty("incident_id").GetInt64());
        Assert.Equal("2026-09-15T12:00:00Z", root.GetProperty("timestamp").GetString());
        Assert.Equal(payload, TransitionPayload.FromJson(payload.ToJson()));
    }
}

public sealed class ShardAssignmentTests
{
    [Fact]
    public void Every_monitor_is_owned_by_exactly_one_worker_for_any_worker_count()
    {
        for (var count = 1; count <= 7; count++)
        {
            var shards = Enumerable.Range(0, count).Select(id => new ShardAssignment(id, count)).ToArray();
            for (long monitorId = 1; monitorId <= 500; monitorId++)
                Assert.Single(shards, s => s.Owns(monitorId));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    public void Invalid_assignments_are_rejected(int workerId, int workerCount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShardAssignment(workerId, workerCount));
}

public sealed class MonitorInputTests
{
    [Fact]
    public void Defaults_fill_optional_fields_and_the_result_is_valid()
    {
        var input = new MonitorInput { Name = " api ", Url = "https://api.test/health", Method = "get" }.WithDefaults();

        Assert.Equal("api", input.Name);
        Assert.Equal("GET", input.Method);
        Assert.Equal(200, input.ExpectedStatus);
        Assert.Equal(60, input.IntervalSeconds);
        Assert.Equal(5_000, input.TimeoutMs);
        Assert.True(input.Enabled);
        Assert.Equal(1_000, input.DegradedP95Ms);
        Assert.Equal(3, input.DownConsecutiveFailures);
        Assert.Empty(input.Validate());
    }

    [Theory]
    [InlineData("name", null, "https://a.test")]
    [InlineData("url", "x", "not a url")]
    [InlineData("url", "x", "ftp://a.test")]
    public void Missing_name_or_bad_url_is_invalid(string field, string? name, string url) =>
        Assert.Contains(field, new MonitorInput { Name = name, Url = url }.WithDefaults().Validate().Keys);

    [Fact]
    public void Numeric_ranges_and_timeout_vs_interval_are_validated()
    {
        var baseline = new MonitorInput { Name = "x", Url = "https://a.test" };
        Assert.Contains("method", (baseline with { Method = "TRACE" }).WithDefaults().Validate().Keys);
        Assert.Contains("expected_status", (baseline with { ExpectedStatus = 99 }).WithDefaults().Validate().Keys);
        Assert.Contains("interval_seconds", (baseline with { IntervalSeconds = 0 }).WithDefaults().Validate().Keys);
        Assert.Contains("timeout_ms", (baseline with { IntervalSeconds = 2, TimeoutMs = 2_500 }).WithDefaults().Validate().Keys);
        Assert.Contains("degraded_p95_ms", (baseline with { DegradedP95Ms = 0 }).WithDefaults().Validate().Keys);
        Assert.Contains("down_consecutive_failures", (baseline with { DownConsecutiveFailures = 0 }).WithDefaults().Validate().Keys);
    }
}
