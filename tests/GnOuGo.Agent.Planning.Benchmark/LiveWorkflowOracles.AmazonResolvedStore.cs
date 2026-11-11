using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonResolvedStoreVersion = "amazon-business-v8";

    private sealed class ResolvedStoreSource
    {
        internal List<string> Findings { get; } = [];
        internal List<ContextProduct> Products { get; set; } = [];
        internal HashSet<JsonObject> StoreReads { get; } = [];
        internal string? NextResolution { get; set; }
        internal bool Complete { get; set; }
        internal int ResolutionCount { get; set; }
    }

    private static bool SearchAddress(string? value) => AmazonAddress(value, out var uri) && uri.AbsolutePath == "/s";

    private static ResolvedStoreSource ResolveStoreSource(IReadOnlyList<JsonObject> events, string? query)
    {
        var state = new ResolvedStoreSource();
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        var reads = events.Select((read, index) => (read, index)).Where(p => p.read["tool"]?.ToString() == "browser_get_content").ToArray();
        var navigations = reads.Where(p => p.read["arguments"]?["url"] is not null).ToArray();
        if (navigations.Length == 0) { state.Findings.Add("complete_owned_search_required"); return state; }
        var (first, sourceIndex) = navigations[0];
        var requested = first["arguments"]!["url"]!.ToString();
        if (SearchAddress(requested) && query is null) query = SearchQuery(new Uri(requested));
        if (reads[0].read != first || !WholeDocumentSearch(first) || !SearchAddress(requested) || string.IsNullOrWhiteSpace(query) ||
            SearchQuery(new Uri(requested)) != query || !complete.TryGetValue(first, out var source) ||
            !ValidNavigationEvidence(first, AmazonResolvedStorePolicy.Id) || !SearchAddress(source.Url) || SearchQuery(new Uri(source.Url!)) != query ||
            !SearchAddress(first["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()) ||
            SearchQuery(new Uri(first["result"]!["navigationEvidence"]!["finalUrl"]!.ToString())) != query)
        { state.Findings.Add("complete_owned_search_required"); return state; }

        // A close or another search cannot lend old source authority to later reads.
        if (events.Skip(sourceIndex + 1).Take(reads[^1].index - sourceIndex - 1).Any(e => e["tool"]?.ToString() == "browser_close") ||
            navigations.Skip(1).Any(p => SearchAddress(p.read["arguments"]?["url"]?.ToString()) ||
                SearchAddress(p.read["result"]?["url"]?.ToString()) ||
                p.read["result"]?["navigationEvidence"]?["hops"] is JsonArray hops && hops.Any(h => SearchAddress(h?["url"]?.ToString()))))
        { state.Findings.Add("source_authority_expired"); return state; }

        if (reads.Any(p => p.read["error"]?.GetValue<bool>() != false || p.read["result"]?["success"]?.GetValue<bool>() != true))
        { state.Findings.Add("source_authority_failed_read"); return state; }

        var candidates = UnresolvedStoreCandidates(source, state.Findings);
        if (state.Findings.Count != 0) return state;
        state.ResolutionCount = candidates.Count;
        var references = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index]; var href = candidate["href"]!.ToString();
            if (!AmazonSponsoredHostPolicy.IsEntry(href))
            { state.Findings.Add("source_resolution_route_unapproved"); return state; }
            if (navigations.Length <= index + 1)
            { state.NextResolution = href; return state; }
            var read = navigations[index + 1].read;
            if (read["arguments"]!["url"]?.ToString() != href || !complete.TryGetValue(read, out var destination) ||
                !ValidNavigationEvidence(read, AmazonResolvedStorePolicy.Id))
            { state.Findings.Add("source_resolution_provenance_unverified"); return state; }
            // Both the observed HTTP destination and the current document must be
            // store pages. A history-only address change cannot disguise a product.
            if (!AmazonResolvedStorePolicy.IsStore(destination.Url ?? "") ||
                !AmazonResolvedStorePolicy.IsStore(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString() ?? ""))
            { state.Findings.Add("source_resolution_destination_indeterminate"); return state; }
            references.Add(candidate["reference"]!.ToString()); state.StoreReads.Add(read);
        }
        state.Products = ContextProducts(source, state.Findings, references);
        if (state.Products.Count == 0) state.Findings.Add("source_products_unverified");
        state.Complete = state.Findings.Count == 0;
        // Retain cursor reads as part of their exact proved navigation, without
        // replacing, filtering or annotating any raw source/event record.
        var inStore = false;
        foreach (var (read, _) in reads)
        {
            if (read["arguments"]?["url"] is not null) inStore = state.StoreReads.Contains(read);
            else if (inStore) state.StoreReads.Add(read);
        }
        return state;
    }

    private static List<JsonObject> UnresolvedStoreCandidates(Snapshot source, List<string> findings)
    {
        ValidateObservedProductRoutes(source, findings, AmazonProductContextPolicy.Id);
        var id = source.Read["result"]?["observationSnapshot"]?["id"]?.ToString() ??
            source.Read["result"]?["observationManifest"]?["id"]?.ToString() ?? source.Read["result"]?["observation"]?["id"]?.ToString();
        var candidates = new List<JsonObject>();
        for (var index = 0; index < source.Records.Count; index++)
        {
            var record = source.Records[index];
            if (id is null || record?["reference"]?.ToString() != id + ":record:" + index) findings.Add("search_record_provenance_inconsistent");
            if (record is JsonObject link && link["kind"]?.ToString() == "link" &&
                AmazonSponsoredHostPolicy.IsSourceEntry(link["href"]?.ToString() ?? "") && !ProductCode(ContextAttribute(link)?["value"]?.ToString()))
                candidates.Add(link);
        }
        return candidates;
    }

    internal static IEnumerable<string> ResolvedStoreAllowedEntries(IReadOnlyList<JsonObject> reads)
    {
        var source = ResolveStoreSource(reads, null);
        if (source.Findings.Count != 0) return [];
        return source.NextResolution is { } next ? [next] : source.Complete ? source.Products.SelectMany(p => p.Urls).Distinct(StringComparer.Ordinal) : [];
    }

    internal static JsonObject VerifyAmazonResolvedStoreSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query));

    private static JsonObject ResolvedStoreSelection(IReadOnlyList<JsonObject> events, int maximum, string? query, ResolvedStoreSource source)
    {
        if (maximum is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maximum));
        var findings = source.Findings.ToList();
        if (string.IsNullOrWhiteSpace(query)) findings.Add("search_query_does_not_match_input");
        if (!source.Complete) findings.Add("source_resolutions_incomplete");
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        var visits = events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["arguments"]?["url"] is not null)
            .Skip(1 + source.ResolutionCount).ToArray();
        var expected = source.Products.Take(maximum).ToArray();
        if (visits.Length != expected.Length || expected.Length == 0) findings.Add("first_product_visits_incomplete");
        for (var index = 0; index < Math.Min(visits.Length, expected.Length); index++)
        {
            var read = visits[index]; var product = expected[index];
            if (!product.Urls.Contains(read["arguments"]!["url"]!.ToString(), StringComparer.Ordinal) ||
                !complete.TryGetValue(read, out var snapshot) || ObservedProductIdentity(snapshot.Url) != product.Identity)
                findings.Add("first_product_identity_or_order_mismatch");
            if (!ValidNavigationEvidence(read, AmazonResolvedStorePolicy.Id) ||
                ObservedProductIdentity(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()) != product.Identity)
                findings.Add("product_navigation_provenance_incomplete");
        }
        return new JsonObject { ["version"] = AmazonResolvedStoreVersion, ["passed"] = findings.Count == 0,
            ["source_prerequisite_qualified"] = source.Complete && !string.IsNullOrWhiteSpace(query), ["source_resolution_count"] = source.ResolutionCount,
            ["expected_product_count"] = expected.Length, ["visited_product_count"] = visits.Length,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }
}
