using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonAaxProductVersion = "amazon-business-v14";
    internal static JsonObject VerifyAmazonAaxProductSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonAaxProductPolicy.Id), AmazonAaxProductPolicy.Id);

    private static Dictionary<string, string[]> AaxReviewAliases(Snapshot source, Dictionary<string, KnownCardReview> known)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var records = source.Records.OfType<JsonObject>().ToArray();
        foreach (var record in records)
        {
            if (record["href"]?.ToString() is not { } href || !AmazonAaxProductPolicy.IsReview(href) ||
                !AuxiliaryCardAnchor(record) || ContextAttribute(record) is not { } context) continue;
            var twins = records.Where(other => SameCard(record, other) && other["href"]?.ToString() is { } original &&
                AmazonAaxProductPolicy.IsAaxEntry(original) && href == original + "#customerReviews").ToArray();
            if (twins.Length == 0) continue;
            var reference = record["reference"]!.ToString();
            var targets = twins.Select(t => t["reference"]!.ToString()).ToArray();
            if (ProductCode(context["value"]?.ToString()) && records.Where(r => SameCard(record, r)).All(r =>
                ObservedProductIdentity(r["href"]?.ToString()) is not { } identity || identity == context["value"]!.ToString()))
                known.Add(reference, new(context["value"]!.ToString(), targets));
            else if (context["value"]?.ToString() == "" && record["occurrence"] is null) aliases.Add(reference, targets);
        }
        return aliases;
    }

    private static bool PrepareAaxCoverage(Snapshot source, ResolvedStoreSource state, IReadOnlySet<string> aliases)
    {
        state.UnclassifiedContexts = UnclassifiedHeadingContexts(source);
        var records = source.Records.OfType<JsonObject>().ToDictionary(r => r["reference"]!.ToString(), StringComparer.Ordinal);
        foreach (var context in state.UnclassifiedContexts.OfType<JsonObject>())
        {
            var heading = records[context["heading_record"]!.ToString()];
            var code = context["asin_context"]?["value"]?.ToString();
            if (!AmazonAaxProductPolicy.IsAaxEntry(heading["href"]!.ToString()) || code != "" && !ProductCode(code) ||
                context["source_records"]!.AsArray().Any(r => !AmazonAaxProductPolicy.Allows(records[r!.ToString()]["href"]!.ToString()) && !aliases.Contains(r!.ToString())))
                state.Findings.Add("search_heading_context_unaccounted");
        }
        return state.Findings.Count == 0;
    }

    private static void BindAaxCoverage(Snapshot source, ResolvedStoreSource state, IReadOnlyDictionary<string, string> products)
    {
        var records = source.Records.OfType<JsonObject>().ToArray();
        var own = records.Where(r => r["href"]?.ToString() is { } href && AmazonAaxProductPolicy.IsAaxEntry(href) &&
            products.ContainsKey(r["reference"]!.ToString())).ToArray();
        var identities = own.ToDictionary(r => r["reference"]!.ToString(), r => products[r["reference"]!.ToString()], StringComparer.Ordinal);
        var originals = own.Select(r => r["href"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        state.AaxContextEvidence = AaxProductContextEvidence(source, identities, originals, state.Findings, emptyOnly: true);
        var supported = state.AaxContextEvidence.OfType<JsonObject>().Where(c => c["relationship_supported"]?.GetValue<bool>() == true)
            .Select(c => c["original_context"]!["heading_record"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        state.UnclassifiedContexts = new JsonArray(state.UnclassifiedContexts.OfType<JsonObject>()
            .Where(c => !ProductCode(c["asin_context"]?["value"]?.ToString()) && !supported.Contains(c["heading_record"]!.ToString()))
            .Select(c => (JsonNode)c.DeepClone()).ToArray());
        if (state.UnclassifiedContexts.Count != 0) state.Findings.Add("search_heading_context_unaccounted");
    }

    private static bool ValidResolvedNavigation(JsonObject read, string policy, ResolvedStoreSource state)
    {
        if (policy != AmazonAaxProductPolicy.Id) return ValidNavigationEvidence(read, policy);
        bool Allows(string value) => AmazonSourceCoveragePolicy.Allows(value) || state.ObservedAaxEntries.Contains(value);
        if (!ValidNavigationEvidence(read, Allows)) return false;
        // Check the literal redirect before URI canonicalization, matching the
        // production Browser's before-contact decision. Old versions stay intact.
        foreach (var hop in read["result"]!["navigationEvidence"]!["hops"]!.AsArray())
            if (hop?["location"]?.ToString() is { } location &&
                (!Uri.TryCreate(new Uri(hop["url"]!.ToString()), location, out var next) || !Allows(next.OriginalString))) return false;
        return true;
    }

    private static string? CaptureId(JsonObject read) => read["result"]?["observationSnapshot"]?["id"]?.ToString() ??
        read["result"]?["observationManifest"]?["id"]?.ToString() ?? read["result"]?["observation"]?["id"]?.ToString();
    private static JsonArray AaxProductContextEvidence(Snapshot source, IReadOnlyDictionary<string, string> identities,
        IReadOnlySet<string> originals, List<string> findings, bool emptyOnly = false)
    {
        var contexts = new JsonArray();
        var records = source.Records.OfType<JsonObject>().ToDictionary(record => record["reference"]!.ToString(), StringComparer.Ordinal);
        foreach (var context in UnclassifiedHeadingContexts(source).OfType<JsonObject>())
        {
            if (emptyOnly && context["asin_context"]?["value"]?.ToString() != "") continue;
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
        return contexts;
    }

}

// Separate explicit grant. The static Browser routes remain v9; AAX authority
// exists only for exact links in the owning Browser's complete current source.
internal static class AmazonAaxProductPolicy
{
    internal const string Id = "amazon-observed-aax-product-context-v10";
    internal const string Approval = "user-amazon-aax-workflow-20261112";
    internal const string SourcePattern = @"https://(?:www\.)?amazon\.fr/s\?[^#\s]+";
    internal const string EntryPattern = @"https://aax-eu-zaz\.amazon\.fr/x/c/[^#\s]+";
    internal const string UrlPattern = AmazonSourceCoveragePolicy.UrlPattern;
    internal static bool IsAaxEntry(string value)
    {
        if (!AmazonAaxSourceDiagnostic.IsEntry(value)) return false;
        try { _ = GnOuGo.Browser.Mcp.BrowserNavigationPolicy.ValidateNavigationTarget(value,
            new() { AllowedNavigationUrlPatterns = [Regex.Escape(value)] }); return true; }
        catch (InvalidOperationException) { return false; }
    }
    internal static bool Allows(string value) => AmazonSourceCoveragePolicy.Allows(value) || IsAaxEntry(value);
    internal static bool IsEntry(string value) => AmazonSourceCoveragePolicy.IsEntry(value) || IsAaxEntry(value);
    internal static bool IsSourceEntry(string value) => AmazonSourceCoveragePolicy.IsSourceEntry(value) || IsAaxEntry(value) || IsReview(value);
    private static readonly Regex Reviews = new(@"\A(?:" + EntryPattern + @")#customerReviews\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    internal static bool IsReview(string value) => Reviews.IsMatch(value);
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-aax-product-context-v10 adds only exact fragment-free AAX /x/c/ entries from the complete current owned search. Every unresolved occurrence, including repetitions, requires its own ordered complete read and matching product redirect/document identity. Every hop must use an existing approved route or an exact AAX entry from that same source. Empty-ASIN cards require consistent original context, exactly one heading, one proved identity and no unproved sibling. Only exact same-card non-heading base-href+#customerReviews twins may reference completed proof; aliases create no HTTP receipt or navigable URL. Retain every raw record, product occurrence and source order. Source reads are separate from all later first-N business visits, fields, real Excel and cleanup. " + AmazonSourceCoveragePolicy.PlanningFacts;
}
