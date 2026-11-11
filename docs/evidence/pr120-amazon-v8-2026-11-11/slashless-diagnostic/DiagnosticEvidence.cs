using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// One-off diagnostic scope, deliberately absent from workflow policy/oracle capture.
internal sealed class DiagnosticEvidence(Assembly assembly)
{
    internal const string Pattern = @"https://sponsored-ads\.amazon\.fr/clk\?[^#\s]+";
    internal const string Search = "https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045";
    private const string Query = "chaussure geox homme 45";
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Regex EntryPattern = new(@"\A(?:" + Pattern + @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private readonly Type oracle = assembly.GetType("LiveWorkflowOracles", true)!;
    private readonly Type policy = assembly.GetType("AmazonResolvedStorePolicy", true)!;
    internal static bool Entry(string value) => !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && EntryPattern.IsMatch(value);
    internal bool OriginalRoute(string value) => (bool)policy.GetMethod("Allows", Flags)!.Invoke(null, [value])!;
    internal bool Route(string value) => OriginalRoute(value) || Entry(value);
    private bool Store(string value) => (bool)policy.GetMethod("IsStore", Flags)!.Invoke(null, [value])!;
    private string? Product(string value) => (string?)oracle.GetMethod("ObservedProductIdentity", Flags)!.Invoke(null, [value]);
    private bool Navigation(JsonObject read) => (bool)oracle.GetMethod("ValidNavigationEvidence", Flags, [typeof(JsonObject), typeof(Func<string, bool>)])!.Invoke(null, [read, (Func<string, bool>)Route])!;
    private static bool SearchUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/s";
    private bool QueryMatches(string? value) => value is not null && OriginalRoute(value) && SearchUrl(value) &&
        (string?)oracle.GetMethod("SearchQuery", Flags)!.Invoke(null, [new Uri(value)]) == Query;

    private IReadOnlyList<JsonNode?> Records(JsonObject read)
    {
        var snapshots = ((IEnumerable)oracle.GetMethod("CompleteSnapshots", Flags)!.Invoke(null, [new List<JsonObject> { read }])!).Cast<object>().ToArray();
        if (snapshots.Length != 1 || read["result"]?["observationSnapshot"] is null)
            throw new InvalidOperationException("A complete self-contained observation is required.");
        return (IReadOnlyList<JsonNode?>)snapshots[0].GetType().GetProperty("Records")!.GetValue(snapshots[0])!;
    }

    internal List<JsonObject> Candidates(JsonObject source)
    {
        if (source["tool"]?.ToString() != "browser_get_content" || source["arguments"]?["url"]?.ToString() != Search ||
            source["arguments"]?["selector"]?.ToString() != "body" || !Navigation(source) ||
            !QueryMatches(source["result"]?["url"]?.ToString()) || !QueryMatches(source["result"]?["navigationEvidence"]?["finalUrl"]?.ToString()))
            throw new InvalidOperationException("The exact complete fresh search is required.");
        var records = Records(source); var id = source["result"]!["observationSnapshot"]!["id"]!.ToString();
        List<JsonObject> candidates = [];
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record?["reference"]?.ToString() != id + ":record:" + index)
                throw new InvalidOperationException("Source record provenance is inconsistent.");
            if (record is JsonObject link && link["kind"]?.ToString() == "link" && Entry(link["href"]?.ToString() ?? "")) candidates.Add(link);
        }
        return candidates;
    }

    internal void RequireNext(IReadOnlyList<JsonObject> events, string url)
    {
        if (events.Count == 0)
        {
            if (url != Search) throw new InvalidOperationException("Only the initial exact search is authorized.");
            return;
        }
        var candidates = Candidates(events[0]);
        for (var index = 1; index < events.Count; index++)
        {
            if (index > candidates.Count || events[index]["arguments"]?["url"]?.ToString() != candidates[index - 1]["href"]?.ToString())
                throw new InvalidOperationException("Ordered source authority expired.");
            RequireResolution(events[index]);
        }
        if (events.Count > candidates.Count || candidates[events.Count - 1]["href"]?.ToString() != url)
            throw new InvalidOperationException("Only the next exact source occurrence is authorized.");
    }

    internal void RequireResolution(JsonObject read)
    {
        if (read["tool"]?.ToString() != "browser_get_content" || !Entry(read["arguments"]?["url"]?.ToString() ?? "") || !Navigation(read))
            throw new InvalidOperationException("Incomplete or unauthorized navigation; source authority expired.");
        _ = Records(read);
        if (SearchUrl(read["result"]?["url"]?.ToString()) || read["result"]?["navigationEvidence"]?["hops"] is not JsonArray hops || hops.Any(h => SearchUrl(h?["url"]?.ToString())))
            throw new InvalidOperationException("A search redirect expired source authority.");
    }

    internal JsonObject Verify(IReadOnlyList<JsonObject> events)
    {
        List<string> findings = []; var resolutions = new JsonArray();
        try
        {
            var candidates = Candidates(events[0]);
            if (candidates.Count == 0) findings.Add("no_slashless_source_links_observed");
            if (events.Count != candidates.Count + 1) findings.Add("source_resolutions_incomplete");
            for (var index = 0; index < candidates.Count; index++)
            {
                var item = new JsonObject { ["source_record"] = candidates[index]["reference"]!.DeepClone(), ["destination_kind"] = "indeterminate", ["product_identity"] = null, ["occurrence_verified"] = false };
                resolutions.Add(item);
                if (events.Count <= index + 1) continue;
                var read = events[index + 1];
                RequireNext(events.Take(index + 1).ToArray(), read["arguments"]?["url"]?.ToString() ?? "");
                RequireResolution(read);
                var actual = read["result"]!["navigationEvidence"]!["finalUrl"]!.ToString(); var document = read["result"]!["url"]!.ToString();
                if (Store(actual) && Store(document)) item["destination_kind"] = "store_navigation";
                else if (Product(actual) is { } product && Product(document) == product) { item["destination_kind"] = "product"; item["product_identity"] = product; }
                else findings.Add("destination_indeterminate");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or TargetInvocationException or ArgumentOutOfRangeException)
        { findings.Add("source_or_resolution_provenance_unverified"); }
        return new JsonObject { ["version"] = "amazon-slashless-source-diagnostic-v1", ["source_only"] = true,
            ["resolution_evidence_complete"] = findings.Count == 0, ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
            ["workflow_policy_modified"] = false, ["model_calls"] = 0,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()), ["resolutions"] = resolutions };
    }

    // Read-only association facts, not a source classifier or occurrence count.
    // The caller must separately justify any later workflow proof/order amendment.
    internal JsonObject ProductAssociationFacts(IReadOnlyList<JsonObject> events)
    {
        var resolution = Verify(events); var groups = new JsonArray();
        if (resolution["resolution_evidence_complete"]!.GetValue<bool>())
        {
            var candidates = Candidates(events[0]); var records = Records(events[0]);
            foreach (var group in candidates.GroupBy(r => r["ancestorAttributes"]?["data-asin"]?["index"]?.ToString()))
            {
                var links = group.ToArray(); var context = links[0]["ancestorAttributes"]?["data-asin"] as JsonObject;
                var completeContext = context?["value"]?.ToString() == "" && context["index"] is JsonValue number && number.TryGetValue<int>(out var ordinal) && ordinal >= 0 &&
                    context["tag"]?.ToString() is { Length: > 0 } && (context["role"] is null || context["role"] is JsonValue role && role.TryGetValue<string>(out _)) &&
                    links.All(r => JsonNode.DeepEquals(context, r["ancestorAttributes"]?["data-asin"]) && r["occurrence"] is null) &&
                    records.Count(r => r?["ancestorAttributes"]?["data-asin"]?["index"]?.ToString() == group.Key) == links.Length;
                var observed = links.Select(r => resolution["resolutions"]!.AsArray().Single(n => n!["source_record"]!.ToString() == r["reference"]!.ToString())!).ToArray();
                var identities = observed.Select(r => r["product_identity"]?.ToString()).Distinct(StringComparer.Ordinal).ToArray();
                var oneHeading = links.Count(r => r["heading"]?.ToString() is { Length: > 0 }) == 1;
                groups.Add(new JsonObject { ["context_index"] = group.Key, ["all_context_links_resolved"] = completeContext,
                    ["one_heading_link"] = oneHeading,
                    ["same_independent_product_identity"] = identities.Length == 1 && identities[0] is not null && observed.All(r => r["destination_kind"]?.ToString() == "product"),
                    ["source_references"] = new JsonArray(links.Select(r => r["reference"]!.DeepClone()).ToArray()),
                    ["product_identity"] = identities.Length == 1 ? identities[0] : null });
            }
        }
        return new JsonObject { ["kind"] = "read_only_product_association_facts", ["groups"] = groups,
            ["association_evidence_complete"] = groups.Count > 0 && groups.All(g => g!["all_context_links_resolved"]!.GetValue<bool>() &&
                g["one_heading_link"]!.GetValue<bool>() && g["same_independent_product_identity"]!.GetValue<bool>()),
            ["product_occurrence_claimed"] = false, ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
            ["workflow_policy_modified"] = false };
    }
}
