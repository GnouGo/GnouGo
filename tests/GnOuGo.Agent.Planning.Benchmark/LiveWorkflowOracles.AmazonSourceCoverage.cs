using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    // Offline coverage investigation only. It grants no navigation, identity,
    // workflow activation or business credit and leaves all versioned oracles intact.
    internal static JsonObject AnalyzeAmazonUnclassifiedHeadingContexts(IReadOnlyList<JsonObject> events, string query)
    {
        var report = VerifyAmazonStoreListSelection(events, 3, query);
        var contexts = new JsonArray();
        var source = CompleteSnapshots(events).FirstOrDefault(s => WholeDocumentSearch(s.Read) && SearchAddress(s.Url));
        if (source is not null)
        {
            var records = source.Records.OfType<JsonObject>().ToArray();
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
                    ["heading_record"] = reference,
                    ["source_records"] = new JsonArray(card.Select(r => r["reference"]!.DeepClone()).ToArray()),
                    ["asin_context"] = ContextAttribute(record)!.DeepClone(),
                    ["occurrence"] = record["occurrence"]?.DeepClone(),
                    ["classification"] = "unverified", ["product_identity"] = null,
                    ["navigation_authorized"] = false
                });
            }
        }
        return new JsonObject
        {
            ["version"] = "amazon-unclassified-heading-contexts-proposal-v1", ["active"] = false,
            ["v12_source_prerequisite_qualified"] = report["source_prerequisite_qualified"]!.DeepClone(),
            ["unclassified_heading_contexts"] = contexts,
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
            ["model_calls"] = 0, ["navigation_calls"] = 0
        };
    }
}
