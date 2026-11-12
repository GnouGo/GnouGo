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
            var records = source.Records.OfType<JsonObject>().ToDictionary(record => record["reference"]!.ToString(), StringComparer.Ordinal);
            foreach (var context in UnclassifiedHeadingContexts(source).OfType<JsonObject>())
            {
                var members = context["source_records"]!.AsArray().Select(reference => records[reference!.ToString()]).ToArray();
                if (!members.Any(member => originals.Contains(member["href"]!.ToString()))) continue;
                var before = findings.Count;
                if (context["asin_context"]?["value"]?.ToString() != "" ||
                    members.Count(member => member["heading"]?.ToString() is { Length: > 0 }) != 1 || !ConsistentCardMetadata(members))
                    findings.Add("aax_original_context_unverified");
                var proposed = new HashSet<string>(StringComparer.Ordinal);
                var aliases = new JsonArray();
                foreach (var member in members)
                {
                    var reference = member["reference"]!.ToString();
                    if (identities.TryGetValue(reference, out var identity)) { proposed.Add(identity); continue; }
                    var href = member["href"]!.ToString();
                    var twin = members.FirstOrDefault(other => other["href"]?.ToString() is { } baseHref &&
                        AmazonAaxSourceDiagnostic.IsEntry(baseHref) && href == baseHref + "#customerReviews" &&
                        identities.ContainsKey(other["reference"]!.ToString()));
                    if (member["heading"] is not null || member["occurrence"] is not null || twin is null)
                    { findings.Add("aax_context_member_unverified"); continue; }
                    proposed.Add(identities[twin["reference"]!.ToString()]);
                    aliases.Add(new JsonObject { ["original_reference"] = reference, ["proved_twin_reference"] = twin["reference"]!.DeepClone(),
                        ["http_receipt_claimed"] = false });
                }
                if (proposed.Count != 1) findings.Add("aax_context_identity_inconsistent");
                contexts.Add(new JsonObject { ["original_context"] = context.DeepClone(), ["original_member_count"] = members.Length,
                    ["own_product_reads"] = members.Count(member => identities.ContainsKey(member["reference"]!.ToString())),
                    ["candidate_identity"] = proposed.Count == 1 ? proposed.Single() : null,
                    ["exact_review_aliases"] = aliases, ["relationship_supported"] = findings.Count == before });
            }
        }
        if (contexts.Count == 0) findings.Add("aax_original_context_missing");
        return new JsonObject { ["version"] = "amazon-aax-product-context-proposal-v1", ["active"] = false,
            ["relationship_evidence_complete"] = findings.Count == 0, ["contexts"] = contexts,
            ["findings"] = new JsonArray(findings.Distinct(StringComparer.Ordinal).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false, ["navigation_authorized"] = false,
            ["model_calls"] = 0, ["navigation_calls"] = 0 };
    }
}
