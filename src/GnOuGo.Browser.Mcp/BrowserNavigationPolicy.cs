namespace GnOuGo.Browser.Mcp;

public static class BrowserNavigationPolicy
{
    public const string HttpUrlPattern = @"^[Hh][Tt][Tt][Pp][Ss]?://[^/?#\s]+[^\s]*$";
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

        if (System.Text.RegularExpressions.Regex.Match(url, HttpUrlPattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)).Length != url.Length)
            throw new ArgumentException("The absolute HTTP URL must escape whitespace.", nameof(url));

        if (settings.AllowedHosts.Count > 0 && !IsHostAllowed(uri.Host, settings.AllowedHosts))
        {
            throw new InvalidOperationException(
                $"Host '{uri.Host}' is not allowed by the current browser policy.");
        }

        // Parsing may remove an explicit default port or collapse dot segments.
        // Neither transformation grants authority absent from the supplied URL.
        // The same rule must authorize both its literal and transmitted forms.
        if (settings.AllowedNavigationUrlPatterns.Count > 0 && !settings.AllowedNavigationUrlPatterns.Any(pattern =>
            Matches(WithExplicitRootPath(url), pattern) && Matches(uri.AbsoluteUri, pattern)))
            throw new InvalidOperationException("The URL is outside the configured navigation routes.");

        return uri;
    }

    private static bool Matches(string url, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(
        url, "\\A(?:" + pattern + ")\\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    // HTTP gives an omitted path the request target "/". Preserve that existing
    // form without normalizing the authority, explicit port, or supplied path.
    private static string WithExplicitRootPath(string url)
    {
        var authorityStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var path = url.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        if (path < 0) return url + "/";
        path += authorityStart;
        return url[path] == '/' ? url : url.Insert(path, "/");
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
