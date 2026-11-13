using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonStoreListVersion = "amazon-business-v12";
    internal static JsonObject VerifyAmazonStoreListSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonStoreListPolicy.Id), AmazonStoreListPolicy.Id);

    private static HashSet<string> ResolvedStoreListReferences(Snapshot source, IReadOnlySet<string> storeReferences, ResolvedStoreSource state)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal); var examined = new HashSet<string>(StringComparer.Ordinal);
        var records = source.Records.OfType<JsonObject>().ToArray();
        foreach (var record in records.Where(r => r["occurrence"] is not null && storeReferences.Contains(r["reference"]!.ToString())))
        {
            if (!examined.Add(record["reference"]!.ToString())) continue;
            var card = records.Where(r => SameCard(record, r)).ToArray();
            var links = card.Where(r => r["href"] is not null).ToArray();
            foreach (var link in links) examined.Add(link["reference"]!.ToString());
            if (ContextAttribute(record)?["value"]?.ToString() != "" ||
                record["occurrence"]?["tag"]?.ToString() != "li" || record["occurrence"]?["role"]?.ToString() != "listitem" ||
                card.Any(r => r["heading"] is not null || ObservedProductIdentity(r["href"]?.ToString()) is not null) ||
                links.Length == 0 || links.Any(r => !AuxiliaryCardAnchor(r) || !storeReferences.Contains(r["reference"]!.ToString())))
            { state.Findings.Add("source_store_list_context_unverified"); continue; }
            foreach (var link in links)
            {
                var reference = link["reference"]!.ToString(); accepted.Add(reference);
                var proof = state.ResolutionEvidence.OfType<JsonObject>().Single(e => e["source_record"]?.ToString() == reference && e["kind"]?.ToString() == "store_navigation");
                state.StoreListEvidence.Add(new JsonObject { ["source_record"] = reference, ["proof_kind"] = "inherited_store_list_navigation",
                    ["event_index"] = proof["event_index"]!.DeepClone(), ["product_identity"] = null, ["http_call_created"] = false, ["business_visit"] = false });
            }
        }
        return accepted;
    }
}

internal static class AmazonStoreListPolicy
{
    internal const string Id = "amazon-observed-store-list-context-v8";
    internal const string Approval = "user-amazon-inherited-store-list-20261111";
    internal const string UrlPattern = AmazonCardReviewPolicy.UrlPattern;
    internal static bool Allows(string value) => AmazonCardReviewPolicy.Allows(value);
    internal static bool IsEntry(string value) => AmazonCardReviewPolicy.IsEntry(value);
    internal static bool IsSourceEntry(string value) => AmazonCardReviewPolicy.IsSourceEntry(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-store-list-context-v8: use the approved two-phase source resolution and later ordered business visits. " +
        AmazonCardReviewPolicy.CardReviewFacts + " The separately approved store-list exception accounts an original empty-ASIN inherited li with role=listitem as non-product navigation only when every original href-bearing link in that exact consistent card is an auxiliary anchor with its own complete ordered store redirect proof. Each anchor's own selector must differ from its inherited container; no heading, direct product identity, self-occurrence, mixed store/product card or unresolved sibling is allowed. Preserve raw containers and every repeated occurrence. Report this store membership separately; it establishes no product identity, later business visit, extra receipt or navigation target. Other semantic store containers remain unverified. " +
        AmazonObservedRedirectPolicy.AcquisitionGuarantees + " " + AmazonObservedRedirectPolicy.GuaranteeBoundary + " Captured full-URL allowlist: " + UrlPattern;
}
