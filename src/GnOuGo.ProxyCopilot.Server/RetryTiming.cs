using GnOuGo.AI.Core;

namespace GnOuGo.ProxyCopilot.Server;

public static class RetryTiming
{
    public static TimeSpan Delay(HttpResponseMessage response, int attempt, int initial, int maximum, DateTimeOffset now)
    {
        if (ProviderRetryDelay.Read(response.Headers, now) is { } timing) return timing.Remaining(now);
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Min(maximum, initial * Math.Pow(2, Math.Min(attempt - 1, 30))));
    }
}
