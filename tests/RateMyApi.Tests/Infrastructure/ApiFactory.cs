using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace RateMyApi.Tests.Infrastructure;

/// <summary>The real API (same Program, same startup/migrations) on an in-memory test server.</summary>
public sealed class ApiFactory(string connectionString, string? apiKey = ApiFactory.DefaultKey, int statusCacheSeconds = 0)
    : WebApplicationFactory<Program>
{
    public const string DefaultKey = "test-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Api:Key", apiKey ?? "");
        builder.UseSetting("Api:StatusCacheSeconds", statusCacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Demo:Enabled", "false");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }
}
