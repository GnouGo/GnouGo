using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private const string ContentPageSchemaDescription = "Content-addressed operation pages only.";
    private const string DescriptionKnowledgePrefix = ContentPageSchemaDescription + " Description reads v1@";
    private sealed record ProviderRequest(string SourceId, string? Cursor, bool Available = true, string? Unavailable = null, int ResultCount = 0);

    private static List<ProviderRequest> ProviderRequestOptions(PlanningSession state)
    {
        var result = new List<ProviderRequest>();
        var objective = State(state).CurrentStep!.Objective;
        foreach (var source in state.Discovery.Sources)
        {
            var pages = state.Discovery.Pages.Where(p => p.SourceId == source.Id && p.Query == objective && p.ProducedArtifactKind is null).ToArray();
            if (pages.Length == 0) result.Add(new(source.Id, null));
            else
            {
                var continuations = pages.Where(p => p.NextCursor is not null && pages.All(next => next.Cursor != p.NextCursor))
                    .Select(p => new ProviderRequest(source.Id, p.NextCursor, ResultCount: pages.Sum(page => page.Capabilities.Count))).Distinct().ToArray();
                if (continuations.Length > 0) result.AddRange(continuations);
                else result.Add(new(source.Id, null, Available: false, Unavailable: pages[^1].UnavailableReason,
                    ResultCount: pages.Sum(page => page.Capabilities.Count)));
            }
        }
        return result;
    }

    private static string ProviderRequestVersion(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        State(state).CurrentStep!.Objective + "\n" + new JsonArray(ProviderRequestOptions(state).Select(option => (JsonNode)new JsonObject
            { ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, ["available"] = option.Available,
                ["sourceVersion"] = PlanningGraphCompiler.Fingerprint(state.Discovery.Sources.Single(s => s.Id == option.SourceId).Description),
                ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount }).ToArray()).ToJsonString());

    internal static string ProviderRequestAddress(PlanningSession state, int offset) =>
        "page:provider-requests:" + ProviderRequestVersion(state) + ":" + offset.ToString(CultureInfo.InvariantCulture);

    internal static JsonObject ProviderRequestPage(PlanningSession state, int offset)
    {
        var options = ProviderRequestOptions(state);
        if (offset < 0 || offset > 0 && offset >= options.Count)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The provider-action page is outside the current exact continuation directory.");
        return new JsonObject
        {
            ["version"] = ProviderRequestVersion(state), ["offset"] = offset, ["total"] = options.Count,
            ["items"] = new JsonArray(options.Skip(offset).Take(2).Select(option =>
            {
                var source = state.Discovery.Sources.Single(s => s.Id == option.SourceId);
                var description = NavigationDescription(source.Description);
                return (JsonNode)new JsonObject { ["sourceId"] = source.Id, ["cursor"] = option.Cursor,
                    ["available"] = option.Available, ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount,
                    ["description"] = description, ["descriptionComplete"] = description.Length == source.Description.Length,
                    ["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0" };
            }).ToArray()),
            ["next"] = offset + 2 < options.Count ? ProviderRequestAddress(state, offset + 2) : null
        };
    }

    private static JsonObject? ProviderEvidencePage(PlanningSession state)
    {
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
            if (address.StartsWith("page:provider-requests:", StringComparison.Ordinal) && value is JsonObject page &&
                page["offset"] is JsonValue position && position.TryGetValue<int>(out var offset) && offset >= 0 &&
                address == ProviderRequestAddress(state, offset) && JsonNode.DeepEquals(page, ProviderRequestPage(state, offset))) return page;
        return null;
    }

    private static JsonObject? PresentedProviderRequests(PlanningSession state)
    {
        if (ProviderEvidencePage(state) is { } page) return page;
        if (State(state).PresentationVersion == 2 && DescribedProviderRequests(state) is { } described) return described;
        if (state.Construction!.Evidence is { Count: > 0 } || ContractDescriptors(state).Count > 0) return null;
        return ProviderRequestPage(state, 0);
    }

    private static string? OperationDirectoryContinuation(PlanningSession state, JsonObject? displayed)
    {
        if (State(state).PresentationVersion != 2) return "page:operations:0";
        return UnseenOperationPage(state, displayed) is { } page
            ? OperationPageAddress(state, page["offset"]!.GetValue<int>()) : null;
    }

    private static JsonObject? UnseenOperationPage(PlanningSession state, JsonObject? displayed = null)
    {
        if (State(state).PresentationVersion != 2) return ContractDescriptorPage(state);
        for (var offset = 0; ; offset += CompactContractDescriptorPageSize)
        {
            var page = ContractDescriptorPage(state, offset);
            if (page["items"]!.AsArray().Count == 0) return null;
            var address = OperationPageAddress(state, offset);
            var supplied = state.Construction!.EvidenceRequests.Contains(
                PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal);
            if (!supplied && !JsonNode.DeepEquals(displayed, page) &&
                !JsonNode.DeepEquals(state.Construction.Evidence?[address], page)) return page;
            if (page["items"]!.AsArray().Count < CompactContractDescriptorPageSize) return null;
        }
    }

    internal static void RecordPresentedOperations(PlanningSession state, string issuedPrompt)
    {
        if (State(state).PresentationVersion != 2) return;
        JsonObject? context;
        try { context = JsonNode.Parse(issuedPrompt[(issuedPrompt.LastIndexOf('\n') + 1)..]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (context?["descriptors"] is not JsonObject page ||
            page["offset"] is not JsonValue position || !position.TryGetValue<int>(out var offset) || offset < 0 ||
            context["directoryEvidence"] is not JsonObject directory) return;
        JsonObject canonical;
        try { canonical = ContractDescriptorPage(state, offset); }
        catch (PlanningResponseException) { return; }
        if (!JsonNode.DeepEquals(page, canonical)) return;
        var continuation = directory["operations"]?.ToString();
        // Older issued requests advertise page zero again. Do not retrospectively
        // consume an address their captured presentation still offered to fetch.
        if (continuation == "page:operations:0" || continuation != OperationDirectoryContinuation(state, page) ||
            page["items"]!.AsArray().Count == 0) return;
        var key = PlanningConstruction.EvidenceBatchFingerprint(state, [OperationPageAddress(state, offset)]);
        if (!state.Construction!.EvidenceRequests.Contains(key, StringComparer.Ordinal))
            state.Construction.EvidenceRequests.Add(key);
    }

    private static bool LoadOperationPage(PlanningSession state, string address, out JsonNode? value)
    {
        value = null;
        if (!address.StartsWith("page:operations:", StringComparison.Ordinal)) return false;
        if (State(state).CurrentStep is null) return true;
        var parts = address.Split(':');
        if (parts is ["page", "operations", var originalOffset] &&
            int.TryParse(originalOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var legacyOffset) && legacyOffset >= 0)
        {
            // Offset-only addresses belong to retained issued requests. Preserve
            // their two-row payload and original continuation representation.
            value = ContractDescriptorPage(state, legacyOffset, legacy: true);
            return true;
        }
        if (State(state).PresentationVersion == 2 && parts is ["page", "operations", _, var position] &&
            int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0 &&
            address == OperationPageAddress(state, offset))
            value = ContractDescriptorPage(state, offset);
        return true;
    }

    internal static void ValidateOperationPageAuthority(JsonNode issuedSchema, JsonNode response)
    {
        if ((issuedSchema["description"]?.ToString() != ContentPageSchemaDescription && IssuedDescriptionKnowledge(issuedSchema) is null) ||
            response["evidenceRequests"] is not JsonArray requests) return;
        if (requests.Any(request => request?.ToString().Split(':') is ["page", "operations", _]))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests",
                "Use the exact content-addressed operation page issued by this request; a legacy offset-only alias does not grant another inspection.");
    }

    private static string? IssuedDescriptionKnowledge(JsonNode issuedSchema)
    {
        var description = issuedSchema["description"]?.ToString();
        if (description?.StartsWith(DescriptionKnowledgePrefix, StringComparison.Ordinal) != true) return null;
        var hash = description[DescriptionKnowledgePrefix.Length..];
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new WorkflowRuntimeException("CONSTRUCTION_DISCOVERY_STALE", "The issued description-read knowledge identity is invalid.");
        return hash;
    }

    // Only immutable, policy-allowed producer identities establish new knowledge.
    // Cursors, ranking, diagnostics, read status and duplicated pages do not.
    private static HashSet<string> DescriptionKnowledge(PlanningSession state, int pageCount) =>
        DescriptorCandidates(state, state.Discovery.Pages.Take(pageCount))
            .Select(c => new JsonArray(c.SourceId, c.Operation!.Id, c.Operation.Version).ToJsonString())
            .ToHashSet(StringComparer.Ordinal);

    private static string DescriptionKnowledgeHash(HashSet<string> knowledge) => PlanningGraphCompiler.Fingerprint(
        new JsonArray(knowledge.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()).ToJsonString());

    internal static string? DescriptionReadKey(PlanningSession state, JsonNode issuedSchema, JsonNode response)
    {
        var issued = IssuedDescriptionKnowledge(issuedSchema);
        if (issued is null) return null;
        var current = DescriptionKnowledge(state, state.Discovery.Pages.Count);
        if (issued != DescriptionKnowledgeHash(current))
            throw new WorkflowRuntimeException("CONSTRUCTION_DISCOVERY_STALE", "Retained discovery knowledge no longer matches the exact issued description-read request.");
        if (response["evidenceRequests"] is not JsonArray { Count: 1 } requests ||
            new[] { "operationIds", "discoveryRequests", "reselectStep", "clarifications" }.Any(name => response[name] is not null) ||
            requests[0]?.ToString() is not { } address || !address.StartsWith("description:", StringComparison.Ordinal) || address.Length > 2048)
            return null;
        try { if (LoadContractEvidence(state, address) is null) return null; }
        catch (PlanningResponseException) { return null; }
        var ordinary = PlanningConstruction.EvidenceBatchFingerprint(state, [address]);
        var prefix = "description-read:" + ordinary + ":";
        var key = prefix + issued;
        if (!state.Construction!.EvidenceRequests.Contains(ordinary, StringComparer.Ordinal) ||
            state.Construction.EvidenceRequests.Contains(key, StringComparer.Ordinal)) return key;
        var previous = state.Construction.EvidenceRequests.LastOrDefault(read => read.StartsWith(prefix, StringComparison.Ordinal));
        if (previous is null) return null; // A historical read retains its original repeat guard.
        var previousHash = previous[prefix.Length..];
        // Reconstruct the earlier identity set from retained immutable provider
        // receipts; only a strict append of knowledge permits another exact read.
        for (var count = 0; count <= state.Discovery.Pages.Count; count++)
        {
            var earlier = DescriptionKnowledge(state, count);
            if (DescriptionKnowledgeHash(earlier) == previousHash && earlier.IsProperSubsetOf(current)) return key;
        }
        return null;
    }

    private static JsonObject? DescribedProviderRequests(PlanningSession state)
    {
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
        {
            if (!address.StartsWith("source-description:", StringComparison.Ordinal) || value is not JsonObject) continue;
            JsonObject original;
            try { original = SourceDescription(state, address["source-description:".Length..]); }
            catch (PlanningResponseException) { continue; }
            if (!JsonNode.DeepEquals(value, original)) continue;
            var sourceId = original["id"]!.ToString();
            var options = ProviderRequestOptions(state);
            var offset = options.FindIndex(option => option.SourceId == sourceId);
            if (offset < 0) continue;
            var selected = options.Skip(offset).TakeWhile(option => option.SourceId == sourceId).ToArray();
            // The exact description remains the sole evidence view. Retain only
            // its current actionable identities, not another directory's prose.
            return new JsonObject
            {
                ["version"] = ProviderRequestVersion(state), ["sourceId"] = sourceId, ["total"] = selected.Length,
                ["items"] = new JsonArray(selected.Take(2).Select(option => (JsonNode)new JsonObject
                {
                    ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, ["available"] = option.Available,
                    ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount
                }).ToArray()),
                ["next"] = selected.Length > 2 ? ProviderRequestAddress(state, offset + 2) : null
            };
        }
        return null;
    }

    private static JsonObject CompactDiscoverySchema(PlanningSession state)
    {
        var requests = PresentedProviderRequests(state)?["items"]?.AsArray() ?? [];
        var alternatives = new JsonArray(requests.Where(request => request!["available"]!.GetValue<bool>()).Select(request => (JsonNode)PlanningSchemas.Object(
            ("sourceId", PlanningSchemas.Enum(request!["sourceId"]!.ToString())),
            ("cursor", request["cursor"] is { } cursor ? PlanningSchemas.Enum(cursor.ToString()) : PlanningSchemas.Type("null")),
            ("query", PlanningSchemas.Type("null")), ("operationIds", PlanningSchemas.Type("null")))).ToArray());
        var schema = PlanningSchemas.Object(
            ("operationIds", PlanningSchemas.Nullable(Strings(1, 2))),
            ("discoveryRequests", alternatives.Count == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(
                PlanningSchemas.Array(alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives }, 1, 2))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1, State(state).PresentationVersion == 2 ? 1 : 4))),
            ("reselectStep", CanReselectDiscovery(state) ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        if (State(state).PresentationVersion == 2)
            schema["description"] = DescriptionKnowledgePrefix + DescriptionKnowledgeHash(DescriptionKnowledge(state, state.Discovery.Pages.Count));
        return schema;
    }

    internal static bool CanReselectDiscovery(PlanningSession state)
    {
        var construction = state.Construction!; var step = State(state).CurrentStep;
        if (construction.Version != 2 || step is not { Kind: "operation", DiscoveryComplete: false } || construction.Candidate is not null ||
            state.EditablePaths is not null || state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            construction.DraftCorrections >= construction.Limits.MaxCorrectionsPerDraft) return false;
        var diagnosed = state.Diagnostics.Any(d => d.Required);
        if (!diagnosed && PlanningModelCalls.RemainingRepairs(state) == 0) return false;
        return diagnosed || state.Discovery.Sources.Count == 0 || ContractDescriptors(state).Count > 0 ||
            state.Discovery.Pages.Any(p => p.Query == step.Objective) || (construction.Evidence?.Select(pair => pair.Key).Any(key =>
                key.StartsWith("page:operations:", StringComparison.Ordinal) || key.StartsWith("page:sources:", StringComparison.Ordinal) ||
                key.StartsWith("description:", StringComparison.Ordinal) || key.StartsWith("source-description:", StringComparison.Ordinal)) ?? false);
    }

    private static bool LoadProviderRequestEvidence(PlanningSession state, string address, out JsonNode? value)
    {
        value = null;
        if (!address.StartsWith("page:provider-requests:", StringComparison.Ordinal)) return false;
        if (State(state).CurrentStep is null) return true;
        if (address.Split(':') is not ["page", "provider-requests", _, var cursor] ||
            !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0 ||
            address != ProviderRequestAddress(state, offset)) return true;
        value = ProviderRequestPage(state, offset);
        return true;
    }
}
