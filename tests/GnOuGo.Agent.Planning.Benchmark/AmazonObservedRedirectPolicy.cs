using System.Text.RegularExpressions;

// Explicit isolated-test authority; no scenario rules belong in Browser itself.
internal static class AmazonObservedRedirectPolicy
{
    internal const string Id = "amazon-observed-redirects-v1";
    internal const string Approval = "user-strict-amazon-extension-20261108";
    internal const string PlanningFacts = "Authorized browser observation policy amazon-observed-redirects-v1: GET only literal links observed in the complete Amazon.fr search, preserving the first product occurrences and sponsored results. Never construct a product URL from an identifier or extract a replacement target from advertising query parameters. An observed /sspa/click link may be followed only while its source search remains the current live browser observation; browser_close or another search expires that authority. Keep the browser alive until dependent visits finish, then perform the required cleanup even on failure. Every redirect hop must satisfy the explicit Amazon.fr route policy; retain the actual redirect and destination evidence. Missing evidence remains a failure, never an invented link or successful visit.";
    internal const string UrlPattern = @"https://(?:www\.)?amazon\.fr/(?:s\?[^#]+|sspa/click\?[^#]+|(?:[^/?#]+/)?dp/[A-Za-z0-9]{10}(?:[/?][^#]*)?|gp/product/[A-Za-z0-9]{10}(?:[/?][^#]*)?|gp/aw/d/[A-Za-z0-9]{10}(?:\?[^#]*)?)";
    private static readonly Regex Routes = new(@"\A(?:" + UrlPattern + @")\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static bool Allows(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && Routes.IsMatch(value);

    internal static bool IsEntry(string value) => Allows(value) && new Uri(value).AbsolutePath == "/sspa/click";

    // Classify all observed card links, including review fragments that are not
    // navigable under this policy. This never constructs a navigation target.
    internal static bool IsSourceEntry(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath == "/sspa/click" && Allows(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));

    internal static void Validate(string? policy, string scenario)
    {
        if (policy is not null && (policy != Id || scenario != "amazon"))
            throw new InvalidOperationException("Unknown or mismatched captured browser observation policy.");
    }
}
