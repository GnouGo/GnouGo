using System.Text.Json.Nodes;

internal static class AmazonFirstProductsPolicy
{
    internal const string Id = "amazon-observed-first-products-v12";
    internal const string Approval = "user-amazon-first-products-20261125";
    internal const string SeasonalListing = "https://www.amazon.fr/blackfriday";
    internal const string UrlPattern = "(?:" + AmazonAaxProductPolicy.UrlPattern + @"|https://www\.amazon\.fr/blackfriday)";
    internal static bool Allows(string href) => AmazonDealsPolicy.Allows(href) || href == SeasonalListing;
    internal static bool IsEntry(string href) => AmazonDealsPolicy.IsEntry(href) || href == SeasonalListing;
    internal static bool IsSourceEntry(string href) => AmazonDealsPolicy.IsSourceEntry(href);
    internal const string PlanningFacts = "Use the input query at amazon.fr/s and capture the full rendered source with browser_get_content, format=observation_complete, omitting selector. " +
        "The configured Browser already captures data-asin in observationSnapshot.pages[].records[].ancestorAttributes. Use that structured source directly. A URL-less read needs a current issued cursor and observation or observation_pages format. " +
        "On the initial search set prefixAttribute=data-asin and prefixGroups=10. Bind observationSnapshot.linkPrefix alone to the ordered selection stage. Browser constructs this view across all pages through ten distinct populated groups and retains earlier empty groups. " +
        "Its ancestors table and links in original order expose group identity, heading membership, descriptive routes and literal hrefs for empty groups and primary links. Only a literal href can authorize a GET. Page/offset reconnects any link to its exact original record. " +
        "One bounded adaptive selection can scan that prefix sequentially, resolving only necessary empty groups with their observed hrefs and stopping as soon as ten products are established. Return ordered source positions for separate business visits. A populated attribute still requires product evidence. " +
        "Keep the complete observations in workflow data. If the prefix falls short use further original groups in order or report the observed shortfall. Project compact detail-field candidates before inference. Passing the whole Browser result, rendered HTML, or full snapshot to a runner can exceed its context before its first call. " +
        "Keep the original ordered cards, data-asin and semantic occurrence; count sponsored products and repeated occurrences. Stop selection after the first ten products. " +
        "A known card can identify its non-heading review and offer anchors from card membership; retain these records without visiting them. Choose the observed product heading, or a same-card product link when the card has no heading. " +
        "Resolve earlier empty-ASIN sponsored entries in original order using their own observed GETs; a verified store or listing is not a product. A necessary observed /deals read may redirect to the exact /blackfriday listing. " +
        "Do not invent URLs, skip ambiguous earlier products, or count a source proof as a business visit. Visit each selected product separately in original order and extract Nom, Description and Prix from that detail page. " +
        "Distinguish an observed product with an unavailable field from an HTTP 200 challenge or error page. Carry any failed visit and its observed reason through the workbook decision and returned result. " +
        "Write the actual XLSX, return it and close Browser on completion or failure. " + AmazonObservedRedirectPolicy.AcquisitionGuarantees;
}

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonFirstProductsVersion = "amazon-business-v16";

    internal static JsonObject VerifyAmazonFirstProducts(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonFirstProductsPolicy.Id, maximum), AmazonFirstProductsPolicy.Id);

    private static bool ProductEntry(string? href) => href is not null && !href.Contains('#') &&
        (AmazonAaxProductPolicy.IsEntry(href) || ObservedProductIdentity(href) is not null && AmazonAaxProductPolicy.Allows(href));

    private static bool ReviewAnchor(JsonObject link, string href) => AuxiliaryCardAnchor(link) &&
        (AmazonAaxProductPolicy.IsReview(href) || SponsoredCardReview.IsMatch(href) ||
            href.EndsWith("#customerReviews", StringComparison.Ordinal) && ObservedProductIdentity(href) is not null);

    private static bool CardSignal(JsonObject link)
    {
        var href = link["href"]!.ToString();
        return ProductEntry(href) || ObservedProductIdentity(href) is not null || AmazonAaxProductPolicy.IsSourceEntry(href) ||
            ContextAttribute(link) is not null && !IsSearchHelp(href) ||
            link["heading"] is not null || ContextAttribute(link) is not null && href == AmazonDealsPolicy.Address ||
            Uri.TryCreate(href, UriKind.Absolute, out var uri) && (uri.Host.StartsWith("aax-", StringComparison.Ordinal) ||
                uri.Host.StartsWith("sponsored-ads.", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/sspa/", StringComparison.Ordinal));
    }

    private static bool IsSearchHelp(string href) => AmazonAddress(href, out var uri) && uri.AbsolutePath == "/gp/help/customer/display.html";

    private static ResolvedStoreSource ResolveFirstProductCards(Snapshot source, (JsonObject read, int index)[] navigations,
        IReadOnlyDictionary<JsonObject, Snapshot> complete, ResolvedStoreSource state, int maximum)
    {
        var links = source.Records.OfType<JsonObject>().Where(r => r["kind"]?.ToString() == "link" && r["href"] is not null).ToArray();
        var members = links.ToLookup(r => (ContextAttribute(r)?["index"]?.ToString(), r["occurrence"]?["index"]?.ToString()));
        var processed = new HashSet<(string?, string?)>();
        var prefix = new List<JsonObject>();
        var previousEnd = -1;
        foreach (var link in links.Where(CardSignal))
        {
            var key = (ContextAttribute(link)?["index"]?.ToString(), link["occurrence"]?["index"]?.ToString());
            if (key.Item1 is null) { state.Findings.Add("source_product_card_unidentified"); return state; }
            if (!processed.Add(key)) continue;
            var card = members[key].ToArray();
            var start = Array.IndexOf(links, card[0]);
            var end = Array.IndexOf(links, card[^1]);
            if (start <= previousEnd || links.Skip(start).Take(end - start + 1).Any(r => CardSignal(r) && !SameCard(link, r)))
            { state.Findings.Add("source_card_order_ambiguous"); return state; }
            previousEnd = end;
            prefix.AddRange(card);
            if (!ConsistentCardMetadata(prefix) || card.Any(r => !SameCard(link, r)))
            { state.Findings.Add("search_card_metadata_inconsistent"); return state; }
            if (!ResolveFirstProductCard(card, navigations, complete, state)) return state;
            if (state.Products.Count == maximum) break;
        }
        if (state.Products.Count != maximum) state.Findings.Add("ordered_product_prefix_incomplete");
        state.Complete = state.Findings.Count == 0;
        return state;
    }

    private static bool ResolveFirstProductCard(JsonObject[] card, (JsonObject read, int index)[] navigations,
        IReadOnlyDictionary<JsonObject, Snapshot> complete, ResolvedStoreSource state)
    {
        var headings = card.Where(r => r["heading"]?.ToString() is { Length: > 0 }).ToArray();
        var code = ContextAttribute(card[0])!["value"]!.ToString();
        var products = card.Where(r => ProductEntry(r["href"]!.ToString())).ToArray();
        var auxiliary = card.Where(r => ReviewAnchor(r, r["href"]!.ToString()) ||
            r["href"]!.ToString() == AmazonDealsPolicy.Address && AuxiliaryCardAnchor(r)).ToArray();
        if (headings.Length > 1 || code.Length != 0 && !ProductCode(code))
            return Fail("source_product_identity_or_occurrence_ambiguous");
        if (card.Any(r => !products.Contains(r) && !auxiliary.Contains(r) && r["href"]!.ToString() != AmazonDealsPolicy.Address))
            return Fail("source_card_member_unverified");
        var primary = headings.Length == 1 ? headings : products;
        if (ProductCode(code))
        {
            if (primary.Length == 0 || primary.Any(r => !products.Contains(r))) return Fail("source_product_link_unavailable");
            foreach (var link in card)
            {
                var identity = ObservedProductIdentity(link["href"]!.ToString());
                if (identity is null || identity == code) continue;
                // A named variation link in its own nested listitem is distinct from the card's primary heading.
                if (headings.Length != 1 || !ProductEntry(link["href"]!.ToString()) || !AuxiliaryCardAnchor(link) || link["role"]?.ToString() != "link" ||
                    link["groupContext"]?["role"]?.ToString() != "listitem" || link["occurrence"]?["selector"]?.ToString() is not { } parent ||
                    link["group"]?.ToString() is not { Length: > 0 } group || group == parent ||
                    link["groupContext"]?["index"] is not JsonValue ordinal || !ordinal.TryGetValue<int>(out var index) || index < 0 ||
                    group.StartsWith('#') && !group.StartsWith(parent + " > ", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(link["text"]?.ToString()))
                    return Fail("source_card_product_identity_conflict");
            }
            return AddProduct(code, "known_source_card_membership");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in products)
        {
            var href = link["href"]!.ToString();
            if (ObservedProductIdentity(href) is { } direct) { identities.Add(direct); continue; }
            if (!Resolve(link, out var destination)) return false;
            if (ObservedProductIdentity(destination) is { } identity) identities.Add(identity);
            else if (AmazonResolvedStorePolicy.IsStore(destination)) destinations.Add(destination);
            else return Fail("source_resolution_destination_indeterminate");
        }
        if (identities.Count == 1 && destinations.Count == 0 &&
            (headings.Length == 1 && products.Contains(headings[0]) || headings.Length == 0 && card[0]["occurrence"] is not null))
            return AddProduct(identities.Single(), "resolved_source_card_membership");
        if (identities.Count != 0) return Fail("source_card_product_identity_conflict");
        foreach (var link in card.Where(r => r["href"]!.ToString() == AmazonDealsPolicy.Address))
        {
            if (!Resolve(link, out var destination)) return false;
            destinations.Add(destination);
        }
        if (destinations.Count == 0 || auxiliary.Any(r => r["href"]!.ToString() != AmazonDealsPolicy.Address))
            return Fail("source_card_identity_unresolved");
        return true;

        bool AddProduct(string identity, string proof)
        {
            var entries = primary.Select(r => r["reference"]!.ToString()).ToArray();
            var productRecords = products.Select(r => r["reference"]!.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var member in auxiliary)
            {
                var evidence = CardMembership(member["reference"]!.ToString(), new KnownCardReview(identity, entries), proof);
                evidence["classification"] = member["href"]!.ToString() == AmazonDealsPolicy.Address ? "auxiliary_promotion" : "auxiliary_review";
                evidence["proof_event_indices"] = new JsonArray(state.ResolutionEvidence.OfType<JsonObject>()
                    .Where(r => productRecords.Contains(r["source_record"]!.ToString()))
                    .Select(r => r["event_index"]!.DeepClone()).ToArray());
                state.AliasEvidence.Add(evidence);
            }
            state.Products.Add(new(identity, primary.Select(r => r["href"]!.ToString()).Distinct(StringComparer.Ordinal).ToArray()));
            return true;
        }
        bool Fail(string reason) { state.Findings.Add(reason); return false; }
        bool Resolve(JsonObject link, out string destination)
        {
            destination = "";
            var href = link["href"]!.ToString();
            var index = state.ResolutionCount++;
            if (index >= navigations.Length) { state.NextResolution = href; return false; }
            var (read, eventIndex) = navigations[index];
            if (read["arguments"]?["url"]?.ToString() != href || !complete.TryGetValue(read, out var observation) ||
                read["diagnosticSourceReference"] is { } supplied && supplied.ToString() != link["reference"]!.ToString() ||
                !ValidResolvedNavigation(read, AmazonFirstProductsPolicy.Id, state) || CaptureId(read) is not { } capture || !state.CaptureIds.Add(capture))
                return Fail("source_resolution_provenance_unverified");
            var navigation = read["result"]!["navigationEvidence"]!;
            destination = navigation["finalUrl"]!.ToString();
            if (destination != observation.Url && (ObservedProductIdentity(destination) is not { } id || ObservedProductIdentity(observation.Url) != id))
                return Fail("source_resolution_document_mismatch");
            if (href == AmazonDealsPolicy.Address && !VerifiedListingNavigation(navigation, destination)) return Fail("source_listing_chain_unverified");
            state.SourceReads.Add(read);
            state.ResolutionEvidence.Add(new JsonObject { ["source_record"] = link["reference"]!.DeepClone(), ["event_index"] = eventIndex,
                ["kind"] = href == AmazonDealsPolicy.Address ? "auxiliary_listing" : AmazonResolvedStorePolicy.IsStore(destination) ? "store_navigation" : "product_identity",
                ["product_identity"] = ObservedProductIdentity(destination), ["business_visit"] = false });
            return true;
        }
    }

    private static bool VerifiedListingNavigation(JsonNode navigation, string destination)
    {
        var hops = navigation["hops"]!.AsArray();
        return navigation["supersededResponses"] is null or JsonArray { Count: 0 } && hops[^1]?["statusCode"]?.GetValue<int>() == 200 &&
            (hops.Count == 1 && destination == AmazonDealsPolicy.Address ||
             hops.Count == 2 && destination == AmazonFirstProductsPolicy.SeasonalListing && hops[0]?["url"]?.ToString() == AmazonDealsPolicy.Address);
    }
}
