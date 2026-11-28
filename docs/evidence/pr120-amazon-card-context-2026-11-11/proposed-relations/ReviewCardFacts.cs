using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Inactive source relationships only. Nothing here supplies navigation targets,
// product redirect proof, source qualification, or a business-oracle verdict.
internal sealed class ReviewCardFacts(Assembly benchmark)
{
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly Type _oracle = benchmark.GetType("LiveWorkflowOracles", true)!;
    private readonly string _routes = (string)benchmark.GetType("AmazonReviewProofPolicy", true)!
        .GetField("UrlPattern", Methods)!.GetRawConstantValue()!;

    internal JsonObject Inspect(JsonObject source)
    {
        var snapshots = ((System.Collections.IEnumerable)_oracle.GetMethod("CompleteSnapshots", Methods)!
            .Invoke(null, [new List<JsonObject> { source }])!).Cast<object>().ToArray();
        if (snapshots.Length != 1) throw new InvalidOperationException("One complete original observation is required.");
        var originalRecords = (IReadOnlyList<JsonNode?>)snapshots[0].GetType().GetProperty("Records")!.GetValue(snapshots[0])!;
        if (originalRecords.Any(r => r is not JsonObject)) throw new InvalidOperationException("Every original record must remain present.");
        var records = originalRecords.Cast<JsonObject>().ToArray();
        var snapshot = source["result"]!["observationSnapshot"]!["id"]!.ToString();
        if (records.Where((r, i) => r["reference"]?.ToString() != snapshot + ":record:" + i).Any())
            throw new InvalidOperationException("Original record order and references must remain complete.");
        return Relations(records);
    }

    internal JsonObject Relations(IReadOnlyList<JsonObject> records)
    {
        var relations = new JsonArray();
        var consistentMetadata = Consistent(records, r => r["ancestorAttributes"]?["data-asin"]) &&
            Consistent(records, r => r["occurrence"]);
        foreach (var link in records.Where(r => SponsoredReview(r["href"]?.ToString())))
        {
            var context = link["ancestorAttributes"]?["data-asin"];
            var code = context?["value"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var card = records.Where(r => JsonNode.DeepEquals(context, r["ancestorAttributes"]?["data-asin"]) &&
                JsonNode.DeepEquals(link["occurrence"], r["occurrence"])).ToArray();
            var entries = card.Where(r => r["kind"]?.ToString() == "link" && Allowed(r["href"]?.ToString())).ToArray();
            var href = link["href"]!.ToString();
            var exact = entries.Where(r => href == r["href"]!.ToString() + "#customerReviews").ToArray();
            var auxiliary = link["kind"]?.ToString() == "link" && link["tag"]?.ToString() == "a" && link["heading"] is null &&
                link["role"]?.ToString() is not ("listitem" or "article" or "row") && InheritedOccurrence(link);
            var tag = context?["tag"] is JsonValue tagValue && tagValue.TryGetValue<string>(out var tagText) ? tagText : null;
            var validContext = context is JsonObject && tag is { Length: >= 1 and <= 64 } && char.IsAsciiLetter(tag[0]) &&
                tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
                (context["role"] is null || context["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out _)) &&
                context["index"] is JsonValue index && index.TryGetValue<int>(out var ordinal) && ordinal >= 0;
            var known = code is not null && Regex.IsMatch(code, "\\A[A-Z0-9]{10}\\z", RegexOptions.CultureInvariant);
            var identities = card.Select(r => DirectIdentity(r["href"]?.ToString())).OfType<string>().ToArray();
            var conflict = known && identities.Any(identity => identity != code);
            // Existing known-ASIN context selection does not require an h2:
            // ContextProducts falls back to that observed card's source links.
            var sourceEntry = entries.Length != 0;
            var kind = !consistentMetadata || !validContext || !auxiliary ? "unsupported_context_or_occurrence" :
                known && !conflict && sourceEntry ? "known_source_card_membership_proposal" :
                code == "" && tag != "nav" && context?["role"]?.ToString() != "navigation" &&
                    exact.Length != 0 && entries.Count(r => r["heading"] is not null) == 1 ?
                    "exact_empty_card_redirect_reference_proposal" : "unverified";
            relations.Add(new JsonObject
            {
                ["source_record"] = link["reference"]?.DeepClone(), ["proposed_relationship"] = kind,
                ["known_source_identity"] = known ? code : null, ["conflicting_direct_identity"] = conflict,
                ["inherited_semantic_occurrence"] = link["occurrence"] is not null,
                ["card_record_references"] = References(card), ["permitted_entry_references"] = References(entries),
                ["exact_http_sibling_references"] = References(exact),
                ["required_own_redirect_source_records"] = code == "" ? References(entries) : new JsonArray(),
                ["redirect_proof_verified"] = false, ["navigation_target_returned"] = false,
                ["http_call_created"] = false, ["business_visit"] = false
            });
        }
        return new JsonObject
        {
            ["version"] = "amazon-card-review-relations-proposal-v1", ["inactive"] = true,
            ["records_examined"] = records.Count, ["metadata_consistent"] = consistentMetadata, ["relations"] = relations,
            ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
            ["workflow_policy_modified"] = false, ["navigation_authority_added"] = false
        };
    }

    private bool Allowed(string? href) => href is not null && Regex.IsMatch(href, "\\A(?:" + _routes + ")\\z", RegexOptions.CultureInvariant);
    private bool SponsoredReview(string? href) => href is not null &&
        (href.StartsWith("https://sponsored-ads.amazon.fr/", StringComparison.Ordinal) ||
         href.StartsWith("https://www.amazon.fr/sspa/click?", StringComparison.Ordinal) ||
         href.StartsWith("https://amazon.fr/sspa/click?", StringComparison.Ordinal)) &&
        Regex.IsMatch(href, "\\A(?:" + _routes + ")#customerReviews\\z", RegexOptions.CultureInvariant);
    private string? DirectIdentity(string? href) => (string?)_oracle.GetMethod("ProductIdentity", Methods)!.Invoke(null, [href]);
    private static JsonArray References(IEnumerable<JsonObject> records) => new(records.Select(r => r["reference"]!.DeepClone()).ToArray());
    private static bool InheritedOccurrence(JsonObject link)
    {
        if (link["occurrence"] is null) return true;
        var occurrence = link["occurrence"]!;
        return link["selector"]?.ToString() is { Length: > 0 } own && occurrence["selector"]?.ToString() is { Length: > 0 } parent &&
            own != parent && (occurrence["tag"]?.ToString() is "article" or "li" or "tr" ||
                occurrence["role"]?.ToString() is "article" or "listitem" or "row") &&
            occurrence["index"] is JsonValue index && index.TryGetValue<int>(out var ordinal) && ordinal >= 0;
    }

    private static bool Consistent(IEnumerable<JsonObject> records, Func<JsonObject, JsonNode?> metadata)
    {
        var indices = new Dictionary<int, JsonNode>(); var selectors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (metadata(record) is not { } item) continue;
            if (item["index"] is not JsonValue index || !index.TryGetValue<int>(out var ordinal) || ordinal < 0) return false;
            if (indices.TryGetValue(ordinal, out var prior) && !JsonNode.DeepEquals(prior, item)) return false;
            indices[ordinal] = item;
            if (item["selector"]?.ToString() is { } selector)
            {
                if (selectors.TryGetValue(selector, out var previous) && previous != ordinal) return false;
                selectors[selector] = ordinal;
            }
        }
        return true;
    }
}
