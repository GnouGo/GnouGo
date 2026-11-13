using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static partial class LiveWorkflowOracles
{
    internal const string AmazonCardReviewVersion = "amazon-business-v11";
    internal static JsonObject VerifyAmazonCardReviewSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
        => ResolvedStoreSelection(events, maximum, query, ResolveStoreSource(events, query, AmazonCardReviewPolicy.Id), AmazonCardReviewPolicy.Id);

    private sealed record KnownCardReview(string Identity, string[] Entries);

    private static Dictionary<string, string[]> CardReviewAliases(Snapshot source, List<string> findings,
        out Dictionary<string, KnownCardReview> known)
    {
        var aliases = ReviewProofAliases(source); known = new(StringComparer.Ordinal);
        var records = source.Records.OfType<JsonObject>().ToArray();
        if (!ConsistentCardMetadata(records)) { findings.Add("search_card_metadata_inconsistent"); return aliases; }
        foreach (var record in records)
        {
            if (record["href"]?.ToString() is not { } href ||
                !(href.StartsWith("https://sponsored-ads.amazon.fr/", StringComparison.Ordinal) ||
                  href.StartsWith("https://www.amazon.fr/sspa/click?", StringComparison.Ordinal) || href.StartsWith("https://amazon.fr/sspa/click?", StringComparison.Ordinal)) ||
                !SponsoredCardReview.IsMatch(href) || !AuxiliaryCardAnchor(record) ||
                record["reference"]?.ToString() is not { } reference || ContextAttribute(record) is not { } context) continue;
            var card = records.Where(r => SameCard(record, r)).ToArray();
            var entries = card.Where(r => r["kind"]?.ToString() == "link" &&
                r["href"]?.ToString() is { } original && AmazonCardReviewPolicy.Allows(original)).ToArray();
            var code = context["value"]!.GetValue<string>();
            if (ProductCode(code) && entries.Length != 0 && card.All(r => ObservedProductIdentity(r["href"]?.ToString()) is not { } identity || identity == code))
            {
                known.Add(reference, new(code, entries.Select(r => r["reference"]!.ToString()).ToArray()));
                continue;
            }
            if (code.Length != 0 || context["tag"]?.ToString() == "nav" || context["role"]?.ToString() == "navigation") continue;
            var exact = entries.Where(r => href == r["href"]!.ToString() + "#customerReviews").Select(r => r["reference"]!.ToString()).ToArray();
            if (exact.Length != 0 && entries.Count(r => r["heading"]?.ToString() is { Length: > 0 }) == 1) aliases[reference] = exact;
        }
        return aliases;
    }

    // The pattern classifies retained data only; no captured HTTP prefix is
    // returned, stripped, substituted, or granted navigation authority.
    private static readonly Regex SponsoredCardReview = new(@"\A(?:" + AmazonCardReviewPolicy.UrlPattern + @")#customerReviews\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    private static bool AuxiliaryCardAnchor(JsonObject record) => record["kind"]?.ToString() == "link" && record["tag"]?.ToString() == "a" &&
        record["heading"] is null && record["role"]?.ToString() is not ("article" or "listitem" or "row") &&
        (record["occurrence"] is null || record["selector"] is JsonValue own && own.TryGetValue<string>(out var selector) &&
         record["occurrence"]?["selector"]?.ToString() is { Length: > 0 } parent && selector != parent);

    private static bool SameCard(JsonObject left, JsonObject right) =>
        JsonNode.DeepEquals(ContextAttribute(left), ContextAttribute(right)) && JsonNode.DeepEquals(left["occurrence"], right["occurrence"]);

    private static bool ConsistentCardMetadata(IEnumerable<JsonObject> records)
    {
        var contexts = new Dictionary<int, JsonNode>(); var occurrences = new Dictionary<int, JsonNode>();
        var selectors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record["ancestorAttributes"]?[AmazonProductContextPolicy.ProductAttribute] is { } context)
            {
                if (context is not JsonObject || context["value"] is not JsonValue value || !value.TryGetValue<string>(out _) ||
                    context["tag"] is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var tag) || tag.Length is < 1 or > 64 ||
                    !char.IsAsciiLetter(tag[0]) || tag.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
                    context["role"] is { } role && (role is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)) ||
                    context["index"] is not JsonValue index || !index.TryGetValue<int>(out var ordinal) || ordinal < 0 ||
                    contexts.TryGetValue(ordinal, out var prior) && !JsonNode.DeepEquals(context, prior)) return false;
                contexts[ordinal] = context;
            }
            if (record["occurrence"] is { } occurrence)
            {
                if (occurrence is not JsonObject || occurrence["selector"] is not JsonValue selectorValue || !selectorValue.TryGetValue<string>(out var selector) ||
                    string.IsNullOrEmpty(selector) || occurrence["index"] is not JsonValue index || !index.TryGetValue<int>(out var ordinal) || ordinal < 0 ||
                    !(occurrence["tag"]?.ToString() is "article" or "li" or "tr" || occurrence["role"]?.ToString() is "article" or "listitem" or "row") ||
                    occurrences.TryGetValue(ordinal, out var prior) && !JsonNode.DeepEquals(occurrence, prior) ||
                    selectors.TryGetValue(selector, out var previous) && previous != ordinal) return false;
                occurrences[ordinal] = occurrence; selectors[selector] = ordinal;
            }
        }
        return true;
    }

    private static Dictionary<string, string> ResolvedCardProductReferences(Snapshot source,
        IReadOnlyDictionary<string, string> productReferences, List<string> findings)
    {
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        var records = source.Records.OfType<JsonObject>().ToArray(); var examined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Where(r => productReferences.ContainsKey(r["reference"]?.ToString() ?? "")))
        {
            if (!examined.Add(record["reference"]!.ToString())) continue;
            var card = records.Where(r => SameCard(record, r) && r["kind"]?.ToString() == "link" && r["href"] is not null).ToArray();
            foreach (var link in card) examined.Add(link["reference"]!.ToString());
            var context = ContextAttribute(record);
            if (context?["value"]?.ToString() != "" || context["tag"]?.ToString() == "nav" || context["role"]?.ToString() == "navigation" ||
                card.Any(r => !productReferences.ContainsKey(r["reference"]!.ToString())) ||
                card.Count(r => r["heading"]?.ToString() is { Length: > 0 }) != 1 ||
                card.Select(r => productReferences[r["reference"]!.ToString()]).Distinct(StringComparer.Ordinal).Count() != 1)
            { findings.Add("source_product_resolution_context_unverified"); continue; }
            foreach (var link in card) accepted.Add(link["reference"]!.ToString(), productReferences[link["reference"]!.ToString()]);
        }
        return accepted;
    }

    private static void BindKnownCardReviews(IReadOnlyDictionary<string, KnownCardReview> known, ResolvedStoreSource state)
    {
        foreach (var (reference, card) in known) state.AliasEvidence.Add(new JsonObject
        {
            ["source_record"] = reference, ["proof_kind"] = "known_source_card_membership", ["product_identity"] = card.Identity,
            ["proof_source_records"] = new JsonArray(card.Entries.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
            ["proof_event_indices"] = new JsonArray(), ["http_call_created"] = false, ["business_visit"] = false
        });
    }
}

