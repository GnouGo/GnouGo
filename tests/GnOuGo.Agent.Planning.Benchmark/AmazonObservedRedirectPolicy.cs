using System.Text.RegularExpressions;

// Explicit isolated-test authority; no scenario rules belong in Browser itself.
internal static class AmazonObservedRedirectPolicy
{
    internal const string Id = "amazon-observed-redirects-v1";
    internal const string Approval = "user-strict-amazon-extension-20261108";
    internal const string AcquisitionGuarantees = "The existing browser_get_content operation performs explicit GET acquisition; the configured Browser host validates every redirect destination before contacting it and returns actual navigationEvidence.";
    internal const string GuaranteeBoundary = "These are existing host guarantees, not missing workflow operations or permission to change the policy. They establish no observed search, visit, product data or execution success.";
    internal const string PlanningFacts = "Authorized browser observation policy amazon-observed-redirects-v1: GET only literal links observed in the complete Amazon.fr search, preserving the first product occurrences and sponsored results. Never construct a product URL from an identifier or extract a replacement target from advertising query parameters. An observed /sspa/click link may be followed only while its source search remains the current live browser observation; browser_close or another search expires that authority. Keep the browser alive until dependent visits finish, then perform the required cleanup even on failure. Every redirect hop must satisfy the explicit Amazon.fr route policy; retain the actual redirect and destination evidence. Missing evidence remains a failure, never an invented link or successful visit."
        + "\nCaptured isolated-host navigation allowlist, matched against the entire absolute URL: " + UrlPattern
        + ". Only HTTPS on www.amazon.fr or amazon.fr, default port, without credentials or fragments is authorized. This allowlist grants no authority to construct a product URL."
        + " " + AcquisitionGuarantees
        + " During approved delegated execution, the host rejects an /sspa/click entry unless that exact literal link was observed in the complete source search. Dependent product reads retain that source authority in the same live browser; browser_close or a new search clears it. Observation cursors still expire on navigation."
        + " " + GuaranteeBoundary;
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

