using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using RateMyApi.Api;
using RateMyApi.Api.Demo;
using RateMyApi.Api.Endpoints;
using RateMyApi.Api.Status;
using RateMyApi.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRateMyApiData(builder.Configuration);
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<WebhookSink>();
builder.Services.AddHostedService<DemoSeedService>();

var app = builder.Build();

// Migrations + current partitions before serving (advisory-locked; workers may be doing the same).
await app.Services.GetRequiredService<DatabaseBootstrapper>().RunAsync(app.Lifetime.ApplicationStopping);

if (string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.Key))
    app.Logger.LogWarning("Api:Key is not set: management endpoints are unauthenticated");

app.UseExceptionHandler();

// Status page: /web (index.html + app.jsx), no build step.
var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
if (Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(webRoot);
    var contentTypes = new FileExtensionContentTypeProvider();
    contentTypes.Mappings[".jsx"] = "text/babel; charset=utf-8";
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        ContentTypeProvider = contentTypes,
        // Revalidate (cheap 304 via ETag) instead of heuristic caching, so upgrades never run stale app.jsx.
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });
}

app.MapRateMyApiEndpoints();
if (app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
    app.MapDemoEndpoints();

app.Run();

public partial class Program;
