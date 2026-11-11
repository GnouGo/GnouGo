using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonSourceCoverageVersion = "amazon-business-v13";
    internal static JsonObject VerifyAmazonSourceCoverageSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonSourceCoveragePolicy.Id), AmazonSourceCoveragePolicy.Id);

    // Offline coverage investigation only. It grants no navigation, identity,
    // workflow activation or business credit and leaves all versioned oracles intact.
    internal static JsonObject AnalyzeAmazonUnclassifiedHeadingContexts(IReadOnlyList<JsonObject> events, string query)
    {
        var report = VerifyAmazonStoreListSelection(events, 3, query);
        var source = CompleteSnapshots(events).FirstOrDefault(s => WholeDocumentSearch(s.Read) && SearchAddress(s.Url));
        var contexts = source is null ? new JsonArray() : UnclassifiedHeadingContexts(source);
        return new JsonObject
        {
            ["version"] = "amazon-unclassified-heading-contexts-proposal-v1", ["active"] = false,
            ["v12_source_prerequisite_qualified"] = report["source_prerequisite_qualified"]!.DeepClone(),
            ["unclassified_heading_contexts"] = contexts,
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
            ["model_calls"] = 0, ["navigation_calls"] = 0
        };
    }

    private static JsonArray UnclassifiedHeadingContexts(Snapshot source)
    {
        var contexts = new JsonArray(); var records = source.Records.OfType<JsonObject>().ToArray();
        var examined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Where(r => r["kind"]?.ToString() == "link" &&
                     r["heading"]?.ToString() is { Length: > 0 } && ContextAttribute(r) is not null &&
                     r["href"]?.ToString() is { } href && ObservedProductIdentity(href) is null &&
                     !AmazonStoreListPolicy.IsSourceEntry(href)))
        {
            var reference = record["reference"]!.ToString();
            if (!examined.Add(reference)) continue;
            var card = records.Where(r => SameCard(record, r) && r["kind"]?.ToString() == "link" && r["href"] is not null).ToArray();
            foreach (var member in card) examined.Add(member["reference"]!.ToString());
            contexts.Add(new JsonObject
            {
                ["heading_record"] = reference, ["source_records"] = new JsonArray(card.Select(r => r["reference"]!.DeepClone()).ToArray()),
                ["asin_context"] = ContextAttribute(record)!.DeepClone(), ["occurrence"] = record["occurrence"]?.DeepClone(),
                ["classification"] = "unverified", ["product_identity"] = null, ["navigation_authorized"] = false
            });
        }
        return contexts;
    }
}

// Stronger coverage of the original no-omission obligation, with exactly the
// previous routes/classification rules and grant. No AAX workflow navigation.
internal static class AmazonSourceCoveragePolicy
{
    internal const string Id = "amazon-observed-source-coverage-v9";
    internal const string Approval = AmazonStoreListPolicy.Approval;
    internal const string UrlPattern = AmazonStoreListPolicy.UrlPattern;
    internal static bool Allows(string value) => AmazonStoreListPolicy.Allows(value);
    internal static bool IsEntry(string value) => AmazonStoreListPolicy.IsEntry(value);
    internal static bool IsSourceEntry(string value) => AmazonStoreListPolicy.IsSourceEntry(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-source-coverage-v9 uses the unchanged v8 routes, grant and source-proof classification rules. A heading-bearing original ASIN context with an unclassified href is unresolved source coverage, including an empty identifier; never omit that context or infer non-product navigation from an unsupported route. Source authority remains blocked until the complete original context is accounted for. This stricter check grants no new route, identity or exception. " + AmazonStoreListPolicy.PlanningFacts;
}
