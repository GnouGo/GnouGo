using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static partial class LiveWorkflowOracles
{
    // Offline proposal only: deliberately absent from policy capture, host allowlists,
    // redirect-entry authorization and the full business oracle version switch.
    internal const string AmazonSourceResolutionProposalVersion = "amazon-source-resolution-v8-proposal";
    private static readonly Regex ProposedStoreRoute = new(
        @"\Ahttps://(?:www\.)?amazon\.fr/stores/page/[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}(?:\?[^#]+)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static bool ProposedSourceResolutionRoute(string value)
        => AmazonSponsoredHostPolicy.Allows(value) || ProposedStoreDestination(value);

    private static bool ProposedStoreDestination(string value)
        => !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && ProposedStoreRoute.IsMatch(value);

    // Resolves destinations, not occurrence equivalence or business completion.
    // The advertised r/target parameter and brand/collection labels are never proof.
    internal static JsonObject VerifyAmazonSourceResolutionProposal(IReadOnlyList<JsonObject> events, string query)
    {
        var findings = new List<string>();
        var resolutions = new JsonArray();
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        var first = events.FirstOrDefault();
        if (first is null || first["tool"]?.ToString() != "browser_get_content" || !WholeDocumentSearch(first) ||
            !complete.TryGetValue(first, out var source) || !ValidNavigationEvidence(first, AmazonProductContextPolicy.Id) ||
            !AmazonAddress(source.Url, out var actual) || actual.AbsolutePath != "/s" || SearchQuery(actual) != query ||
            !AmazonAddress(first["arguments"]?["url"]?.ToString(), out var requested) || requested.AbsolutePath != "/s" ||
            string.IsNullOrWhiteSpace(query) || SearchQuery(requested) != query)
            return ResolutionReport(["complete_owned_search_required"], resolutions);

        var sourceId = first["result"]?["observationSnapshot"]?["id"]?.ToString() ??
            first["result"]?["observationManifest"]?["id"]?.ToString() ?? first["result"]?["observation"]?["id"]?.ToString();
        ValidateObservedProductRoutes(source, findings, AmazonProductContextPolicy.Id);
        var candidates = new List<JsonObject>();
        for (var index = 0; index < source.Records.Count; index++)
        {
            var record = source.Records[index];
            if (sourceId is null || record?["reference"]?.ToString() != sourceId + ":record:" + index)
                findings.Add("search_record_provenance_inconsistent");
            if (record is not JsonObject link || link["kind"]?.ToString() != "link" ||
                !AmazonSponsoredHostPolicy.IsSourceEntry(link["href"]?.ToString() ?? "")) continue;
            // Every sponsored link without a proven source identity stays in scope,
            // including absent attributes and explicit navigation. Nothing is omitted.
            if (!ProductCode(ContextAttribute(link)?["value"]?.ToString())) candidates.Add(link);
        }
        if (candidates.Count == 0) findings.Add("no_unresolved_source_links");
        if (events.Count - 1 != candidates.Count) findings.Add("source_resolutions_incomplete");
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var item = new JsonObject { ["source_record"] = candidate["reference"]?.DeepClone(),
                ["destination_kind"] = "indeterminate", ["product_identity"] = null, ["occurrence_verified"] = false };
            resolutions.Add(item);
            if (index + 1 >= events.Count) continue;
            var read = events[index + 1];
            if (read["tool"]?.ToString() != "browser_get_content" ||
                read["arguments"]?["url"]?.ToString() != candidate["href"]?.ToString() ||
                !complete.TryGetValue(read, out var destination) || !ValidNavigationEvidence(read, ProposedSourceResolutionRoute))
            { findings.Add("source_resolution_provenance_unverified"); continue; }
            if (ObservedProductIdentity(destination.Url) is { } product)
            {
                item["destination_kind"] = "product"; item["product_identity"] = product;
                // Equal destination IDs do not deduplicate source occurrences or
                // establish which outer links are aliases of a child product.
            }
            else if (destination.Url is { } url && ProposedStoreDestination(url)) item["destination_kind"] = "store_navigation";
            else findings.Add("source_resolution_destination_indeterminate");
        }
        return ResolutionReport(findings, resolutions);
    }

    private static JsonObject ResolutionReport(List<string> findings, JsonArray resolutions) => new()
    {
        ["version"] = AmazonSourceResolutionProposalVersion, ["active"] = false,
        ["resolution_evidence_complete"] = findings.Count == 0,
        ["source_prerequisite_qualified"] = false, ["business_oracle_passed"] = false,
        ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["resolutions"] = resolutions
    };
}