    internal static bool IsObserved(string? policy) => policy is Id or AmazonSponsoredHostPolicy.Id or AmazonProductContextPolicy.Id or AmazonResolvedStorePolicy.Id or AmazonResolvedProductPolicy.Id or AmazonReviewProofPolicy.Id;
    internal static bool Allows(string value, string policy) => policy switch
    {
        Id => Allows(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.Allows(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.Allows(value),
        AmazonResolvedStorePolicy.Id => AmazonResolvedStorePolicy.Allows(value),
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.Allows(value),
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.Allows(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static bool IsEntry(string value, string policy) => policy switch
    {
        Id => IsEntry(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.IsEntry(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.IsEntry(value),
        AmazonResolvedStorePolicy.Id => AmazonSponsoredHostPolicy.IsEntry(value),
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.IsEntry(value),
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.IsEntry(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static bool IsSourceEntry(string value, string policy) => policy switch
    {
        Id => IsSourceEntry(value),
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.IsSourceEntry(value),
        AmazonProductContextPolicy.Id => AmazonSponsoredHostPolicy.IsSourceEntry(value),
        AmazonResolvedStorePolicy.Id => AmazonSponsoredHostPolicy.IsSourceEntry(value),
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.IsSourceEntry(value),
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.IsSourceEntry(value),
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string Facts(string policy) => policy switch
    {
        Id => PlanningFacts,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.PlanningFacts,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.PlanningFacts,
        AmazonResolvedStorePolicy.Id => AmazonResolvedStorePolicy.PlanningFacts,
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.PlanningFacts,
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.PlanningFacts,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string ApprovalFor(string policy) => policy switch
    {
        Id => Approval,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.Approval,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.Approval,
        AmazonResolvedStorePolicy.Id => AmazonResolvedStorePolicy.Approval,
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.Approval,
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.Approval,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };
    internal static string Pattern(string policy) => policy switch
    {
        Id => UrlPattern,
        AmazonSponsoredHostPolicy.Id => AmazonSponsoredHostPolicy.UrlPattern,
        AmazonProductContextPolicy.Id => AmazonProductContextPolicy.UrlPattern,
        AmazonResolvedStorePolicy.Id => AmazonResolvedStorePolicy.UrlPattern,
        AmazonResolvedProductPolicy.Id => AmazonResolvedProductPolicy.UrlPattern,
        AmazonReviewProofPolicy.Id => AmazonReviewProofPolicy.UrlPattern,
        _ => throw new InvalidOperationException("Unknown captured browser observation policy.")
    };

    internal static string? Capture(string[] args, string scenario)
    {
        var original = args.Contains("--amazon-observed-redirects", StringComparer.Ordinal);
        var sponsored = args.Contains("--amazon-observed-sponsored-host", StringComparer.Ordinal);
        var context = args.Contains("--amazon-product-context", StringComparer.Ordinal);
        var approval = SchemaPortabilityCampaign.Option(args, "--amazon-scope-approval");
        if (args.Contains("--amazon-review-proof-context", StringComparer.Ordinal))
        {
            if (original || sponsored || context || args.Contains("--amazon-resolved-store-context", StringComparer.Ordinal) ||
                args.Contains("--amazon-resolved-product-context", StringComparer.Ordinal))
                throw new ArgumentException("Select the separate review-proof policy alone.");
            if (approval != AmazonReviewProofPolicy.Approval)
                throw new InvalidOperationException("The review-proof amendment requires its exact separate grant.");
            Validate(AmazonReviewProofPolicy.Id, scenario);
            return AmazonReviewProofPolicy.Id;
        }
        if (args.Contains("--amazon-resolved-product-context", StringComparer.Ordinal))
        {
            if (original || sponsored || context || args.Contains("--amazon-resolved-store-context", StringComparer.Ordinal))
                throw new ArgumentException("Select the separate resolved-product policy alone.");
            if (approval != AmazonResolvedProductPolicy.Approval)
                throw new InvalidOperationException("The product-resolution workflow amendment requires its exact separate grant.");
            Validate(AmazonResolvedProductPolicy.Id, scenario);
            return AmazonResolvedProductPolicy.Id;
        }
        if (args.Contains("--amazon-resolved-store-context", StringComparer.Ordinal))
        {
            if (original || sponsored || context) throw new ArgumentException("Select the separate resolved-store policy alone.");
            if (approval != AmazonResolvedStorePolicy.Approval) throw new InvalidOperationException("The workflow store-context amendment requires its exact separate grant.");
            Validate(AmazonResolvedStorePolicy.Id, scenario);
            return AmazonResolvedStorePolicy.Id;
        }
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

// Separately authorized workflow amendment; old policies and grants remain unchanged.
internal static class AmazonResolvedStorePolicy
{
    internal const string Id = "amazon-observed-resolved-store-context-v4";
    internal const string Approval = "user-amazon-workflow-store-context-20261111";
    internal const string StorePattern = @"https://(?:www\.)?amazon\.fr/stores/page/[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}(?:\?[^#\s]+)?";
    internal const string UrlPattern = "(?:" + AmazonSponsoredHostPolicy.UrlPattern + "|" + StorePattern + ")";
    private static readonly Regex Stores = new(@"\A(?:" + StorePattern + @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    internal static bool IsStore(string value) => !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && Stores.IsMatch(value);
    internal static bool Allows(string value) => AmazonSponsoredHostPolicy.Allows(value) || IsStore(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-resolved-store-context-v4: acquire a complete whole-document Amazon.fr search including nearest data-asin context. Before selecting products, resolve each sponsored source-link occurrence with unverified source identity by GET of its exact literal href in source order. Retain all complete redirect and destination observations, including repeated links. The host permits the next exact resolution only; it then permits source product links after every context is verified. Only an observed approved store-page destination with a consistent empty-ASIN source context and no product heading/semantic occurrence/direct product URL establishes non-product navigation. Product or ambiguous destinations remain blockers. Do not decode advertising targets, construct URLs, omit sponsored products, merge repeated product occurrences or change order. Visit the first requested products, extract their observed fields, write Excel, and close the browser even on failure. Close or another search expires source authority. Observation cursors expire on navigation. Every GET redirect is checked before contact; the only added route is /stores/page/{UUID} on the existing Amazon.fr hosts, for redirects only. No model output grants permission. Captured full-URL allowlist: " + UrlPattern;
}


// Explicitly approved product-identity resolution phase, separate from v4/v8.
internal static class AmazonResolvedProductPolicy
{
    internal const string Id = "amazon-observed-resolved-product-context-v5";
    internal const string Approval = "user-amazon-workflow-product-context-20261111";
    internal const string SlashlessPattern = @"https://sponsored-ads\.amazon\.fr/clk\?[^#\s]+";
    internal const string UrlPattern = "(?:" + AmazonResolvedStorePolicy.UrlPattern + "|" + SlashlessPattern + ")";
    private static readonly Regex Slashless = new(@"\A(?:" + SlashlessPattern + @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    internal static bool IsSlashless(string value) => !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && Slashless.IsMatch(value);
    internal static bool Allows(string value) => AmazonResolvedStorePolicy.Allows(value) || IsSlashless(value);
    internal static bool IsEntry(string value) => AmazonSponsoredHostPolicy.IsEntry(value) || IsSlashless(value);
    internal static bool IsSourceEntry(string value) => AmazonSponsoredHostPolicy.IsSourceEntry(value) || IsSlashless(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-resolved-product-context-v5: acquire a complete whole-document Amazon.fr search with nearest data-asin context. Before business product selection, resolve every sponsored source-link occurrence with unverified identity by GET of its exact literal href in source order, including repeated hrefs. This separately authorized source-resolution phase may reach products. Keep and report those early reads; they never substitute for later required business visits. An empty-ASIN product context requires every link's complete own redirect/destination proof, the same observed product identity, consistent original context, exactly one heading-bearing link and no conflicting or unresolved context link/occurrence. Different contexts remain separate products even when identities match. Store navigation retains the strict no-product-heading/occurrence rule. The host grants the next exact resolution only, then original source product links after all contexts qualify. Visit every first requested product in original source order, extract actual fields, write Excel and close the browser even on failure. Never decode advertising targets, construct or normalize URLs, omit sponsored products or deduplicate product occurrences. Close, failure or another search expires source authority; observation cursors expire on navigation. Every redirect is checked before contact. Only literal /clk with a nonempty query is newly added on the already approved sponsored host; store-page routes remain redirect-only. No model output grants permission. Captured full-URL allowlist: " + UrlPattern;
}

// Verifier-only amendment: navigation remains byte-for-byte the v5 allowlist.
internal static class AmazonReviewProofPolicy
{
    internal const string Id = "amazon-observed-review-proof-context-v6";
    internal const string Approval = "user-amazon-workflow-review-proof-20261111";
    internal const string UrlPattern = AmazonResolvedProductPolicy.UrlPattern;
    internal static bool Allows(string value) => AmazonResolvedProductPolicy.Allows(value);
    internal static bool IsEntry(string value) => AmazonResolvedProductPolicy.IsEntry(value);
    internal static bool IsSourceEntry(string value) => AmazonResolvedProductPolicy.IsSourceEntry(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-review-proof-context-v6: use the approved v5 two-phase source resolution and later ordered business visits. Resolve every navigable source-link occurrence by its own exact GET, including equal hrefs. Only a non-heading, non-occurrence link in the same verified empty-ASIN context whose original href exactly equals a separately observed permitted href plus #customerReviews may reference that link's completed matching product proof. Never navigate, strip, normalize or manufacture a URL from the alias. Keep every raw record and every product occurrence; references are not extra GETs, receipts or business visits. Resolved product contexts must have consistent identity, exactly one heading and no unresolved sibling. Store navigation requires independent store-page proof and no product heading or semantic occurrence; it cannot supply product identity. The host grants only the next exact source read, then original product links after qualification. Visit every first requested product in source order, extract fields from those later business visits, write Excel and close even on failure. Close, new search or failed evidence expires authority. Every redirect is checked before contact. Hosts and full-URL permissions are unchanged from v5; no fragment navigation is allowed. "
        + AmazonObservedRedirectPolicy.AcquisitionGuarantees + " " + AmazonObservedRedirectPolicy.GuaranteeBoundary
        + " Captured full-URL allowlist: " + UrlPattern;
}
