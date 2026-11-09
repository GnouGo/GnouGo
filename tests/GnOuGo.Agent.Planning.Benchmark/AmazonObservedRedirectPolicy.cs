using System.Text.RegularExpressions;

// Explicit isolated-test authority; no scenario rules belong in Browser itself.
internal static class AmazonObservedRedirectPolicy
{
    internal const string Id = "amazon-observed-redirects-v1";
    internal const string Approval = "user-strict-amazon-extension-20261108";
    internal const string PlanningFacts = "Authorized browser observation policy amazon-observed-redirects-v1: GET only literal links observed in the complete Amazon.fr search, preserving the first product occurrences and sponsored results. Never construct a product URL from an identifier or extract a replacement target from advertising query parameters. An observed /sspa/click link may be followed only while its source search remains the current live browser observation; browser_close or another search expires that authority. Keep the browser alive until dependent visits finish, then perform the required cleanup even on failure. Every redirect hop must satisfy the explicit Amazon.fr route policy; retain the actual redirect and destination evidence. Missing evidence remains a failure, never an invented link or successful visit."
        + "\nCaptured isolated-host navigation allowlist, matched against the entire absolute URL: " + UrlPattern
        + ". Only HTTPS on www.amazon.fr or amazon.fr, default port, without credentials or fragments is authorized. This allowlist grants no authority to construct a product URL."
        + " The existing browser_get_content operation performs explicit GET acquisition; the configured Browser host validates every redirect destination before contacting it and returns actual navigationEvidence."
        + " During approved delegated execution, the host rejects an /sspa/click entry unless that exact literal link was observed in the complete source search. Dependent product reads retain that source authority in the same live browser; browser_close or a new search clears it. Observation cursors still expire on navigation."
        + " These are existing host guarantees, not missing workflow operations or permission to change the policy. They establish no observed search, visit, product data or execution success.";
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
        if (policy is not null && (!IsObserved(policy) || scenario != "amazon"))
            throw new InvalidOperationException("Unknown or mismatched captured browser observation policy.");
    }

    internal static bool IsObserved(string? policy) => policy is Id or AmazonSponsoredHostPolicy.Id or AmazonProductContextPolicy.Id;
    internal static bool Allows(string value, string policy) => policy switch
    {
        Id => Allows(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.Allows(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.Allows(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static bool IsEntry(string value, string policy) => policy switch
    {
        Id => IsEntry(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.IsEntry(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.IsEntry(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static bool IsSourceEntry(string value, string policy) => policy switch
    {
        Id => IsSourceEntry(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.IsSourceEntry(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.IsSourceEntry(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string Facts(string policy) => policy switch
    {
        Id => PlanningFacts,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.PlanningFacts,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.PlanningFacts,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string ApprovalFor(string policy) => policy switch
    {
        Id => Approval,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.Approval,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.Approval,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string Pattern(string policy) => policy switch
    {
        Id => UrlPattern,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.UrlPattern,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.UrlPattern,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };

    internal static string? Capture(string[] args, string scenario)
    {
        var original = args.Contains("--amazon-observed-redirects", StringComparer.Ordinal);
        var sponsored = args.Contains("--amazon-observed-sponsored-host", StringComparer.Ordinal);
        var context = args.Contains("--amazon-product-context", StringComparer.Ordinal);
        var approval = SchemaPortabilityCampaign.Option(args, "--amazon-scope-approval");
        if (original && sponsored || !sponsored && (approval is not null || context))
            throw new ArgumentException("Select one explicit Amazon observation policy and its matching approval.");
        if (sponsored && approval != AmazonSponsoredHostPolicy.Approval)
            throw new InvalidOperationException("The sponsored-host extension requires its separately authorized scope approval.");
        var policy = context ? AmazonProductContextPolicy.Id : sponsored ? AmazonSponsoredHostPolicy.Id : original ? Id : null;
        Validate(policy, scenario);
        return policy;
    }
}

// Prepared opt-in only. The original v1 policy and approval do not authorize this host.
internal static class AmazonSponsoredHostPolicy
{
    internal const string Id = "amazon-observed-sponsored-host-v2";
    internal const string Approval = "user-strict-amazon-sponsored-host-extension-20261109";
    internal const string UrlPattern = "(?:" + AmazonObservedRedirectPolicy.UrlPattern + @"|https://sponsored-ads\.amazon\.fr/clk/\?[^#]+)";
    internal const string PlanningFacts = "Authorized browser observation policy amazon-observed-sponsored-host-v2: GET only exact literal links observed in the complete successful Amazon.fr search, preserving first product occurrences and sponsored results. The separately approved additional host is sponsored-ads.amazon.fr, only /clk/ with a nonempty query. Original Amazon.fr search, /sspa/click and product routes remain as captured below. Never construct a product URL, decode an advertising r/target parameter into a navigation target, omit sponsored products or substitute later products. Both /sspa/click and sponsored-host entries require exact source-link membership in the same live Browser; another search or browser_close expires that authority. Every actual redirect hop must match the explicit captured full-URL allowlist before contact; retain its observed response and destination evidence. Only HTTPS, default port, no credentials or fragments are permitted. Missing evidence remains failure. The Browser already performs explicit GET acquisition and validates redirects; these are host guarantees, not missing workflow operations, permission to expand the policy, or evidence of successful execution. Observation cursors expire on navigation. Keep the Browser alive until dependent visits finish, then close it even on failure. Captured full-URL allowlist: " + UrlPattern;
    private static readonly Regex Routes = new(@"\A(?:" + UrlPattern + @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static bool Allows(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0 && Routes.IsMatch(value);
    internal static bool IsEntry(string value) => Allows(value) && (AmazonObservedRedirectPolicy.IsEntry(value) ||
        new Uri(value) is { Host: "sponsored-ads.amazon.fr", AbsolutePath: "/clk/" });
    internal static bool IsSourceEntry(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        IsEntry(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
}

// Same granted routes, with stronger source classification from observed DOM attributes.
internal static class AmazonProductContextPolicy
{
    internal const string Id = "amazon-observed-product-context-v3";
    internal const string Approval = AmazonSponsoredHostPolicy.Approval;
    internal const string UrlPattern = AmazonSponsoredHostPolicy.UrlPattern;
    internal const string ProductAttribute = "data-asin";
    internal const string PlanningFacts = AmazonSponsoredHostPolicy.PlanningFacts + "\nThe captured product-context policy amazon-observed-product-context-v3 has the same authorized routes and grant. The isolated Browser host additionally records ancestorAttributes[\"data-asin\"] on observed links: the nearest matching DOM ancestor within the selected root, its exact value/tag/role/snapshot-local index, or explicit null when absent. Empty values remain empty and do not alone establish a non-product: require an observed nav element or navigation role without a product heading/occurrence; otherwise retain the ambiguity as failure. Use this original DOM evidence to distinguish product containers from unrelated brand/navigation advertising; preserve every product occurrence, including sponsored products and repeated identifiers. Source IDs are evidence only, never permission to construct a product URL or decode an advertising target. A selected literal link must come from its actual recorded product context. Retain complete source/navigation evidence; missing or contradictory context remains failure.";
}
