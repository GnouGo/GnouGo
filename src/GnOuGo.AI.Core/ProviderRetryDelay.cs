using System.Globalization;
using System.Net.Http.Headers;

namespace GnOuGo.AI.Core;

/// <summary>A validated retry header and the absolute instant observed at the transport boundary.</summary>
public sealed record ProviderRetryDelay(string Header, string Value, DateTimeOffset NotBeforeUtc, DateTimeOffset ObservedAtUtc)
{
    public static ProviderRetryDelay? Read(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ProviderRetryDelay? latest = null;
        foreach (var name in new[] { "Retry-After", "retry-after-ms", "x-ms-retry-after-ms" })
        {
            if (!headers.TryGetValues(name, out var values)) continue;
            foreach (var raw in values)
            {
                var value = raw.Trim();
                if (value.Length == 0) continue;
                DateTimeOffset due;
                var headerName = name;
                if (IsUnsignedDecimal(value))
                {
                    var scale = name == "Retry-After" ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMillisecond;
                    var available = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks;
                    due = decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var delta)
                        && delta < (decimal)available / scale ? now.AddTicks((long)decimal.Ceiling(delta * scale)) : DateTimeOffset.MaxValue;
                    if (value.Length > 64)
                    {
                        headerName = "Retry-After";
                        value = decimal.Ceiling((decimal)(due.UtcTicks - now.UtcTicks) / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
                    }
                }
                else if (name == "Retry-After" && RetryConditionHeaderValue.TryParse(value, out var header) && header.Date is { } date)
                {
                    due = date < now ? now : date;
                    value = date.ToString("R", CultureInfo.InvariantCulture);
                }
                else continue;
                if (latest is null || due > latest.NotBeforeUtc) latest = new(headerName, value, due, now);
            }
        }
        return latest;
    }

    private static bool IsUnsignedDecimal(string value)
    {
        var point = false;
        var digit = false;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character)) digit = true;
            else if (character == '.' && !point) point = true;
            else return false;
        }
        return digit;
    }

    public TimeSpan Remaining(DateTimeOffset now) => NotBeforeUtc == DateTimeOffset.MaxValue ? TimeSpan.MaxValue :
        NotBeforeUtc > now ? NotBeforeUtc - now : TimeSpan.Zero;
}
