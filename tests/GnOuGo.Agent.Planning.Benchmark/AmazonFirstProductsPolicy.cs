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
    internal static void ConfigureDetailText(IDictionary<string, string?> environment)
    {
        const string key = "Browser__ObservationTextSelectorPrefixes__";
        string[] prefixes = ["#title", "#productTitle", "#productDescription", "#productFactsDesktopExpander", "#feature-bullets",
            "#apex-pricetopay", "#corePrice", "#newAccordionRow_0", "#captcha", "#challenge", "#error"];
        var configured = environment.Where(pair => pair.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        var saved = configured.Select(pair => pair.Value).ToHashSet(StringComparer.Ordinal);
        var occupied = configured.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in prefixes.Where(prefix => !saved.Contains(prefix)))
        {
            if (occupied.Count >= 16) throw new InvalidOperationException("The combined detail selector configuration exceeds the Browser limit.");
            var slot = Enumerable.Range(0, 16).Select(index => key + index).First(slot => !occupied.Contains(slot));
            environment.Add(slot, prefix);
            occupied.Add(slot);
        }
    }
    internal const string PlanningFacts =
        "Acquire the encoded input query at amazon.fr/s with browser_get_content, format=observation_complete and no selector. " +
        "Set prefixAttribute=data-asin and prefixGroups=10. The complete search retains observationSnapshot.pages[].records[].ancestorAttributes. " +
        "observedSourceAccepted=true establishes the observed 2xx GET, complete capture and publication of the requested prefix, including an empty link list. " +
        "Assess the captured pages together for a challenge or error. Return a boolean for a nonempty, entirely clear capture and the observed reason. " +
        "Select only when this boolean and observedSourceAccepted each equal true and the prefix exists. Otherwise return the actual reason. " +
        "Bind observationSnapshot.linkPrefix alone to ordered selection. Its links and ancestor table retain original order, grouping, heading, href and page/offset. " +
        "Select the first ten actual product occurrences, including sponsored cards and repeated ASINs. Use the heading or a same-card product link when no heading exists. " +
        "Keep auxiliary reviews and offers associated with their cards. A populated ASIN alone does not establish a product. " +
        "Resolve only earlier ambiguous empty-ASIN groups using their exact observed hrefs in order. A necessary observed /deals GET may redirect to /blackfriday. Exclude stores and listings. " +
        "The prefix decision returns one status, ordered evidence and a reason. Use complete for ten established products, needs_navigation for an unresolved earlier group, or incomplete for an observed shortfall or failure. " +
        "Branch on that status. Keep the bounded navigation agent inside the needs_navigation branch. The complete branch uses the observed list directly. " +
        "Stop selecting at ten. A short prefix requires further original groups in order or an explicit observed shortfall. " +
        "Use the same selection schema after resolution, with status, ordered products and reason. Retain only observed entries on partial or empty outcomes. " +
        "Keep each selected product's exact signed href and its zero-based sourceIndex in the original linkPrefix.links array in both selection branches. " +
        "Preserve the Browser href pattern in the agent output schema. " +
        "On complete status and ten entries, use one sequential loop. In each iteration first test that sourceIndex is in range. Read the original link at each sourceIndex with at_index and require its href to equal the selected href in a conditional. On a match bind browser_get_content.url directly to the original link's href at that index. The selected href is the comparison operand. The other branch returns an ineligible row and the observed discrepancy. Source resolutions are separate from product visits. " +
        "Each observation_complete detail GET exposes observationSnapshot.textMatches.records in original page order with text, selector and source reference. " +
        "Bind this compact view to the field extraction together with visit status, page identity and any observed error. Pass textMatches.records as a distinct input. Reject challenges, errors and incomplete evidence. " +
        "Extract named fields and preserve unavailable values explicitly. Form each row from name, description and price in that order. Check ten true visit eligibilities before writing. " +
        "The expected matrix starts with a batch containing the single header row, followed by the batch of ten ordered visit rows. " +
        "Independently reopen the same workbook with document_read format=cells and compare the worksheet's rows with that full matrix. Return the required public string finalOutcome at the root. After successful readback and exact equality include the verified workbook path. If the read fails or its cells differ, report verified=false and the reason, with the read status and both matrices as evidence. A successful document_read alone only confirms the read. " +
        "Close Browser in the enclosing workflow's cleanup. A URL-less page read requires a current issued cursor and observation or observation_pages format. " +
        AmazonObservedRedirectPolicy.AcquisitionGuarantees;

    internal static string RunPathFacts(string directory)
    {
        var arguments = new JsonObject
        {
            ["agent.run"] = new JsonObject { ["workspace"] = directory },
            ["document_write"] = new JsonObject { ["filePath"] = directory + "/products.xlsx" }
        };
        return "Host path arguments by operation: " + arguments.ToJsonString() +
            ". The adaptive selection supplies workspace inside its own agent.run call. Before workflow execution the host deterministically prepares that confined directory. " +
            "Workbook delivery uses a direct document_write call with filePath and rows. Document MCP resolves the entire supplied filePath from its own workspace root and creates the parent directories during that write.";
    }
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