internal static class AmazonCardReviewPolicy
{
    internal const string Id = "amazon-observed-card-review-context-v7";
    internal const string Approval = "user-amazon-semantic-card-review-20261111";
    internal const string UrlPattern = AmazonReviewProofPolicy.UrlPattern;
    internal static bool Allows(string value) => AmazonReviewProofPolicy.Allows(value);
    internal static bool IsEntry(string value) => AmazonReviewProofPolicy.IsEntry(value);
    internal static bool IsSourceEntry(string value) => AmazonReviewProofPolicy.IsSourceEntry(value);
    internal const string CardReviewFacts = AmazonResolvedProductPolicy.SourceResolutionFacts + " Preserve every raw record and product occurrence. For empty-ASIN product proof, distinguish cards by their original observed semantic container and unchanged ASIN ancestor; require consistent selectors/ordinals, each navigable occurrence's own complete ordered product redirect proof, exactly one heading per card, one consistent identity and no unresolved sibling. A non-heading review anchor that is not itself a product occurrence may reference such completed proof only when its original href exactly equals a separately observed permitted href in that card plus #customerReviews. In an already identified nonempty DOM-ASIN card, an auxiliary review anchor may instead reference the validated source-card membership, even without an exact HTTP-prefix twin; require consistent original context and a permitted observed entry, and reject contradictory direct identities or cross-card borrowing. That membership is not a redirect receipt or HTTP equality claim. Never navigate, strip, decode or manufacture an alias URL. All permitted entry URLs remain exact original observations. Store proof cannot establish product identity. The host grants only the next exact source read and checks each redirect before contact. Close, new search or failed evidence expires authority. Retain every later first-N business visit in source order and extract its actual fields; write the real Excel and close even on failure. Missing evidence remains failure. ";
    internal const string PlanningFacts = "Authorized browser policy amazon-observed-card-review-context-v7: use the approved two-phase source resolution and later ordered business visits. " + CardReviewFacts +
        AmazonObservedRedirectPolicy.AcquisitionGuarantees + " " + AmazonObservedRedirectPolicy.GuaranteeBoundary + " Captured full-URL allowlist: " + UrlPattern;
}
