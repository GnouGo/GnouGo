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

    internal static JsonObject ProviderRequestPage(PlanningSession state, int offset, int count = 2)
    {
        var options = ProviderRequestOptions(state);
        if (offset < 0 || offset > 0 && offset >= options.Count)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The provider-action page is outside the current exact continuation directory.");
        return new JsonObject
        {
            ["version"] = ProviderRequestVersion(state), ["offset"] = offset, ["total"] = options.Count,
            ["items"] = new JsonArray(options.Skip(offset).Take(count).Select(option =>
            {
                var source = state.Discovery.Sources.Single(s => s.Id == option.SourceId);
                var description = NavigationDescription(source.Description);
                return (JsonNode)new JsonObject { ["sourceId"] = source.Id, ["cursor"] = option.Cursor,
                    ["canRequestPage"] = option.Available, ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount,
                    ["description"] = description, ["descriptionComplete"] = description.Length == source.Description.Length,
                    ["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0" };
            }).ToArray()),
            ["next"] = offset + count < options.Count ? ProviderRequestAddress(state, offset + count) : null
        };
    }

    private static JsonObject? ProviderEvidencePage(PlanningSession state) => ProviderEvidencePages(state).FirstOrDefault();

    private static IEnumerable<JsonObject> ProviderEvidencePages(PlanningSession state)
    {
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
            if (address.StartsWith("page:provider-requests:", StringComparison.Ordinal) && value is JsonObject page &&
                page["offset"] is JsonValue position && position.TryGetValue<int>(out var offset) && offset >= 0 &&
                address == ProviderRequestAddress(state, offset) && JsonNode.DeepEquals(page, ProviderRequestPage(state, offset))) yield return page;
    }

    internal static void RemoveStaleDiscoveryDirectories(PlanningSession state)
    {
        if (state.Construction!.Evidence is not { } evidence) return;
        foreach (var (address, value) in evidence.ToArray())
        {
            if (!address.StartsWith("page:provider-requests:", StringComparison.Ordinal) &&
                !address.StartsWith("page:operations:", StringComparison.Ordinal)) continue;
            JsonNode? current;
            try { LoadEvidence(state, address, out current); }
            catch (PlanningResponseException) { current = null; }
            // Only current action directories expire. Original requested prose,
            // schemas and requirements, and every consumed identity, are retained.
            if (!JsonNode.DeepEquals(value, current)) evidence.Remove(address);
        }
    }

    private static JsonObject? PresentedProviderRequests(PlanningSession state)
    {
        if (ProviderEvidencePage(state) is { } page) return page;
        if (DescribedProviderRequests(state) is { } described) return described;
        {
            // Inspecting one operation does not exhaust other providers for this
            // substep. Keep their exact bounded actions beside the original evidence.
            var firstAvailable = ProviderRequestOptions(state).FindIndex(option => option.Available);
            // The decision overview can expose more choices without changing
            // canonical evidence pages or selecting any provider automatically.
            return ProviderRequestPage(state, Math.Max(0, firstAvailable), 8);
        }
    }

    private static JsonObject? DecisionOperationPage(PlanningSession state)
    {
        var page = ContractDescriptorPage(state);
        // A consumed directory is not an inspected contract. Stateless decisions
        // still need bounded identities and exact description links.
        return page["items"]!.AsArray().Count == 0 || JsonNode.DeepEquals(state.Construction!.Evidence?[OperationPageAddress(state, 0)], page) ? null : page;
    }

    private static string? SourceDirectoryContinuation(PlanningSession state) =>
        UnconsumedDirectory(state, "page:sources:", state.Discovery.Sources.Count, SourceDirectoryPageSize);

    private static string? ProviderDirectoryContinuation(PlanningSession state) =>
        UnconsumedDirectory(state, "page:provider-requests:" + ProviderRequestVersion(state) + ":",
                ProviderRequestOptions(state).Count, 2);

    private static string? UnconsumedDirectory(PlanningSession state, string prefix, int count, int pageSize)
    {
        for (var offset = 0; offset < count; offset += pageSize)
        {
            var address = prefix + offset.ToString(CultureInfo.InvariantCulture);
            if (state.Construction!.Evidence?.ContainsKey(address) != true && !state.Construction.EvidenceRequests.Contains(
                PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal)) return address;
        }
        return null;
    }

    private static string? OperationDirectoryContinuation(PlanningSession state, JsonObject? displayed)
    {
        return UnseenOperationPage(state, displayed) is { } page
            ? OperationPageAddress(state, page["offset"]!.GetValue<int>()) : null;
    }

    private static JsonObject? UnseenOperationPage(PlanningSession state, JsonObject? displayed = null)
    {
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
        if (continuation != OperationDirectoryContinuation(state, page) ||
            page["items"]!.AsArray().Count == 0) return;
        var key = PlanningConstruction.EvidenceBatchFingerprint(state, [OperationPageAddress(state, offset)]);
        if (!state.Construction!.EvidenceRequests.Contains(key, StringComparer.Ordinal))
            state.Construction.EvidenceRequests.Add(key);
    }

    private static bool LoadOperationPage(PlanningSession state, string address, out JsonNode? value, JsonNode? issuedSchema = null)
    {
        value = null;
        if (!address.StartsWith("page:operations:", StringComparison.Ordinal)) return false;
        if (State(state).CurrentStep is null)
        {
            value = LoadKnownOperationPage(state, address);
            return true;
        }
        var parts = address.Split(':');
        if (parts is ["page", "operations", _, var position] &&
            int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
        {
            // Pending requests may have issued a directory over all retained queries.
            // Its exact address must remain issued authority; a valid old hash alone is insufficient.
            {
                var issued = issuedSchema?["properties"]?["evidenceRequests"]?["anyOf"] is JsonArray alternatives &&
                    alternatives.Any(option => option?["type"]?.ToString() == "array" &&
                        option["items"]?["enum"] is JsonArray addresses && addresses.Any(item => item?.ToString() == address));
                var supplied = state.Construction!.Evidence?[address];
                var read = supplied is JsonObject && state.Construction.EvidenceRequests.Contains(
                    PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal);
                if (issued || read)
                {
                    var original = ContractDescriptorPage(state, offset, retained: true);
                    if ((issued || JsonNode.DeepEquals(supplied, original)) &&
                        address == "page:operations:" + PlanningGraphCompiler.Fingerprint(original.ToJsonString()) + ":" + offset.ToString(CultureInfo.InvariantCulture))
                    { value = original; return true; }
                }
            }
            if (address == OperationPageAddress(state, offset)) value = ContractDescriptorPage(state, offset);
        }
        return true;
    }

    internal static void ValidateOperationPageAuthority(JsonNode issuedSchema, JsonNode response)
    {
        if ((issuedSchema["description"]?.ToString() != ContentPageSchemaDescription && IssuedDescriptionKnowledge(issuedSchema) is null) ||
            response["evidenceRequests"] is not JsonArray requests) return;
        if (requests.Any(request => request?.ToString().Split(':') is ["page", "operations", _]))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests",
                "Use the exact content-addressed operation page issued by this request; an offset-only alias grants no inspection.");
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

    private static JsonObject? DiscoveryEvidence(PlanningSession state)
    {
        var evidence = state.Construction!.Evidence?.DeepClone().AsObject();
        if (evidence is null) return evidence;
        var prerequisiteAddresses = CurrentPrerequisiteAddresses(state);
        var schema = new JsonObject { ["description"] = DescriptionKnowledgePrefix +
            DescriptionKnowledgeHash(DescriptionKnowledge(state, state.Discovery.Pages.Count)) };
        foreach (var (address, original) in evidence.ToArray())
        {
            if (!address.StartsWith("description:", StringComparison.Ordinal) || original is not JsonObject page ||
                prerequisiteAddresses.Contains(address) || !state.Construction.EvidenceRequests.Contains(
                    PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal)) continue;
            var response = new JsonObject { ["evidenceRequests"] = new JsonArray(address) };
            if (DescriptionReadKey(state, schema, response) is not { } readKey ||
                state.Construction.EvidenceRequests.Contains(readKey, StringComparer.Ordinal) ||
                State(state).FailedActions.Any(action => action.EndsWith(":" + readKey, StringComparison.Ordinal)) ||
                !JsonNode.DeepEquals(original, LoadContractEvidence(state, address, recordSchemaSelection: false))) continue;
            // A prior provider-knowledge view is only omitted when the existing
            // strict-growth guard permits its exact original to be read again.
            // Keep the durable original and all successful read identities intact.
            page.Remove("text");
            page["textOmitted"] = true;
            page["evidence"] = address;
        }
        FactorDiscoveryVersions(state, evidence);
        return evidence;
    }

    private static void FactorDiscoveryVersions(PlanningSession state, JsonObject presentation)
    {
        foreach (var (address, original) in state.Construction!.Evidence!)
        {
            if (original is not JsonObject page || presentation[address] is not JsonObject view) continue;
            if (address.StartsWith("description:", StringComparison.Ordinal) || address.StartsWith("source-description:", StringComparison.Ordinal))
            {
                // The exact address already carries the version. Compare with
                // the canonical original, not a potentially text-omitted view.
                try
                {
                    if (JsonNode.DeepEquals(page, LoadContractEvidence(state, address, recordSchemaSelection: false)))
                        view.Remove("version");
                }
                catch (PlanningResponseException) { /* Unverified evidence keeps its original presentation. */ }
            }
            else if (address.StartsWith("page:operations:", StringComparison.Ordinal) &&
                page["offset"] is JsonValue offsetNode && offsetNode.TryGetValue<int>(out var offset) && offset >= 0 &&
                address == "page:operations:" + PlanningGraphCompiler.Fingerprint(page.ToJsonString()) + ":" + offset.ToString(CultureInfo.InvariantCulture) &&
                view["items"] is JsonArray items)
            {
                // Retained directory hashes bind their original rows even if
                // later discovery changes ranking. Do not rebuild that page.
                foreach (var item in items.OfType<JsonObject>())
                    if (item["id"] is JsonValue idNode && idNode.TryGetValue<string>(out var id) &&
                        item["version"] is JsonValue versionNode && versionNode.TryGetValue<string>(out var version) &&
                        item["descriptionEvidence"]?.ToString() == "description:" + ContractIdentity(id, version) + ":0")
                        item.Remove("version");
            }
        }
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
            new[] { "operationIds", "binding", "discoveryRequests", "reselectStep", "clarifications" }.Any(name => response[name] is not null) ||
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
        if (previous is null) return null; // Missing issued knowledge grants no reread authority.
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

    private static JsonObject? DescribedProviderRequests(PlanningSession state) => DescribedProviderRequestPages(state).FirstOrDefault();

    private static IEnumerable<JsonObject> DescribedProviderRequestPages(PlanningSession state)
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
            yield return new JsonObject
            {
                ["version"] = ProviderRequestVersion(state), ["sourceId"] = sourceId, ["total"] = selected.Length,
                ["items"] = new JsonArray(selected.Take(2).Select(option => (JsonNode)new JsonObject
                {
                    ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, ["canRequestPage"] = option.Available,
                    ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount
                }).ToArray()),
                ["next"] = selected.Length > 2 ? ProviderRequestAddress(state, offset + 2) : null
            };
        }
    }

    private static bool DiscoveryCloseCanProgress(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        // A close binding adds no task. Reuse the existing draft/Apply progress
        // choices; nested scopes may export even when a child is still open.
        return step.Scope != "/root" || step.OutputNames.Count > 0 || ClosurePresentation(state).CloseScope ||
            step.CloseRequirementIds.Any(id => state.Construction!.Requirements.Any(r => r.Id == id && r.Status != "closed"));
    }

    private static JsonObject CompactDiscoverySchema(PlanningSession state)
    {
        var requests = PresentedProviderRequests(state)?["items"]?.AsArray() ?? [];
        IEnumerable<JsonNode?> visible = requests;
        {
            var supplied = ProviderEvidencePages(state).Concat(DescribedProviderRequestPages(state))
                .SelectMany(page => page["items"]!.AsArray())
                .DistinctBy(item => (item!["sourceId"]!.ToString(), item["cursor"]?.ToString())).ToArray();
            if (supplied.Length > 0) visible = supplied;
        }
        var available = visible.Where(request => (request!["canRequestPage"] ?? request["available"])!.GetValue<bool>()).ToArray();
        // Factor only identical cursor branches. The accepted source/cursor pairs
        // stay exact; grouping every source with every cursor would widen authority.
        var choices = available.GroupBy(request => request!["cursor"]?.ToString(), StringComparer.Ordinal)
                .Select(group => (Sources: group.Select(request => request!["sourceId"]!.ToString()).ToArray(), Cursor: group.Key));
        var alternatives = new JsonArray(choices.Select(choice => (JsonNode)PlanningSchemas.Object(
            ("sourceId", PlanningSchemas.Enum(choice.Sources)),
            ("cursor", choice.Cursor is { } cursor ? PlanningSchemas.Enum(cursor) : PlanningSchemas.Type("null")),
            ("query", PlanningSchemas.Type("null")), ("operationIds", PlanningSchemas.Type("null")))).ToArray());
        var schema = PlanningSchemas.Object(
            ("operationIds", PlanningSchemas.Nullable(Strings(1, 2))),
            ("discoveryRequests", alternatives.Count == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(
                PlanningSchemas.Array(alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives }, 1, 2))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1,
                8))),
            ("reselectStep", CanReselectDiscovery(state) ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        {
            schema["properties"]!.AsObject().Remove("operationIds");
            schema["required"]!.AsArray().RemoveAt(0);
            var binding = new JsonObject { ["anyOf"] = new JsonArray(
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("operation")),
                        ("operationIds", Strings(1, 2)), ("groupId", PlanningSchemas.Type("null"))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("call")),
                        ("operationIds", Strings(0, 0)), ("groupId", Text(96))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum(TaskKinds.Where(kind => kind is not ("operation" or "call") &&
                        (kind != "close" || DiscoveryCloseCanProgress(state))).ToArray())),
                        ("operationIds", Strings(0, 0)), ("groupId", PlanningSchemas.Type("null")))) };
            {
                var branches = binding["anyOf"]!.AsArray();
                branches[2]!["properties"]!["kind"] = PlanningSchemas.Enum(TaskKinds.Where(kind => kind is not ("operation" or "call" or "close")).ToArray());
                foreach (var branch in branches.OfType<JsonObject>())
                {
                    branch["properties"]!["closeScopes"] = PlanningSchemas.Type("null");
                    branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("closeScopes"));
                }
                if (DiscoveryCloseCanProgress(state))
                {
                    var selected = State(state).CurrentStep!;
                    branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("close")), ("operationIds", EmptyArray()),
                        ("groupId", PlanningSchemas.Type("null")), ("closeScopes", ClosurePresentation(state).CloseScope
                            ? PlanningSchemas.Array(PlanningSchemas.Enum(selected.Scope), 0, 1) : EmptyArray())));
                }
            }
            schema["properties"]!["binding"] = PlanningSchemas.Nullable(binding);
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("binding"));
        }
        schema["description"] = DescriptionKnowledgePrefix + DescriptionKnowledgeHash(DescriptionKnowledge(state, state.Discovery.Pages.Count));
        ConstrainDiscoveryEvidence(state, DiscoveryContext(state), schema);
        ConstrainReselectionSchema(state, schema);
        return schema;
    }

    internal static bool CanReselectDiscovery(PlanningSession state)
    {
        var construction = state.Construction!; var step = State(state).CurrentStep;
        if (step is null || step.DiscoveryComplete ||
            (step.Kind != "operation" && !(step.Kind is null)) || construction.Candidate is not null ||
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
