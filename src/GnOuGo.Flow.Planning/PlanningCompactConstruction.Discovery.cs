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
                    [ProviderPageActionName(state)] = option.Available, ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount,
                    ["description"] = description, ["descriptionComplete"] = description.Length == source.Description.Length,
                    ["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0" };
            }).ToArray()),
            ["next"] = offset + count < options.Count ? ProviderRequestAddress(state, offset + count) : null
        };
    }

    private static string ProviderPageActionName(PlanningSession state) =>
        PlanningConstructionPhasePolicies.IsFresh(state.Request) ? "canRequestPage" : "available";

    private static bool ProviderPageMatches(PlanningSession state, JsonNode? value, JsonNode? current)
    {
        if (JsonNode.DeepEquals(value, current)) return true;
        if (!PlanningConstructionPhasePolicies.IsFresh(state.Request) || current is not JsonObject page ||
            page["items"] is not JsonArray) return false;
        // Retained pages keep their exact bytes and content address. Only the
        // presentation label changed; every original field must still match.
        var retainedShape = page.DeepClone();
        foreach (var item in retainedShape["items"]!.AsArray())
        {
            var row = item!.AsObject();
            if (!row.Remove("canRequestPage", out var action)) return false;
            row["available"] = action;
        }
        return JsonNode.DeepEquals(value, retainedShape);
    }

    private static JsonObject? ProviderEvidencePage(PlanningSession state) => ProviderEvidencePages(state).FirstOrDefault();

    private static IEnumerable<JsonObject> ProviderEvidencePages(PlanningSession state)
    {
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
            if (address.StartsWith("page:provider-requests:", StringComparison.Ordinal) && value is JsonObject page &&
                page["offset"] is JsonValue position && position.TryGetValue<int>(out var offset) && offset >= 0 &&
                address == ProviderRequestAddress(state, offset) && ProviderPageMatches(state, page, ProviderRequestPage(state, offset))) yield return page;
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
            if (!(address.StartsWith("page:provider-requests:", StringComparison.Ordinal)
                    ? ProviderPageMatches(state, value, current) : JsonNode.DeepEquals(value, current))) evidence.Remove(address);
        }
    }

    private static JsonObject? PresentedProviderRequests(PlanningSession state)
    {
        if (ProviderEvidencePage(state) is { } page) return page;
        if (IsCompactPresentation(state) && DescribedProviderRequests(state) is { } described) return described;
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request))
        {
            // Inspecting one operation does not exhaust other providers for this
            // substep. Keep their exact bounded actions beside the original evidence.
            var firstAvailable = ProviderRequestOptions(state).FindIndex(option => option.Available);
            // The decision overview can expose more choices without changing
            // canonical evidence pages or selecting any provider automatically.
            return ProviderRequestPage(state, Math.Max(0, firstAvailable), UsesDeferredBinding(state) ? 8 : 2);
        }
        if (state.Construction!.Evidence is { Count: > 0 } || ContractDescriptors(state).Count > 0) return null;
        return ProviderRequestPage(state, 0);
    }

    private static JsonObject? DecisionOperationPage(PlanningSession state)
    {
        if (state.Construction!.Evidence is { Count: > 0 } && !UsesDeferredBinding(state)) return null;
        if (!IsCompactPresentation(state)) return UnseenOperationPage(state);
        var page = ContractDescriptorPage(state);
        // A consumed directory is not an inspected contract. Stateless decisions
        // still need bounded identities and exact description links.
        return page["items"]!.AsArray().Count == 0 || JsonNode.DeepEquals(state.Construction.Evidence?[OperationPageAddress(state, 0)], page) ? null : page;
    }

    private static string? SourceDirectoryContinuation(PlanningSession state) =>
        !IsCompactPresentation(state) ? "page:sources:0" :
            UnconsumedDirectory(state, "page:sources:", state.Discovery.Sources.Count, SourceDirectoryPageSize);

    private static string? ProviderDirectoryContinuation(PlanningSession state) =>
        !IsCompactPresentation(state) ? ProviderRequestAddress(state, 0) :
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
        if (!IsCompactPresentation(state)) return "page:operations:0";
        return UnseenOperationPage(state, displayed) is { } page
            ? OperationPageAddress(state, page["offset"]!.GetValue<int>()) : null;
    }

    private static JsonObject? UnseenOperationPage(PlanningSession state, JsonObject? displayed = null)
    {
        if (!IsCompactPresentation(state)) return ContractDescriptorPage(state);
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
        if (!IsCompactPresentation(state)) return;
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
        if (IsCompactPresentation(state) && parts is ["page", "operations", _, var position] &&
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

    private static JsonObject? DiscoveryEvidence(PlanningSession state)
    {
        var evidence = state.Construction!.Evidence?.DeepClone().AsObject();
        if (evidence is null || !UsesDeferredBinding(state) || !PlanningConstructionPhasePolicies.IsFresh(state.Request)) return evidence;
        var prerequisiteAddresses = State(state).CurrentStep?.Prerequisites?.SelectMany(item => item.EvidenceAddresses).ToHashSet(StringComparer.Ordinal) ?? [];
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
        if (UsesVerifiedReview(state))
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
                    ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, [ProviderPageActionName(state)] = option.Available,
                    ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount
                }).ToArray()),
                ["next"] = selected.Length > 2 ? ProviderRequestAddress(state, offset + 2) : null
            };
        }
    }

    private static JsonObject CompactDiscoverySchema(PlanningSession state)
    {
        var requests = PresentedProviderRequests(state)?["items"]?.AsArray() ?? [];
        IEnumerable<JsonNode?> visible = requests;
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request))
        {
            var supplied = ProviderEvidencePages(state).Concat(DescribedProviderRequestPages(state))
                .SelectMany(page => page["items"]!.AsArray())
                .DistinctBy(item => (item!["sourceId"]!.ToString(), item["cursor"]?.ToString())).ToArray();
            if (supplied.Length > 0) visible = supplied;
        }
        var available = visible.Where(request => (request!["canRequestPage"] ?? request["available"])!.GetValue<bool>()).ToArray();
        // Factor only identical cursor branches. The accepted source/cursor pairs
        // stay exact; grouping every source with every cursor would widen authority.
        var choices = IsCompactPresentation(state)
            ? available.GroupBy(request => request!["cursor"]?.ToString(), StringComparer.Ordinal)
                .Select(group => (Sources: group.Select(request => request!["sourceId"]!.ToString()).ToArray(), Cursor: group.Key))
            : available.Select(request => (Sources: new[] { request!["sourceId"]!.ToString() }, Cursor: request["cursor"]?.ToString()));
        var alternatives = new JsonArray(choices.Select(choice => (JsonNode)PlanningSchemas.Object(
            ("sourceId", PlanningSchemas.Enum(choice.Sources)),
            ("cursor", choice.Cursor is { } cursor ? PlanningSchemas.Enum(cursor) : PlanningSchemas.Type("null")),
            ("query", PlanningSchemas.Type("null")), ("operationIds", PlanningSchemas.Type("null")))).ToArray());
        var schema = PlanningSchemas.Object(
            ("operationIds", PlanningSchemas.Nullable(Strings(1, 2))),
            ("discoveryRequests", alternatives.Count == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(
                PlanningSchemas.Array(alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives }, 1, 2))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1,
                PlanningConstructionPhasePolicies.IsFresh(state.Request) ? 8 : IsCompactPresentation(state) ? 1 : 4))),
            ("reselectStep", CanReselectDiscovery(state) ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        if (UsesDeferredBinding(state))
        {
            schema["properties"]!.AsObject().Remove("operationIds");
            schema["required"]!.AsArray().RemoveAt(0);
            schema["properties"]!["binding"] = PlanningSchemas.Nullable(PlanningSchemas.Object(
                ("kind", PlanningSchemas.Enum(TaskKinds)), ("operationIds", Strings(0, 2)),
                ("groupId", PlanningSchemas.Nullable(Text(96)))));
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("binding"));
        }
        if (IsCompactPresentation(state))
            schema["description"] = DescriptionKnowledgePrefix + DescriptionKnowledgeHash(DescriptionKnowledge(state, state.Discovery.Pages.Count));
        if (UsesDeferredBinding(state) && PlanningConstructionPhasePolicies.IsFresh(state.Request))
            ConstrainDiscoveryEvidence(state, DiscoveryContext(state), schema);
        return schema;
    }

    internal static bool CanReselectDiscovery(PlanningSession state)
    {
        var construction = state.Construction!; var step = State(state).CurrentStep;
        if (construction.Version != 2 || step is null || step.DiscoveryComplete ||
            (step.Kind != "operation" && !(UsesDeferredBinding(state) && step.Kind is null)) || construction.Candidate is not null ||
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
