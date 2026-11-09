using System.Text.Json.Nodes;

internal static partial class LiveWorkflowOracles
{
    internal static IEnumerable<string> CompleteSearchRedirectEntries(IReadOnlyList<JsonObject> reads, string policy = AmazonObservedRedirectPolicy.Id)
        => policy == AmazonProductContextPolicy.Id ? CompleteContextSearchRedirectEntries(reads).Distinct(StringComparer.Ordinal) : CompleteSnapshots(reads).Where(s => WholeDocumentSearch(s.Read) && ValidNavigationEvidence(s.Read, policy) &&
            AmazonObservedRedirectPolicy.Allows(s.Url ?? "", policy) && new Uri(s.Url!).AbsolutePath == "/s" &&
            s.Read["arguments"]?["url"]?.ToString() is { } requested && AmazonObservedRedirectPolicy.Allows(requested, policy) &&
            new Uri(requested).AbsolutePath == "/s" && SearchQuery(new Uri(requested)) == SearchQuery(new Uri(s.Url!)) &&
            s.Read["result"]?["success"]?.GetValue<bool>() == true)
            .Where(s => { var findings = new List<string>(); ValidateSourceRecords(s, findings, policy); return findings.Count == 0; })
            .SelectMany(s => s.Records).Select(r => r?["href"]?.ToString())
            .Where(href => href is not null && AmazonObservedRedirectPolicy.IsEntry(href, policy)).Select(href => href!).Distinct(StringComparer.Ordinal);

    internal static JsonObject VerifyAmazonObservedRedirectSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => VerifyAmazonObservedSelection(events, maximum, query, AmazonObservedRedirectPolicy.Id, AmazonObservedRedirectVersion);

    internal static JsonObject VerifyAmazonSponsoredHostSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => VerifyAmazonObservedSelection(events, maximum, query, AmazonSponsoredHostPolicy.Id, AmazonSponsoredHostVersion);

