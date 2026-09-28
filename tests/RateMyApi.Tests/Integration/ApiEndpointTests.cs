using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;

namespace RateMyApi.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ApiEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Monitor_crud_requires_the_api_key_and_validates_input()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        await using var api = new ApiFactory(db.ConnectionString);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/monitors")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.DefaultKey);

        var invalid = await client.PostAsJsonAsync("/api/monitors", new { name = "", url = "not-a-url", timeout_ms = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("url", out _));
        Assert.True(errors.TryGetProperty("timeout_ms", out _));

        var created = await client.PostAsJsonAsync("/api/monitors", new
        {
            name = "billing", url = "https://billing.test/health", interval_seconds = 30, timeout_ms = 2000,
            degraded_p95_ms = 400, down_consecutive_failures = 2,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var monitor = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = monitor.GetProperty("id").GetInt64();
        Assert.Equal($"/api/monitors/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal("ok", monitor.GetProperty("state").GetString());
        Assert.Equal("GET", monitor.GetProperty("method").GetString());
        Assert.Equal(200, monitor.GetProperty("expected_status").GetInt32());
        Assert.Equal(400, monitor.GetProperty("degraded_p95_ms").GetInt32());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/monitors");
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt64());

        var updated = await client.PutAsJsonAsync($"/api/monitors/{id}", new { name = "billing-v2", url = "https://billing.test/v2/health", interval_seconds = 10, timeout_ms = 1000 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("billing-v2", 10, 1000), (after.GetProperty("name").GetString(), after.GetProperty("interval_seconds").GetInt32(), after.GetProperty("degraded_p95_ms").GetInt32()));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/monitors/999999", new { name = "x", url = "https://x.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/monitors/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/monitors/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/monitors/{id}")).StatusCode);
    }

    [Fact]
    public async Task Status_is_public_and_series_and_incidents_are_served_from_the_database()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var id = await db.InsertMonitorAsync("https://internal.example/secret-path", name: "payments", downConsecutiveFailures: 2);
        await db.ExecuteAsync("""
            INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success)
            SELECT @id, ts, 200, 80, true FROM generate_series(now() - interval '3 hours', now(), interval '1 minute') ts
            """, new { id });
        await db.ExecuteAsync(
            "INSERT INTO incidents (monitor_id, state, trigger_reason, p95_at_open) VALUES (@id, 'down', '2 consecutive failures (threshold 2)', 80)",
            new { id });
        await new StatsRepository(db.DataSource).RefreshAsync();

        await using var api = new ApiFactory(db.ConnectionString);
        using var anonymous = api.CreateClient();

        var status = await anonymous.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("down", status.GetProperty("overall").GetString());
        var entry = Assert.Single(status.GetProperty("monitors").EnumerateArray());
        Assert.Equal("payments", entry.GetProperty("name").GetString());
        Assert.False(entry.TryGetProperty("url", out _), "the public status payload must not leak monitor URLs");
        Assert.InRange(entry.GetProperty("buckets").GetArrayLength(), 3, 4);
        Assert.Equal(100.0, entry.GetProperty("uptime_pct").GetDouble());
        Assert.Equal("2 consecutive failures (threshold 2)", Assert.Single(status.GetProperty("incidents").EnumerateArray()).GetProperty("trigger_reason").GetString());

        var health = await anonymous.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.Equal("ok", health.GetProperty("status").GetString());

        var page = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("app.jsx", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/app.jsx")).StatusCode);

        using var admin = api.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.DefaultKey);
        var series = await admin.GetFromJsonAsync<JsonElement>($"/api/monitors/{id}/series?hours=2");
        Assert.Equal(2, series.GetProperty("hours").GetInt32());
        Assert.All(series.GetProperty("buckets").EnumerateArray(), b => Assert.Equal(80, b.GetProperty("p95_ms").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/monitors/{id}/series?hours=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/monitors/999999/series")).StatusCode);

        var open = await admin.GetFromJsonAsync<JsonElement>("/api/incidents?open=true");
        Assert.Equal("payments", Assert.Single(open.EnumerateArray()).GetProperty("monitor_name").GetString());
    }
}
