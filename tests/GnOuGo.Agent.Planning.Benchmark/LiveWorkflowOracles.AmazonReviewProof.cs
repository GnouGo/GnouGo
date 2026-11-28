using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    // Compare original strings only. The returned values are record references,
    // never constructed navigation targets or independent HTTP receipts.
    private static Dictionary<string, string[]> ReviewProofAliases(Snapshot source)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var records = source.Records.OfType<JsonObject>().ToArray();
        foreach (var record in records)
        {
            var context = ContextAttribute(record);
            if (record["kind"]?.ToString() != "link" || record["heading"] is not null || record["occurrence"] is not null ||
                record["href"]?.ToString() is not { } href || !href.EndsWith("#customerReviews", StringComparison.Ordinal) ||
                record["reference"]?.ToString() is not { } reference ||
                context?["value"] is not JsonValue value || !value.TryGetValue<string>(out var code) || code.Length != 0 ||
                context["index"] is not JsonValue index || !index.TryGetValue<int>(out var ordinal) || ordinal < 0) continue;
            var targets = records.Where(other => other["kind"]?.ToString() == "link" &&
                other["href"]?.ToString() is { } original && AmazonResolvedProductPolicy.IsEntry(original) &&
                href == original + "#customerReviews" && JsonNode.DeepEquals(context, ContextAttribute(other)))
                .Select(other => other["reference"]?.ToString()).OfType<string>().ToArray();
            if (targets.Length != 0) aliases[reference] = targets;
        }
        return aliases;
    }

    private static void BindReviewProofAliases(IReadOnlyDictionary<string, string[]> aliases,
        Dictionary<string, string> productReferences, ResolvedStoreSource state)
    {
        foreach (var (alias, targets) in aliases)
        {
            if (targets.Any(t => !productReferences.ContainsKey(t)) ||
                targets.Select(t => productReferences[t]).Distinct(StringComparer.Ordinal).Count() != 1)
            { state.Findings.Add("source_review_alias_product_proof_unverified"); continue; }
            productReferences.Add(alias, productReferences[targets[0]]);
            state.AliasEvidence.Add(new JsonObject
            {
                ["source_record"] = alias,
                ["proof_source_records"] = new JsonArray(targets.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
                ["proof_event_indices"] = new JsonArray(state.ResolutionEvidence.Where(e => targets.Contains(e!["source_record"]!.ToString(), StringComparer.Ordinal))
                    .Select(e => e!["event_index"]!.DeepClone()).ToArray()),
                ["product_identity"] = productReferences[targets[0]],
                ["http_call_created"] = false, ["business_visit"] = false
            });
        }
    }
}
