using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RateMyApi.Tests.Infrastructure;

public sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>A real Kestrel server on a random loopback port for the worker's HttpClient to hit.</summary>
public sealed class TestHttpTarget : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHttpTarget(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    /// <summary>Status code returned by POST /hook (webhook receiver); settable per test.</summary>
    public int HookStatus { get; set; } = StatusCodes.Status204NoContent;

    public string Url(string path) => BaseUrl + path;

    public static async Task<TestHttpTarget> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        TestHttpTarget? target = null;

        app.MapGet("/ok", () => Results.Ok());
        app.MapGet("/delay/{ms:int}", async (int ms, CancellationToken ct) =>
        {
            await Task.Delay(ms, ct);
            return Results.Ok();
        });
        app.MapGet("/hang", async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Results.Ok();
        });
        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        app.MapPost("/hook", async (HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            target!.Received.Enqueue(new ReceivedRequest(request.Path, headers, body.ToArray()));
            return Results.StatusCode(target.HookStatus);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        target = new TestHttpTarget(app, address);
        return target;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
