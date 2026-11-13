using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    private sealed record ContextProduct(string Identity, string[] Urls);

    private static IEnumerable<string> CompleteContextSearchRedirectEntries(IReadOnlyList<JsonObject> reads)
    {
        foreach (var snapshot in CompleteSnapshots(reads))
        {
            if (!WholeDocumentSearch(snapshot.Read) || !ValidNavigationEvidence(snapshot.Read, AmazonProductContextPolicy.Id) ||
                !AmazonAddress(snapshot.Url, out var actual) || actual.AbsolutePath != "/s" ||
                !AmazonAddress(snapshot.Read["arguments"]?["url"]?.ToString(), out var requested) || requested.AbsolutePath != "/s" ||
                SearchQuery(actual) != SearchQuery(requested)) continue;
            var findings = new List<string>();
            var products = ContextProducts(snapshot, findings);
            if (findings.Count != 0 || products.Count == 0) continue;
            foreach (var record in snapshot.Records.OfType<JsonObject>())
                if (record["href"]?.ToString() is { } href && AmazonSponsoredHostPolicy.IsEntry(href) &&
                    ContextAttribute(record) is { } context && ProductCode(context["value"]?.ToString())) yield return href;
        }
    }

    internal static JsonObject VerifyAmazonProductContextSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
    {
        if (maximum is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maximum));
        var findings = new List<string>();
        var snapshots = CompleteSnapshots(events).ToDictionary(s => s.Read);
        Snapshot? search = null; var searchIndex = -1;
        foreach (var (read, index) in events.Select((read, index) => (read, index)))
        {
            if (read["tool"]?.ToString() != "browser_get_content" ||
                !AmazonAddress(read["arguments"]?["url"]?.ToString(), out var requested) || requested.AbsolutePath != "/s") continue;
            if (string.IsNullOrWhiteSpace(query) || SearchQuery(requested) != query) findings.Add("search_query_does_not_match_input");
            if (!WholeDocumentSearch(read)) findings.Add("search_observation_scope_not_independently_complete");
            else if (!snapshots.TryGetValue(read, out var snapshot) || !AmazonAddress(snapshot.Url, out var actual) ||
                     actual.AbsolutePath != "/s" || SearchQuery(actual) != query || read["result"]?["success"]?.GetValue<bool>() != true)
                findings.Add("search_observation_not_independently_complete");
            else if (search is null) { search = snapshot; searchIndex = index; }
        }
        var products = new List<ContextProduct>();
        if (search is null) findings.Add("requested_search_not_observed");
        else
        {
            if (!ValidNavigationEvidence(search.Read, AmazonProductContextPolicy.Id)) findings.Add("search_navigation_provenance_incomplete");
            products = ContextProducts(search, findings);
        }
        var expected = products.Take(maximum).ToArray();
        var visits = events.Select((read, index) => (read, index)).Where(p => p.read["tool"]?.ToString() == "browser_get_content" &&
            p.read["arguments"]?["url"] is { } url && !(AmazonAddress(url.ToString(), out var address) && address.AbsolutePath == "/s")).ToArray();
        if (visits.Length != expected.Length || expected.Length == 0) findings.Add("first_product_visits_incomplete");
        for (var index = 0; index < Math.Min(visits.Length, expected.Length); index++)
        {
            var (read, at) = visits[index]; var product = expected[index];
            if (at <= searchIndex || !product.Urls.Contains(read["arguments"]!["url"]!.ToString(), StringComparer.Ordinal) ||
                !snapshots.TryGetValue(read, out var snapshot) || ObservedProductIdentity(snapshot.Url) != product.Identity)
                findings.Add("first_product_identity_or_order_mismatch");
            if (!ValidNavigationEvidence(read, AmazonProductContextPolicy.Id) ||
                ObservedProductIdentity(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()) != product.Identity)
                findings.Add("product_navigation_provenance_incomplete");
        }
        return new() { ["version"] = AmazonProductContextVersion, ["passed"] = findings.Count == 0,
            ["expected_product_count"] = expected.Length, ["visited_product_count"] = visits.Length,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    private static List<ContextProduct> ContextProducts(Snapshot snapshot, List<string> findings,
        IReadOnlySet<string>? independentlyResolvedStoreReferences = null,
        IReadOnlyDictionary<string, string>? independentlyResolvedProductReferences = null, string policy = AmazonProductContextPolicy.Id,
        IReadOnlySet<string>? proofAliasReferences = null, IReadOnlySet<string>? independentlyResolvedStoreListReferences = null)
    {
        var id = snapshot.Read["result"]?["observationSnapshot"]?["id"]?.ToString() ??
            snapshot.Read["result"]?["observationManifest"]?["id"]?.ToString() ?? snapshot.Read["result"]?["observation"]?["id"]?.ToString();
        ValidateObservedProductRoutes(snapshot, findings, policy, proofAliasReferences);
        var contexts = new Dictionary<int, JsonObject>();
        var candidates = new List<JsonObject>();
        var occurrences = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var occurrenceIndices = new Dictionary<int, string>(); var previousOccurrence = -1;
        for (var index = 0; index < snapshot.Records.Count; index++)
        {
            var record = snapshot.Records[index];
            if (id is null || record?["reference"]?.ToString() != id + ":record:" + index) findings.Add("search_record_provenance_inconsistent");
            if (record?["href"]?.ToString() is not { } href ||
                ObservedProductIdentity(href) is null && !AmazonObservedRedirectPolicy.IsSourceEntry(href, policy) &&
                proofAliasReferences?.Contains(record["reference"]?.ToString() ?? "") != true) continue;
            var context = ContextAttribute(record);
            if (record["kind"]?.ToString() != "link" || context is null || context["value"] is not JsonValue value || !value.TryGetValue<string>(out var code) ||
                context["tag"] is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var tag) ||
                tag.Length is < 1 or > 64 || !char.IsAsciiLetter(tag[0]) || tag.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
                context["role"] is { } role && (role is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)) || context["index"] is not JsonValue number || !number.TryGetValue<int>(out var at) || at < 0)
            { findings.Add("search_product_context_unverified"); continue; }
            if (contexts.TryGetValue(at, out var retained))
            {
                if (!JsonNode.DeepEquals(retained, context)) findings.Add("search_product_context_inconsistent");
            }
            else
            {
                contexts[at] = context;
            }
            // Independent resolution evidence is supplied only by its separately
            // authorized verifier or offline proposal. Legacy versions retain null.
            if (code.Length == 0 && ObservedProductIdentity(href) is null && record["occurrence"] is null && record["heading"] is null &&
                independentlyResolvedStoreReferences?.Contains(record["reference"]!.ToString()) == true)
                continue;
            if (code.Length == 0 && ObservedProductIdentity(href) is null &&
                independentlyResolvedStoreListReferences?.Contains(record["reference"]!.ToString()) == true)
                continue; // Separately authorized, individually proved store-card membership; raw occurrence remains intact.
            if (code.Length == 0 && ObservedProductIdentity(href) is null && record["occurrence"] is null && record["heading"] is null &&
                (tag == "nav" || context["role"]?.ToString() == "navigation"))
                continue; // Observed navigation semantics, never an inference from the empty identifier alone.
            if (code.Length == 0 && independentlyResolvedProductReferences?.TryGetValue(record["reference"]!.ToString(), out var resolvedIdentity) == true)
                code = resolvedIdentity; // Local evidence value only; never edit the source attribute.
            if (!ProductCode(code)) { findings.Add("search_product_context_unverified"); continue; }
            if (record["occurrence"] is JsonObject occurrence)
            {
                var selector = occurrence["selector"]?.ToString();
                if (selector is not { Length: > 0 } || occurrence["index"] is not JsonValue order || !order.TryGetValue<int>(out var ordinal) || ordinal < 0 ||
                    !(occurrence["tag"]?.ToString() is "article" or "li" or "tr" || occurrence["role"]?.ToString() is "article" or "listitem" or "row"))
                { findings.Add("search_product_occurrences_ambiguous"); continue; }
                if (occurrences.TryGetValue(selector, out var prior))
                {
                    if (!JsonNode.DeepEquals(prior, occurrence)) findings.Add("search_occurrence_context_inconsistent");
                }
                else
                {
                    if (ordinal <= previousOccurrence || occurrenceIndices.ContainsKey(ordinal)) findings.Add("search_occurrence_order_inconsistent");
                    previousOccurrence = ordinal; occurrenceIndices[ordinal] = selector; occurrences[selector] = occurrence;
                }
            }
            candidates.Add(record.AsObject());
        }
        var products = new List<ContextProduct>();
        foreach (var group in candidates.GroupBy(record => record["occurrence"]?["selector"]?.ToString() ??
                     "attribute:" + ContextAttribute(record)!["index"]!.ToString(), StringComparer.Ordinal))
        {
            var primary = group.Where(record => record["heading"]?.ToString() is { Length: > 0 }).ToArray();
            if (primary.Length == 0) primary = group.ToArray();
            foreach (var record in primary)
                if (ObservedProductIdentity(record["href"]!.ToString()) is { } identity && identity != Identity(record))
                    findings.Add("search_product_context_identity_mismatch");
            var identities = primary.Select(Identity).Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length != 1) { findings.Add("search_product_aliases_unverified"); continue; }
            var urls = primary.Select(record => record["href"]!.ToString()).Where(url => AmazonObservedRedirectPolicy.Allows(url, policy)).Distinct(StringComparer.Ordinal).ToArray();
            if (urls.Length == 0) findings.Add("search_product_link_unavailable");
            products.Add(new(identities[0], urls));
        }
        return products;
        string Identity(JsonObject record) => independentlyResolvedProductReferences?.TryGetValue(record["reference"]!.ToString(), out var identity) == true
            ? identity : ContextAttribute(record)!["value"]!.ToString();
    }

    private static JsonObject? ContextAttribute(JsonNode? record) => record?["ancestorAttributes"]?[AmazonProductContextPolicy.ProductAttribute] as JsonObject;
    private static bool ProductCode(string? value) => value is { Length: 10 } && value.All(char.IsAsciiLetterOrDigit);
}
