using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    // Offline evidence analysis only. No workflow policy, entry grant or business
    // verifier dispatch registers this proposed relationship.
    internal static JsonObject AnalyzeAmazonAaxProductContextProposal(IReadOnlyList<JsonObject> events, string query)
    {
        var findings = new List<string>();
        var contexts = new JsonArray();
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var original = events.FirstOrDefault();
        List<JsonObject> entries;
        try { entries = original is null ? [] : AaxDiagnosticSourceEntries(original, query); }
        catch (InvalidOperationException) { entries = []; findings.Add("complete_owned_search_required"); }
        if (entries.Count == 0) findings.Add("aax_original_entries_missing");
        var originals = entries.Select(entry => entry["href"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var snapshots = new HashSet<string>(StringComparer.Ordinal);
        if (original?["result"]?["observationSnapshot"]?["id"] is { } sourceId) snapshots.Add(sourceId.ToString());
        if (events.Count != entries.Count + 1) findings.Add("aax_own_resolutions_incomplete");
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (index + 1 >= events.Count) continue;
            var read = events[index + 1];
            if (read["tool"]?.ToString() != "browser_get_content" || read["arguments"]?["url"]?.ToString() != entry["href"]?.ToString() ||
                !AaxDiagnosticReadComplete(read, value => AmazonAaxSourceDiagnostic.IsDestination(value) || originals.Contains(value)) ||
                read["result"]?["observationSnapshot"]?["id"]?.ToString() is not { } capture || !snapshots.Add(capture))
            { findings.Add("aax_own_resolution_unverified"); continue; }
            var identity = ObservedProductIdentity(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString());
            if (identity is null || identity != ObservedProductIdentity(read["result"]?["url"]?.ToString()))
            { findings.Add("aax_product_destination_unverified"); continue; }
            identities[entry["reference"]!.ToString()] = identity;
        }
        if (entries.Count > 0)
        {
            var source = CompleteSnapshots([original!]).Single();
            contexts = AaxProductContextEvidence(source, identities, originals, findings);
        }
        if (contexts.Count == 0) findings.Add("aax_original_context_missing");
        return new JsonObject { ["version"] = "amazon-aax-product-context-proposal-v1", ["active"] = false,
            ["relationship_evidence_complete"] = findings.Count == 0, ["contexts"] = contexts,
            ["findings"] = new JsonArray(findings.Distinct(StringComparer.Ordinal).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false, ["navigation_authorized"] = false,
            ["model_calls"] = 0, ["navigation_calls"] = 0 };
    }
}
