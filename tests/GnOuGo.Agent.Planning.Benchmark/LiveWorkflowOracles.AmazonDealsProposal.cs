using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    // Offline relationship evidence only. This is deliberately absent from policy
    // capture, Browser configuration, planning facts and business-oracle dispatch.
    internal static JsonObject AnalyzeAmazonDealsContextProposal(IReadOnlyList<JsonObject> events, string query)
    {
        const string deals = "https://www.amazon.fr/deals";
        var findings = new List<string>();
        var contexts = new JsonArray();
        Snapshot? source = null;
        if (events.FirstOrDefault() is { } first)
        {
            try
            {
                _ = AaxDiagnosticSourceEntries(first, query);
                source = CompleteSnapshots([first]).Single();
            }
            catch (InvalidOperationException) { findings.Add("complete_owned_search_required"); }
        }
        else findings.Add("complete_owned_search_required");
        var records = source?.Records.OfType<JsonObject>().ToArray() ?? [];
        var candidates = records.Where(r => r["kind"]?.ToString() == "link" && r["href"]?.ToString() is { } href &&
            (href == deals || AmazonAaxProductPolicy.IsEntry(href) && !ProductCode(ContextAttribute(r)?["value"]?.ToString()))).ToArray();
        var reads = events.Skip(1).ToArray();
        if (reads.LastOrDefault()?["tool"]?.ToString() == "browser_close") reads = reads[..^1];
        if (reads.Length != candidates.Length) findings.Add("own_resolutions_incomplete");
        var snapshots = new HashSet<string>(StringComparer.Ordinal);
        if (source is not null && CaptureId(source.Read) is { } sourceId) snapshots.Add(sourceId);
        var originals = records.Select(r => r["href"]?.ToString()).Where(h => h is not null && AmazonAaxProductPolicy.IsAaxEntry(h))
            .Select(h => h!).ToHashSet(StringComparer.Ordinal);
        bool Allows(string value) => AmazonSourceCoveragePolicy.Allows(value) || originals.Contains(value) ||
            candidates.Any(r => r["href"]?.ToString() == deals) && value == deals;
        var products = new Dictionary<string, string>(StringComparer.Ordinal);
        var offers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Length && index < reads.Length; index++)
        {
            var candidate = candidates[index]; var read = reads[index];
            var href = candidate["href"]!.ToString(); var reference = candidate["reference"]!.ToString();
            if (read["tool"]?.ToString() != "browser_get_content" || read["arguments"]?["url"]?.ToString() != href ||
                read["diagnosticSourceReference"] is { } supplied && supplied.ToString() != reference ||
                !AaxDiagnosticReadComplete(read, Allows) || CaptureId(read) is not { } capture || !snapshots.Add(capture))
            { findings.Add("own_resolution_unverified"); continue; }
            var navigation = read["result"]!["navigationEvidence"]!;
            var hops = navigation["hops"]!.AsArray();
            // Match the owning Browser's literal before-contact check as well as
            // the established completed navigation evidence check.
            if (hops.Any(h => h?["location"]?.ToString() is { } location &&
                (!Uri.TryCreate(new Uri(h["url"]!.ToString()), location, out var next) || !Allows(next.OriginalString))))
            { findings.Add("literal_redirect_unverified"); continue; }
            if (href == deals)
            {
                if (navigation["finalUrl"]?.ToString() != deals || read["result"]?["url"]?.ToString() != deals ||
                    hops.Count != 1 || hops[0]?["url"]?.ToString() != deals || hops[0]?["statusCode"]?.GetValue<int>() != 200)
                    findings.Add("exact_deals_destination_unverified");
                else offers.Add(reference);
            }
            else if (ObservedProductIdentity(navigation["finalUrl"]?.ToString()) is { } identity &&
                identity == ObservedProductIdentity(read["result"]?["url"]?.ToString())) products.Add(reference, identity);
            else if (!ProposedStoreDestination(navigation["finalUrl"]?.ToString() ?? "")) findings.Add("source_destination_unverified");
        }
        var examined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in records.Where(r => r["kind"]?.ToString() == "link" && r["heading"]?.ToString() is { Length: > 0 } &&
            ContextAttribute(r)?["value"]?.ToString() == "" && r["href"]?.ToString() is { } href && AmazonAaxProductPolicy.IsEntry(href)))
        {
            if (!examined.Add(heading["reference"]!.ToString())) continue;
            var members = records.Where(r => r["kind"]?.ToString() == "link" && r["href"] is not null && SameCard(heading, r)).ToArray();
            foreach (var member in members) examined.Add(member["reference"]!.ToString());
            if (!members.Any(r => r["href"]?.ToString() == deals)) continue;
            var context = new JsonObject { ["heading_record"] = heading["reference"]!.DeepClone(),
                ["source_records"] = new JsonArray(members.Select(r => r["reference"]!.DeepClone()).ToArray()),
                ["asin_context"] = ContextAttribute(heading)!.DeepClone(), ["occurrence"] = heading["occurrence"]?.DeepClone() };
            var before = findings.Count;
            if (context["asin_context"]?["value"]?.ToString() != "" || context["asin_context"]?["tag"]?.ToString() != "div" ||
                context["occurrence"] is not null || members.Count(r => r["heading"]?.ToString() is { Length: > 0 }) != 1 ||
                !ConsistentCardMetadata(members)) findings.Add("deals_original_context_unverified");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var aliases = new JsonArray();
            foreach (var member in members)
            {
                var reference = member["reference"]!.ToString(); var href = member["href"]!.ToString();
                if (products.TryGetValue(reference, out var identity)) { identities.Add(identity); continue; }
                if (href == deals && AuxiliaryCardAnchor(member) && member["occurrence"] is null && offers.Contains(reference)) continue;
                var twin = members.FirstOrDefault(r => r["href"]?.ToString() is { } original && AmazonAaxProductPolicy.IsEntry(original) &&
                    href == original + "#customerReviews" && products.ContainsKey(r["reference"]!.ToString()));
                if (!AuxiliaryCardAnchor(member) || member["occurrence"] is not null || twin is null)
                { findings.Add("deals_context_member_unverified"); continue; }
                identities.Add(products[twin["reference"]!.ToString()]);
                aliases.Add(new JsonObject { ["original_reference"] = reference, ["proved_twin_reference"] = twin["reference"]!.DeepClone(),
                    ["http_receipt_claimed"] = false });
            }
            if (identities.Count != 1) findings.Add("deals_context_identity_inconsistent");
            contexts.Add(new JsonObject { ["original_context"] = context.DeepClone(), ["original_member_count"] = members.Length,
                ["own_product_reads"] = members.Count(m => products.ContainsKey(m["reference"]!.ToString())),
                ["own_auxiliary_deals_reads"] = members.Count(m => offers.Contains(m["reference"]!.ToString())),
                ["candidate_identity"] = identities.Count == 1 ? identities.Single() : null, ["exact_review_aliases"] = aliases,
                ["relationship_supported"] = findings.Count == before });
        }
        if (contexts.Count == 0) findings.Add("deals_original_product_context_missing");
        return new JsonObject { ["version"] = "amazon-deals-context-v11-v15-proposal-v1", ["active"] = false,
            ["relationship_evidence_complete"] = findings.Count == 0, ["contexts"] = contexts,
            ["ordered_own_read_count"] = candidates.Length, ["own_deals_read_count"] = offers.Count,
            ["findings"] = new JsonArray(findings.Distinct(StringComparer.Ordinal).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false, ["navigation_authorized"] = false,
            ["model_calls"] = 0, ["navigation_calls"] = 0 };
    }
}
