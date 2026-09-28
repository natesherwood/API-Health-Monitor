using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using RateMyApi.Api.Security;
using RateMyApi.Api.Status;
using RateMyApi.Core.Monitoring;
using RateMyApi.Data.Repositories;

namespace RateMyApi.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapRateMyApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Public.
        api.MapGet("/status", async (StatusService status, CancellationToken ct) => Results.Ok(await status.GetAsync(ct)));
        app.MapGet("/healthz", HealthAsync);

        // Management: X-Api-Key when configured.
        var admin = api.MapGroup("").AddEndpointFilter<ApiKeyEndpointFilter>();

        admin.MapGet("/monitors", async (MonitorRepository monitors, IOptions<ApiOptions> options, CancellationToken ct) =>
            Results.Ok(await monitors.ListWithStatusAsync(options.Value.LiveWindow, null, ct)));

        admin.MapGet("/monitors/{id:long}", async (long id, MonitorRepository monitors, IOptions<ApiOptions> options, CancellationToken ct) =>
            (await monitors.ListWithStatusAsync(options.Value.LiveWindow, id, ct)).SingleOrDefault() is { } row
                ? Results.Ok(row)
                : Results.NotFound());

        admin.MapPost("/monitors", async (MonitorInput input, MonitorRepository monitors, IOptions<ApiOptions> options, CancellationToken ct) =>
        {
            var normalized = input.WithDefaults();
            var errors = normalized.Validate();
            if (errors.Count > 0)
                return Results.ValidationProblem(errors.ToDictionary());

            var created = await monitors.CreateAsync(normalized, ct);
            var row = (await monitors.ListWithStatusAsync(options.Value.LiveWindow, created.Id, ct)).Single();
            return Results.Created($"/api/monitors/{created.Id}", row);
        });

        admin.MapPut("/monitors/{id:long}", async (long id, MonitorInput input, MonitorRepository monitors, IOptions<ApiOptions> options, CancellationToken ct) =>
        {
            var normalized = input.WithDefaults();
            var errors = normalized.Validate();
            if (errors.Count > 0)
                return Results.ValidationProblem(errors.ToDictionary());

            if (await monitors.UpdateAsync(id, normalized, ct) is null)
                return Results.NotFound();

            return Results.Ok((await monitors.ListWithStatusAsync(options.Value.LiveWindow, id, ct)).Single());
        });

        admin.MapDelete("/monitors/{id:long}", async (long id, MonitorRepository monitors, CancellationToken ct) =>
            await monitors.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/monitors/{id:long}/series", async (long id, int? hours, MonitorRepository monitors, StatsRepository stats, CancellationToken ct) =>
        {
            var span = hours ?? 24;
            if (span is < 1 or > StatsRepository.MaxHours)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["hours"] = [$"Must be between 1 and {StatsRepository.MaxHours}."],
                });
            }

            if (await monitors.GetAsync(id, ct) is null)
                return Results.NotFound();

            var buckets = await stats.GetSeriesAsync(id, span, ct);
            return Results.Ok(new { monitorId = id, hours = span, buckets });
        });

        admin.MapGet("/incidents", async (bool? open, int? limit, IncidentRepository incidents, CancellationToken ct) =>
            Results.Ok(await incidents.ListAsync(open ?? false, Math.Clamp(limit ?? 100, 1, 500), ct)));

        return app;
    }

    private static async Task<IResult> HealthAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var conn = await dataSource.OpenConnectionAsync(timeout.Token);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: timeout.Token));
            return Results.Ok(new { status = "ok" });
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            return Results.Json(new { status = "unhealthy", error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
