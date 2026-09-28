using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using RateMyApi.Data.Repositories;
using RateMyApi.Tests.Infrastructure;

namespace RateMyApi.Tests.Acceptance;

/// <summary>Acceptance 7: REFRESH MATERIALIZED VIEW CONCURRENTLY does not block reads from /api/status.</summary>
[Collection(PostgresCollection.Name)]
public sealed class A7_ConcurrentRefreshTests(PostgresFixture postgres)
{
    private const int Monitors = 5;

    [Fact]
    public async Task Refresh_concurrently_does_not_block_status_reads_while_a_plain_refresh_would()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        for (var i = 0; i < Monitors; i++)
            await db.InsertMonitorAsync($"http://svc{i}.test/", name: $"svc-{i}");
        await db.ExecuteAsync("""
            INSERT INTO check_results (monitor_id, checked_at, status_code, latency_ms, success)
            SELECT m.id, ts, 200, (20 + random() * 200)::int, random() > 0.02
            FROM monitors m, generate_series(now() - interval '48 hours', now(), interval '1 minute') ts
            """);
        await new StatsRepository(db.DataSource).RefreshAsync();

        await using var api = new ApiFactory(db.ConnectionString);
        using var client = api.CreateClient();
        (await client.GetAsync("/api/status")).EnsureSuccessStatusCode(); // warm-up: startup, JIT, pools

        // 1) Hold a CONCURRENTLY refresh open. Its lock on the view lives until commit.
        await using (var refresher = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await refresher.BeginTransactionAsync())
        {
            await refresher.ExecuteAsync("REFRESH MATERIALIZED VIEW CONCURRENTLY monitor_hourly_stats", transaction: tx);
            var held = await LocksOnViewAsync(db, refresher);
            Assert.Contains("ExclusiveLock", held);
            Assert.DoesNotContain("AccessExclusiveLock", held);

            var clock = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await client.GetAsync("/api/status", timeout.Token);
            var elapsed = clock.Elapsed;

            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Monitors, status.GetProperty("monitors").GetArrayLength());
            Assert.All(status.GetProperty("monitors").EnumerateArray(), m => Assert.True(m.GetProperty("buckets").GetArrayLength() > 0));
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"/api/status took {elapsed} during a concurrent refresh");

            await tx.CommitAsync();
        }

        // 2) Control, proving the test can detect blocking: a plain REFRESH takes ACCESS EXCLUSIVE
        //    and the very same read waits until it is released.
        await using (var refresher = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await refresher.BeginTransactionAsync())
        {
            await refresher.ExecuteAsync("REFRESH MATERIALIZED VIEW monitor_hourly_stats", transaction: tx);
            Assert.Contains("AccessExclusiveLock", await LocksOnViewAsync(db, refresher));

            var pending = client.GetAsync("/api/status");
            var first = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1.5)));
            Assert.NotSame(pending, first);

            await tx.RollbackAsync();
            (await pending).EnsureSuccessStatusCode();
        }
    }

    private static async Task<IReadOnlyList<string>> LocksOnViewAsync(TestDatabase db, NpgsqlConnection holder) =>
        await db.QueryAsync<string>("""
            SELECT mode FROM pg_locks
            WHERE relation = 'monitor_hourly_stats'::regclass AND pid = @pid AND granted
            """, new { pid = holder.ProcessID });
}