    private static JsonObject VerifyAmazonObservedSelection(IReadOnlyList<JsonObject> events, int maximum, string? query, string policy, string version)
    {
        if (maximum is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maximum));
        var findings = new List<string>();
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        var searches = events.Select((read, index) => (read, index)).Where(p => p.read["tool"]?.ToString() == "browser_get_content" &&
            AmazonAddress(p.read["arguments"]?["url"]?.ToString(), out var address) && address.AbsolutePath == "/s").ToArray();
        Snapshot? search = null; var searchIndex = -1;
        foreach (var (read, index) in searches)
        {
            if (string.IsNullOrWhiteSpace(query) || SearchQuery(new Uri(read["arguments"]!["url"]!.ToString())) != query)
                findings.Add("search_query_does_not_match_input");
            if (!WholeDocumentSearch(read)) findings.Add("search_observation_scope_not_independently_complete");
            else if (!complete.TryGetValue(read, out var snapshot) || !AmazonAddress(snapshot.Url, out var actual) ||
                actual.AbsolutePath != "/s" || SearchQuery(actual) != query || snapshot.Records.Count == 0 ||
                read["result"]?["success"]?.GetValue<bool>() != true)
                findings.Add("search_observation_not_independently_complete");
            else if (search is null) { search = snapshot; searchIndex = index; }
        }
        var expected = new List<(string? Identity, string[] Urls)>();
        if (search is null) findings.Add("requested_search_not_observed");
        else
        {
            if (!ValidNavigationEvidence(search.Read, policy)) findings.Add("search_navigation_provenance_incomplete");
            ValidateSourceRecords(search, findings, policy);
            var candidates = search.Records.OfType<JsonObject>().Where(r => r["href"]?.ToString() is { } href &&
                (ObservedProductIdentity(href) is not null || AmazonObservedRedirectPolicy.IsSourceEntry(href, policy))).ToArray();
            ValidateObservedProductRoutes(search, findings, policy);
            foreach (var group in candidates.GroupBy(r => r["occurrence"]?["selector"]?.ToString() ?? "", StringComparer.Ordinal))
            {
                if (group.Key.Length == 0) { findings.Add("search_product_occurrences_ambiguous"); continue; }
                var primary = group.Where(r => r["heading"]?.ToString() is { Length: > 0 }).ToArray();
                if (primary.Length == 0) primary = group.ToArray();
                var identities = primary.Select(r => ObservedProductIdentity(r["href"]!.ToString())).Where(id => id is not null).Distinct(StringComparer.Ordinal).ToArray();
                var urls = primary.Select(r => r["href"]!.ToString()).Distinct(StringComparer.Ordinal).ToArray();
                if (identities.Length > 1 || identities.Length == 0 && urls.Length != 1 ||
                    identities.Length > 0 && primary.Any(r => AmazonObservedRedirectPolicy.IsSourceEntry(r["href"]!.ToString(), policy)))
                    findings.Add("search_product_aliases_unverified");
                // Distinct source occurrences stay distinct even when their ASINs match.
                expected.Add((identities.FirstOrDefault(), urls));
            }
            if (expected.Count == 0) findings.Add("search_product_identities_unverified");
        }
        var selected = expected.Take(maximum).ToArray();
        var visits = events.Select((read, index) => (read, index)).Where(p => p.read["tool"]?.ToString() == "browser_get_content" &&
            p.read["arguments"]?["url"] is { } url && !(AmazonAddress(url.ToString(), out var address) && address.AbsolutePath == "/s")).ToArray();
        if (visits.Any(p => p.index <= searchIndex)) findings.Add("product_visit_before_search");
        if (visits.Length != selected.Length || selected.Length == 0) findings.Add("first_product_visits_incomplete");
        for (var index = 0; index < Math.Min(visits.Length, selected.Length); index++)
        {
            var (read, at) = visits[index]; var product = selected[index];
            if (at <= searchIndex || !product.Urls.Contains(read["arguments"]!["url"]!.ToString(), StringComparer.Ordinal) ||
                !complete.TryGetValue(read, out var snapshot) || ObservedProductIdentity(snapshot.Url) is not { } identity ||
                product.Identity is { } expectedIdentity && identity != expectedIdentity)
                findings.Add("first_product_identity_or_order_mismatch");
            if (!ValidNavigationEvidence(read, policy) || ObservedProductIdentity(read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()) != ObservedProductIdentity(read["result"]?["url"]?.ToString()))
                findings.Add("product_navigation_provenance_incomplete");
        }
        return new() { ["version"] = version, ["passed"] = findings.Count == 0,
            ["expected_product_count"] = selected.Length, ["visited_product_count"] = visits.Length,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    private static void ValidateObservedProductRoutes(Snapshot snapshot, List<string> findings, string policy)
    {
        foreach (var record in snapshot.Records.OfType<JsonObject>())
        {
            if (record["href"]?.ToString() is not { } href) continue;
            if (!AmazonAddress(href, out var address))
            {
                if (policy is AmazonSponsoredHostPolicy.Id or AmazonProductContextPolicy.Id && Uri.TryCreate(href, UriKind.Absolute, out var other) &&
                    other.Host.StartsWith("sponsored-ads.", StringComparison.Ordinal) && !AmazonSponsoredHostPolicy.IsSourceEntry(href))
                    findings.Add("search_product_redirect_route_unapproved");
                continue;
            }
            if ((address.AbsolutePath.Contains("/dp/", StringComparison.Ordinal) || address.AbsolutePath.Contains("/gp/product/", StringComparison.Ordinal)) && ObservedProductIdentity(href) is null)
                findings.Add("search_product_identity_unverified");
            if ((address.AbsolutePath.Contains("sspa", StringComparison.Ordinal) || address.AbsolutePath.Contains("slredirect", StringComparison.Ordinal)) && !AmazonObservedRedirectPolicy.IsSourceEntry(href, policy))
                findings.Add("search_product_redirect_route_unapproved");
        }
    }

    private static string? ObservedProductIdentity(string? value)
    {
        if (ProductIdentity(value) is { } identity) return identity;
        if (!AmazonAddress(value, out var address)) return null;
        var parts = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["gp", "aw", "d", var asin] && asin.Length == 10 && asin.All(char.IsAsciiLetterOrDigit) ? asin : null;
    }

    private static void ValidateSourceRecords(Snapshot snapshot, List<string> findings, string policy = AmazonObservedRedirectPolicy.Id)
    {
        var id = snapshot.Read["result"]?["observationSnapshot"]?["id"]?.ToString() ??
            snapshot.Read["result"]?["observationManifest"]?["id"]?.ToString() ?? snapshot.Read["result"]?["observation"]?["id"]?.ToString();
        var contexts = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var indices = new Dictionary<int, string>(); var previousIndex = -1;
        for (var index = 0; index < snapshot.Records.Count; index++)
        {
            var record = snapshot.Records[index];
            if (id is null || record?["reference"]?.ToString() != id + ":record:" + index)
                findings.Add("search_record_provenance_inconsistent");
            if (record?["occurrence"] is not JsonObject occurrence)
            {
                if (ObservedProductIdentity(record?["href"]?.ToString()) is not null || AmazonObservedRedirectPolicy.IsSourceEntry(record?["href"]?.ToString() ?? "", policy))
                    findings.Add("search_product_occurrences_ambiguous");
                continue;
            }
            var selector = occurrence["selector"]?.ToString();
            if (selector is not { Length: > 0 } || occurrence["index"] is not JsonValue value || !value.TryGetValue<int>(out var at) || at < 0 ||
                !(occurrence["tag"]?.ToString() is "article" or "li" or "tr" || occurrence["role"]?.ToString() is "article" or "listitem" or "row"))
            { findings.Add("search_product_occurrences_ambiguous"); continue; }
            if (contexts.TryGetValue(selector, out var retained))
            {
                if (!JsonNode.DeepEquals(retained, occurrence)) findings.Add("search_occurrence_context_inconsistent");
            }
            else
            {
                if (at <= previousIndex || indices.TryGetValue(at, out var other) && other != selector)
                    findings.Add("search_occurrence_order_inconsistent");
                contexts[selector] = occurrence; indices[at] = selector; previousIndex = at;
            }
        }
    }

    internal static bool ValidNavigationEvidence(JsonObject read, string policy = AmazonObservedRedirectPolicy.Id)
    {
        var result = read["result"];
        if (result?["success"]?.GetValue<bool>() != true || result["navigationEvidence"] is not JsonObject evidence ||
            evidence["complete"]?.GetValue<bool>() != true || evidence["requestedUrl"]?.ToString() != read["arguments"]?["url"]?.ToString() ||
            (evidence["documentUrl"] ?? evidence["finalUrl"])?.ToString() != result["url"]?.ToString() ||
            !AmazonObservedRedirectPolicy.Allows(result["url"]?.ToString() ?? "", policy) ||
            evidence["hops"] is not JsonArray hops || hops.Count is < 1 or > 21) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal); var expected = evidence["requestedUrl"]!.ToString();
        for (var index = 0; index < hops.Count; index++)
        {
            var hop = hops[index]; var url = hop?["url"]?.ToString();
            if (url is null || url != expected || !AmazonObservedRedirectPolicy.Allows(url, policy) || !seen.Add(url) || hop?["method"]?.ToString() != "GET" ||
                hop["statusCode"] is not JsonValue status || !status.TryGetValue<int>(out var code)) return false;
            if (index == hops.Count - 1)
                return code is >= 200 and < 300 && hop["location"] is null && evidence["finalUrl"]?.ToString() == url;
            if (code is not (301 or 302 or 303 or 307 or 308) || hop["location"]?.ToString() is not { Length: > 0 } location ||
                !Uri.TryCreate(new Uri(url), location, out var next)) return false;
            expected = next.AbsoluteUri;
        }
        return false;
    }
}
