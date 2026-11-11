using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonResolvedStoreVersion = "amazon-business-v8";
    internal const string AmazonResolvedProductVersion = "amazon-business-v9";

    internal const string AmazonReviewProofVersion = "amazon-business-v10";

    private sealed class ResolvedStoreSource
    {
        internal List<string> Findings { get; } = [];
        internal List<ContextProduct> Products { get; set; } = [];
        internal HashSet<JsonObject> StoreReads { get; } = [];
        internal HashSet<JsonObject> SourceReads { get; } = [];
        internal JsonArray ResolutionEvidence { get; } = new();
        internal JsonArray AliasEvidence { get; } = new();
        internal JsonArray StoreListEvidence { get; } = new();
        internal JsonArray UnclassifiedContexts { get; set; } = new();
        internal string? NextResolution { get; set; }
        internal bool Complete { get; set; }
        internal int ResolutionCount { get; set; }
    }

    private static bool SearchAddress(string? value) => AmazonAddress(value, out var uri) && uri.AbsolutePath == "/s";

    private static ResolvedStoreSource ResolveStoreSource(IReadOnlyList<JsonObject> events, string? query, string policy = AmazonResolvedStorePolicy.Id)
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
            !ValidNavigationEvidence(first, policy) || !SearchAddress(source.Url) || SearchQuery(new Uri(source.Url!)) != query ||
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

        if (policy == AmazonSourceCoveragePolicy.Id)
        {
            state.UnclassifiedContexts = UnclassifiedHeadingContexts(source);
            if (state.UnclassifiedContexts.Count != 0)
            { state.Findings.Add("search_heading_context_unaccounted"); return state; }
        }

        var knownReviews = new Dictionary<string, KnownCardReview>(StringComparer.Ordinal);
        var aliases = policy is AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id ? CardReviewAliases(source, state.Findings, out knownReviews) :
            policy == AmazonReviewProofPolicy.Id ? ReviewProofAliases(source) : new Dictionary<string, string[]>(StringComparer.Ordinal);
        var aliasReferences = aliases.Keys.Concat(knownReviews.Keys).ToHashSet(StringComparer.Ordinal);
        var candidates = UnresolvedStoreCandidates(source, state.Findings, policy, aliasReferences);
        if (state.Findings.Count != 0) return state;
        state.ResolutionCount = candidates.Count;
        var references = new HashSet<string>(StringComparer.Ordinal);
        var productReferences = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index]; var href = candidate["href"]!.ToString();
            if (!AmazonObservedRedirectPolicy.IsEntry(href, policy))
            { state.Findings.Add("source_resolution_route_unapproved"); return state; }
            if (navigations.Length <= index + 1)
            { state.NextResolution = href; return state; }
            var read = navigations[index + 1].read;
            if (read["arguments"]!["url"]?.ToString() != href || !complete.TryGetValue(read, out var destination) ||
                !ValidNavigationEvidence(read, policy))
            { state.Findings.Add("source_resolution_provenance_unverified"); return state; }
            // HTTP and document identities must independently agree; a history
            // update cannot disguise a product as a store, or substitute an ASIN.
            var final = read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString();
            var reference = candidate["reference"]!.ToString();
            string kind; string? identity = null;
            if (AmazonResolvedStorePolicy.IsStore(destination.Url ?? "") && AmazonResolvedStorePolicy.IsStore(final ?? ""))
            { references.Add(reference); state.StoreReads.Add(read); kind = "store_navigation"; }
            else if (policy is AmazonResolvedProductPolicy.Id or AmazonReviewProofPolicy.Id or AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id && ObservedProductIdentity(final) is { } product && ObservedProductIdentity(destination.Url) == product)
            { productReferences.Add(reference, product); kind = "product_identity"; identity = product; }
            else { state.Findings.Add("source_resolution_destination_indeterminate"); return state; }
            state.SourceReads.Add(read);
            state.ResolutionEvidence.Add(new JsonObject { ["source_record"] = reference, ["event_index"] = navigations[index + 1].index,
                ["kind"] = kind, ["product_identity"] = identity, ["business_visit"] = false });
        }
        if (policy is AmazonResolvedProductPolicy.Id or AmazonReviewProofPolicy.Id or AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id)
        {
            BindReviewProofAliases(aliases, productReferences, state);
            var identities = policy is AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id ? ResolvedCardProductReferences(source, productReferences, state.Findings) :
                ResolvedProductReferences(source, productReferences, state.Findings);
            var storeLists = policy is AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id ? ResolvedStoreListReferences(source, references, state) : null;
            state.Products = ContextProducts(source, state.Findings, references, identities, policy, aliasReferences, storeLists);
            if (policy is AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id) BindKnownCardReviews(knownReviews, state);
        }
        else state.Products = ContextProducts(source, state.Findings, references);
        if (state.Products.Count == 0) state.Findings.Add("source_products_unverified");
        state.Complete = state.Findings.Count == 0;
        // Retain cursor reads as part of their exact proved navigation, without
        // replacing, filtering or annotating any raw source/event record.
        var inStore = false; var inResolution = false;
        foreach (var (read, _) in reads)
        {
            if (read["arguments"]?["url"] is not null)
            { inStore = state.StoreReads.Contains(read); inResolution = state.SourceReads.Contains(read); }
            else
            { if (inStore) state.StoreReads.Add(read); if (inResolution) state.SourceReads.Add(read); }
        }
        return state;
    }

    private static List<JsonObject> UnresolvedStoreCandidates(Snapshot source, List<string> findings, string policy, IReadOnlySet<string> aliasReferences)
    {
        ValidateObservedProductRoutes(source, findings, policy is AmazonResolvedProductPolicy.Id or AmazonReviewProofPolicy.Id or AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id ? policy : AmazonProductContextPolicy.Id, aliasReferences);
        var id = source.Read["result"]?["observationSnapshot"]?["id"]?.ToString() ??
            source.Read["result"]?["observationManifest"]?["id"]?.ToString() ?? source.Read["result"]?["observation"]?["id"]?.ToString();
        var candidates = new List<JsonObject>();
        for (var index = 0; index < source.Records.Count; index++)
        {
            var record = source.Records[index];
            if (id is null || record?["reference"]?.ToString() != id + ":record:" + index) findings.Add("search_record_provenance_inconsistent");
            if (record is JsonObject link && !aliasReferences.Contains(link["reference"]?.ToString() ?? "") && link["kind"]?.ToString() == "link" &&
                AmazonObservedRedirectPolicy.IsSourceEntry(link["href"]?.ToString() ?? "", policy) && !ProductCode(ContextAttribute(link)?["value"]?.ToString()))
                candidates.Add(link);
        }
        return candidates;
    }

    private static Dictionary<string, string> ResolvedProductReferences(Snapshot source,
        IReadOnlyDictionary<string, string> productReferences, List<string> findings)
    {
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = source.Records.OfType<JsonObject>().Where(r => productReferences.ContainsKey(r["reference"]?.ToString() ?? "")).ToArray();
        foreach (var group in resolved.GroupBy(r => ContextAttribute(r)?["index"]?.ToString(), StringComparer.Ordinal))
        {
            var links = group.ToArray(); var context = ContextAttribute(links[0]);
            var allContextLinks = source.Records.OfType<JsonObject>().Where(r => ContextAttribute(r)?["index"]?.ToString() == group.Key).ToArray();
            if (context?["value"] is not JsonValue value || !value.TryGetValue<string>(out var code) || code.Length != 0 ||
                context["index"] is not JsonValue index || !index.TryGetValue<int>(out var ordinal) || ordinal < 0 ||
                context["tag"]?.ToString() == "nav" || context["role"]?.ToString() == "navigation" ||
                allContextLinks.Length != links.Length || links.Any(r => r["occurrence"] is not null || !JsonNode.DeepEquals(context, ContextAttribute(r))) ||
                links.Count(r => r["heading"]?.ToString() is { Length: > 0 }) != 1 ||
                links.Select(r => productReferences[r["reference"]!.ToString()]).Distinct(StringComparer.Ordinal).Count() != 1)
            { findings.Add("source_product_resolution_context_unverified"); continue; }
            // Original context validation still checks every tag, role, source
            // reference and grouping. Identities remain separate from raw records.
            foreach (var link in links) accepted.Add(link["reference"]!.ToString(), productReferences[link["reference"]!.ToString()]);
        }
        return accepted;
    }

    internal static IEnumerable<string> ResolvedStoreAllowedEntries(IReadOnlyList<JsonObject> reads)
        => ResolvedSourceAllowedEntries(reads, AmazonResolvedStorePolicy.Id);

    internal static IEnumerable<string> ResolvedSourceAllowedEntries(IReadOnlyList<JsonObject> reads, string policy)
    {
        var source = ResolveStoreSource(reads, null, policy);
        if (source.Findings.Count != 0) return [];
        return source.NextResolution is { } next ? [next] : source.Complete ? source.Products.SelectMany(p => p.Urls).Distinct(StringComparer.Ordinal) : [];
    }

    internal static JsonObject VerifyAmazonResolvedStoreSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query));

    internal static JsonObject VerifyAmazonResolvedProductSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonResolvedProductPolicy.Id), AmazonResolvedProductPolicy.Id);

    internal static JsonObject VerifyAmazonReviewProofSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonReviewProofPolicy.Id), AmazonReviewProofPolicy.Id);

    private static JsonObject ResolvedStoreSelection(IReadOnlyList<JsonObject> events, int maximum, string? query, ResolvedStoreSource source, string policy = AmazonResolvedStorePolicy.Id)
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
            if (!ValidNavigationEvidence(read, policy) ||
                ObservedProductIdentity(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()) != product.Identity)
                findings.Add("product_navigation_provenance_incomplete");
        }
        var report = new JsonObject { ["version"] = policy == AmazonSourceCoveragePolicy.Id ? AmazonSourceCoverageVersion : policy == AmazonStoreListPolicy.Id ? AmazonStoreListVersion : policy == AmazonCardReviewPolicy.Id ? AmazonCardReviewVersion : policy == AmazonReviewProofPolicy.Id ? AmazonReviewProofVersion : policy == AmazonResolvedProductPolicy.Id ? AmazonResolvedProductVersion : AmazonResolvedStoreVersion, ["passed"] = findings.Count == 0,
            ["source_prerequisite_qualified"] = source.Complete && !string.IsNullOrWhiteSpace(query), ["source_resolution_count"] = source.ResolutionCount,
            ["expected_product_count"] = expected.Length, ["visited_product_count"] = visits.Length,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
        if (policy is AmazonResolvedProductPolicy.Id or AmazonReviewProofPolicy.Id or AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id)
        {
            report["source_resolution_evidence"] = source.ResolutionEvidence.DeepClone();
            report["source_product_reads"] = source.ResolutionEvidence.Count(e => e?["kind"]?.ToString() == "product_identity");
            report["source_store_reads"] = source.ResolutionEvidence.Count(e => e?["kind"]?.ToString() == "store_navigation");
        }
        if (policy is AmazonReviewProofPolicy.Id or AmazonCardReviewPolicy.Id or AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id) report["source_proof_aliases"] = source.AliasEvidence.DeepClone();
        if (policy is AmazonStoreListPolicy.Id or AmazonSourceCoveragePolicy.Id) report["source_store_list_evidence"] = source.StoreListEvidence.DeepClone();
        if (policy == AmazonSourceCoveragePolicy.Id) report["unclassified_heading_contexts"] = source.UnclassifiedContexts.DeepClone();
        return report;
    }
}
