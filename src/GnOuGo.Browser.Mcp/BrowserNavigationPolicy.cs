namespace GnOuGo.Browser.Mcp;

public static class BrowserNavigationPolicy
{
    private static readonly string[] AllowedSchemes = ["http", "https"];

    public static Uri ValidateNavigationTarget(string url, BrowserServerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A non-empty absolute URL is required.", nameof(url));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("The URL must be absolute.", nameof(url));

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only http:// and https:// URLs are allowed.");

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("The URL must contain a valid host.");

        if (settings.AllowedHosts.Count > 0 && !IsHostAllowed(uri.Host, settings.AllowedHosts))
        {
            throw new InvalidOperationException(
                $"Host '{uri.Host}' is not allowed by the current browser policy.");
        }

        return uri;
    }

    public static bool IsHostAllowed(string host, IEnumerable<string> rules)
    {
        foreach (var rawRule in rules)
        {
            var rule = rawRule?.Trim();
            if (string.IsNullOrWhiteSpace(rule))
                continue;

            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = rule[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && host.Length > suffix.Length)
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(host, rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

