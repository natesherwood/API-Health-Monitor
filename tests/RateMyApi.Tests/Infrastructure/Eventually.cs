using System.Diagnostics;

namespace RateMyApi.Tests.Infrastructure;

public static class Eventually
{
    /// <summary>Polls <paramref name="condition"/> until true; fails the test with <paramref name="because"/> on timeout.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (await condition())
                return;
            if (Stopwatch.GetElapsedTime(started) > timeout)
                Assert.Fail($"Timed out after {timeout.TotalSeconds:0.#}s waiting until {because}.");
            await Task.Delay(50);
        }
    }
}
