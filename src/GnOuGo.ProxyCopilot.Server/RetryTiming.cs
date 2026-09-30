using System.Globalization;
using System.Net.Http.Headers;

namespace GnOuGo.ProxyCopilot.Server;

public static class RetryTiming
{
    public static TimeSpan Delay(HttpResponseMessage response, int attempt, int initial, int maximum, DateTimeOffset now)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var headers = values.ToArray();
            if (headers.Length == 1)
            {
                var value = headers[0].Trim();
                // Even an overflowing delta is a valid request to wait longer than our
                // budget. Do not mistake it for a missing header and retry early.
                if (value.Length > 0 && value.All(char.IsAsciiDigit))
                    return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                        && seconds < (ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
                        ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
                if (RetryConditionHeaderValue.TryParse(value, out var header) && header.Date is { } date)
                    return date > now ? date - now : TimeSpan.Zero;
            }
        }
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Min(maximum, initial * Math.Pow(2, Math.Min(attempt - 1, 30))));
    }
}
