using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RateMyApi.Data;

namespace RateMyApi.Worker;

// Not top-level statements: the test project references both the API and the worker, and the API
// owns the global Program type used by WebApplicationFactory<Program>.
internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddRateMyApiWorker(builder.Configuration);
        builder.Services.AddRateMyApiWorkerHostedServices();
        // A claimed batch always finishes before exit (see CheckScheduler); allow for the longest check timeout.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

        using var host = builder.Build();
        await host.Services.GetRequiredService<DatabaseBootstrapper>().RunAsync();
        await host.RunAsync();
    }
}
