using System.Text.Json.Nodes;

internal static class AmazonDealsPolicy
{
    internal const string Id = "amazon-observed-deals-context-v11";
    internal const string Approval = "user-amazon-deals-workflow-20261125";
    internal const string Address = "https://www.amazon.fr/deals";
    internal const string ObservedEntryPattern = "(?:" + AmazonAaxProductPolicy.EntryPattern + @"|https://www\.amazon\.fr/deals)";
    internal static bool Allows(string href) => href == Address || AmazonAaxProductPolicy.Allows(href);
    internal static bool IsEntry(string href) => href == Address || AmazonAaxProductPolicy.IsEntry(href);
    internal static bool IsSourceEntry(string href) => href == Address || AmazonAaxProductPolicy.IsSourceEntry(href);
    internal const string PlanningFacts = "Authorized browser policy " + Id + " extends the observed AAX policy. " +
        "Read every literal https://www.amazon.fr/deals link from the owned search once per occurrence, interleaved in original order with the other required source reads. " +
        "It can prove an auxiliary listing only with its own complete single GET 200 to that unchanged address. " +
        "Within an unresolved product card it must be a non-heading anchor in the same empty-ASIN div without a semantic occurrence. " +
        "Require one heading, consistent metadata and product identity, and individual product proofs or exact same-card review twins for the other links. " +
        "Keep all records and later first-ten business visits. " + AmazonAaxProductPolicy.SourceFacts;
}

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonDealsVersion = "amazon-business-v15";
    internal static JsonObject VerifyAmazonDealsSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonDealsPolicy.Id), AmazonDealsPolicy.Id);

    private static bool ExactListingRead(JsonObject read, string? documentUrl)
        => documentUrl == AmazonDealsPolicy.Address && read["result"]?["url"]?.ToString() == AmazonDealsPolicy.Address &&
            read["result"]?["acquisition"]?["navigation"]?["url"]?.ToString() == AmazonDealsPolicy.Address &&
            read["result"]?["navigationEvidence"]?["hops"] is JsonArray { Count: 1 } hops &&
            hops[0]?["statusCode"]?.GetValue<int>() == 200;
}
