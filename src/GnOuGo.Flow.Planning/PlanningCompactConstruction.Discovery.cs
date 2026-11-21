using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private const string ContentPageSchemaDescription = "Content-addressed operation pages only.";
    private const string DescriptionKnowledgePrefix = ContentPageSchemaDescription + " Description reads v1@";
    private sealed record ProviderRequest(string SourceId, string? Cursor, bool Available = true, string? Unavailable = null, int ResultCount = 0, string? FocusId = null);

    private static List<ProviderRequest> ProviderRequestOptions(PlanningSession state)
    {
        var result = new List<ProviderRequest>();
        var focuses = PlanningDiscoveryFocus.Enabled(state) ? PlanningDiscoveryFocus.Choices(state)
            : new[] { new PlanningDiscoveryFocus.Focus("", State(state).CurrentStep!.Objective, "legacy") };
        foreach (var source in state.Discovery.Sources)
        {
            var completeCount = PlanningDiscoveryFocus.Enabled(state) ? PlanningDiscoveryFocus.CompleteDirectoryCount(state, source.Id) : null;
            foreach (var focus in focuses)
            {
                var objective = focus.Query;
                var focusId = PlanningDiscoveryFocus.Enabled(state) ? focus.Id : null;
                var failure = PlanningDiscoveryFocus.Failure(state, source.Id, objective, focusId);
                if (failure is not null) { result.Add(new(source.Id, null, false, failure.Message, FocusId: focusId)); continue; }
                if (completeCount is { } count) { result.Add(new(source.Id, null, false, ResultCount: count, FocusId: focusId)); continue; }
                var pages = PlanningDiscoveryFocus.Pages(state, source.Id, objective, focusId).ToArray();
                if (pages.Length == 0) result.Add(new(source.Id, null, FocusId: focusId));
                else
                {
                    var continuations = pages.Where(p => p.NextCursor is not null && pages.All(next => next.Cursor != p.NextCursor))
                        .Select(p => new ProviderRequest(source.Id, p.NextCursor, ResultCount: pages.Sum(page => page.Capabilities.Count), FocusId: focusId)).Distinct().ToArray();
                    if (continuations.Length > 0) result.AddRange(continuations);
                    else result.Add(new(source.Id, null, Available: false, Unavailable: pages[^1].UnavailableReason,
                        ResultCount: pages.Sum(page => page.Capabilities.Count), FocusId: focusId));
                }
            }
        }
        return result;
    }

    private static string ProviderRequestVersion(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        State(state).CurrentStep!.Objective + "\n" + new JsonArray(ProviderRequestOptions(state).Select(option =>
        {
            var item = new JsonObject { ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, ["available"] = option.Available,
                ["sourceVersion"] = PlanningGraphCompiler.Fingerprint(state.Discovery.Sources.Single(s => s.Id == option.SourceId).Description),
                ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount };
            if (option.FocusId is not null) item["focusId"] = option.FocusId;
            return (JsonNode)item;
        }).ToArray()).ToJsonString());

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
                var item = ProviderRequestIdentity(option);
                item["description"] = description;
                item["descriptionComplete"] = description.Length == source.Description.Length;
                item["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0";
                return item;
            }).ToArray()),
            ["next"] = offset + count < options.Count ? ProviderRequestAddress(state, offset + count) : null
        };
    }

    private static JsonObject ProviderRequestIdentity(ProviderRequest option)
    {
        var item = new JsonObject { ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor,
            ["canRequestPage"] = option.Available, ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount };
        if (option.FocusId is not null) item["focusId"] = option.FocusId;
        return item;
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
        // Keep the exact actions for later sources visible when sharing their
        // descriptions fits the entire menu within the ordinary overview size.
        const int overviewSize = 8;
        var options = ProviderRequestOptions(state);
        var offset = Math.Max(0, options.FindIndex(option => option.Available));
        var overview = ProviderRequestPage(state, offset, overviewSize);
        if (offset != 0 || !PlanningDiscoveryFocus.Enabled(state) || options.Count <= overviewSize || options.Count > 4 * overviewSize ||
            options.Select(option => option.SourceId).Distinct().Count() == options.Take(overviewSize).Select(option => option.SourceId).Distinct().Count())
            return overview;
        var complete = CompactProviderFocusMenu(ProviderRequestPage(state, 0, options.Count));
        return System.Text.Encoding.UTF8.GetByteCount(complete.ToJsonString()) <= System.Text.Encoding.UTF8.GetByteCount(overview.ToJsonString())
            ? complete : overview;
    }

    private static JsonObject CompactProviderFocusMenu(JsonObject page)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in page["items"]!.AsArray().OfType<JsonObject>())
        {
            if (!sources.Add(row["sourceId"]!.ToString()))
            {
                row.Remove("description");
                row.Remove("descriptionComplete");
                row.Remove("descriptionEvidence");
            }
            if (row["unavailable"] is null) row.Remove("unavailable");
            if (row["resultCount"]?.GetValue<int>() == 0) row.Remove("resultCount");
        }
        page["descriptionsBySourceId"] = true;
        page["defaults"] = new JsonObject { ["unavailable"] = null, ["resultCount"] = 0 };
        return page;
    }

    private static JsonObject? DecisionOperationPage(PlanningSession state)
    {
        var page = ContractDescriptorPage(state);
        // A consumed directory is not an inspected contract. Stateless decisions
        // still need bounded identities and exact description links.
        return page["items"]!.AsArray().Count == 0 || JsonNode.DeepEquals(state.Construction!.Evidence?[OperationPageAddress(state, 0)], page) ? null : page;
    }

    private static JsonArray? CompleteProviderMenu(PlanningSession state, JsonObject context)
    {
        if (context["providerRequests"] is not JsonObject page || page["items"] is not JsonArray { Count: > 0 } items ||
            page["total"]?.GetValue<int>() != items.Count || page["next"] is not null || page["offset"]?.GetValue<int>() != 0) return null;
        var canonical = ProviderRequestPage(state, 0, items.Count);
        if (page["descriptionsBySourceId"]?.GetValue<bool>() == true) canonical = CompactProviderFocusMenu(canonical);
        return JsonNode.DeepEquals(page, canonical) ? items : null;
    }

    private static JsonObject OmitDisplayedDirectoryReads(PlanningSession state, JsonObject context)
    {
        if (CompleteProviderMenu(state, context) is not { } items) return context;
        // Each current action is already on screen with its exact source description link.
        // Keep the original canonical pages available to previously issued requests.
        var displayed = context.DeepClone().AsObject();
        var directory = displayed["directoryEvidence"]!.AsObject();
        directory.Remove("providers");
        var sources = items.Select(item => item!["sourceId"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        if (state.Discovery.Sources.All(source => sources.Contains(source.Id))) directory.Remove("sources");
        return displayed;
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

    private static string? OperationDirectoryContinuation(PlanningSession state, JsonObject? displayed) =>
        UnseenOperationPages(state, displayed).Select(entry => entry.Address).FirstOrDefault();

    private static IEnumerable<(string Address, JsonObject Page)> UnseenOperationPages(PlanningSession state, JsonObject? displayed)
    {
        for (var offset = 0; ; offset += CompactContractDescriptorPageSize)
        {
            var page = ContractDescriptorPage(state, offset);
            var items = page["items"]!.AsArray();
            if (items.Count == 0) yield break;
            var address = OperationPageAddress(page);
            var supplied = state.Construction!.EvidenceRequests.Contains(
                PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal);
            if (!supplied && !JsonNode.DeepEquals(displayed, page) &&
                !JsonNode.DeepEquals(state.Construction.Evidence?[address], page)) yield return (address, page);
            if (items.Count < CompactContractDescriptorPageSize) yield break;
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
        CompactDiscoveryEvidence(state, evidence);
        return evidence;
    }

    private static void CompactDiscoveryEvidence(PlanningSession state, JsonObject presentation)
    {
        foreach (var (address, original) in state.Construction!.Evidence!)
        {
            if (original is not JsonObject page || presentation[address] is not JsonObject view) continue;
            if (address.StartsWith("contract:", StringComparison.Ordinal) && page["outputCount"]?.ToString() == "0" &&
                page["outputs"] is JsonArray { Count: 0 } && page["id"] is JsonValue contractId && page["version"] is JsonValue contractVersion &&
                page["outputDirectory"]?.ToString() == "port-page:" + ContractIdentity(contractId.ToString(), contractVersion.ToString()) + ":/output:0")
            {
                // The count and empty list already answer the named-port query.
                // Union or opaque result schemas remain reachable through their own directory.
                try
                {
                    if (ContractEvidenceCurrent(state, address, page) &&
                        TaskOperations.Describe(ExactContract(state, contractId.ToString(), contractVersion.ToString())).Outputs.Count == 0)
                        view.Remove("outputDirectory");
                }
                catch (PlanningResponseException) { /* Keep the issued link when the current contract cannot be verified. */ }
            }
            else if (address.StartsWith("description:", StringComparison.Ordinal) || address.StartsWith("source-description:", StringComparison.Ordinal))
            {
                // The exact address already carries the version. Compare with
                // the canonical original, not a potentially text-omitted view.
                try
                {
                    if (JsonNode.DeepEquals(page, LoadContractEvidence(state, address, recordSchemaSelection: false)))
                    {
                        if (address.StartsWith("description:", StringComparison.Ordinal) && view["text"] is JsonValue text)
                        {
                            var contract = "contract:" + ContractIdentity(page["id"]!.ToString(), page["version"]!.ToString());
                            var pointer = "/evidence/" + PlanningSchemaReferences.Escape(contract) + "/description";
                            var length = text.GetValue<string>().Length;
                            if (length > pointer.Length + 40 && presentation[contract]?["description"] is JsonValue full &&
                                PlanningGraphCompiler.Fingerprint(full.GetValue<string>()) == page["descriptionHash"]!.ToString() &&
                                ContractEvidenceCurrent(state, contract, presentation[contract]))
                            {
                                view.Remove("text");
                                view["textPresentedAt"] = pointer;
                                view["textLength"] = length;
                            }
                        }
                        view.Remove("version");
                    }
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
            requests[0]?.ToString() is not { } address || !address.StartsWith("description:", StringComparison.Ordinal) ||
            address.Length > 2048 && !PlanningTokenThresholds.OptimizationAdvisory(state.Request))
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
            var items = new JsonArray(selected.Take(2).Select(option => (JsonNode)ProviderRequestIdentity(option)).ToArray());
            if (selected.Length is > 2 and <= ContractDirectoryPageSize &&
                selected.All(option => option.FocusId is not null && option.Cursor is null))
            {
                var focuses = new JsonArray(selected.Select(option => (JsonNode)ProviderRequestIdentity(option)).ToArray());
                if (System.Text.Encoding.UTF8.GetByteCount(focuses.ToJsonString()) <= ContractDescriptionPageSize)
                    items = focuses;
            }
            yield return new JsonObject
            {
                ["version"] = ProviderRequestVersion(state), ["sourceId"] = sourceId, ["total"] = selected.Length,
                ["items"] = items,
                ["next"] = selected.Length > items.Count ? ProviderRequestAddress(state, offset + items.Count) : null
            };
        }
    }

    private static bool DiscoveryCloseCanProgress(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        // A close binding adds no task. Reuse the existing draft/Apply progress
        // choices; nested scopes may export even when a child is still open.
        return step.Scope != "/root" || step.OutputNames.Count > 0 || ClosurePresentation(state).CloseScope ||
            step.CloseRequirementIds.Any(id => CanClaimRequirementCompletion(state, id)) || CanClaimCurrentBlockCompletion(state);
    }

    // Metadata acquisition deliberately precedes executable binding. The same
    // visible/versioned directory and policy filters govern both paths.
    internal static CapabilitySummary[] InspectionCandidates(PlanningSession state, string? source = null) =>
        State(state).CurrentStep is { DiscoveryComplete: false }
            ? DescriptorCandidates(state, PlanningDiscoveryFocus.VisiblePages(state))
                .Where(c => c.Operation!.Version == c.Version && (source is null || c.SourceId == source) &&
                    state.Discovery.Sources.Any(s => s.Id == c.SourceId)).ToArray()
            : [];

    private static JsonObject? KnownDiscoveryContracts(PlanningSession state, JsonObject? descriptors, JsonObject? described)
    {
        // A descriptor on a retrieved continuation can bind the same exact cached
        // contract as a descriptor on the first page. Resolution still checks its version and contents.
        var pages = (state.Construction!.Evidence ?? []).Where(item => item.Key.StartsWith("page:operations:", StringComparison.Ordinal))
            .Select(item => item.Value).Prepend(descriptors).Append(described);
        var displayed = pages.OfType<JsonObject>().SelectMany(page => page["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => row["id"] is not null && row["version"] is not null)
            .Select(row => (row["id"]!.ToString(), row["version"]!.ToString())).ToHashSet();
        if (displayed.Count == 0) return null;
        var candidates = InspectionCandidates(state).Where(c => displayed.Contains((c.Operation!.Id, c.Version)))
            .ToDictionary(c => (c.Operation!.Id, c.Version));
        var focus = PlanningDiscoveryFocus.Enabled(state) ? PlanningDiscoveryFocus.Choices(state)[0].Id : null;
        var matches = new JsonArray();
        foreach (var contract in KnownOperationContracts(state))
        {
            var id = TaskOperations.Describe(contract).Id;
            if (!candidates.TryGetValue((id, contract.Version), out var candidate) || !PlanningDiscoveryContext.MatchesDescriptor(contract, candidate) ||
                PlanningDiscoveryFocus.Failure(state, candidate.SourceId, PlanningDiscoveryFocus.Query(state, focus), focus) is not null) continue;
            var address = "contract:" + ContractIdentity(id, contract.Version);
            if (state.Construction!.Evidence?.ContainsKey(address) == true) continue;
            matches.Add((JsonNode)new JsonObject { ["id"] = id, ["version"] = contract.Version, ["hash"] = ContractHash(contract), ["evidence"] = address });
        }
        return matches.Count > 0 ? new JsonObject { ["items"] = matches } : null;
    }

    private static JsonArray ContractInspectionOptions(PlanningSession state, JsonObject? known, JsonObject? describedOperations)
    {
        var focus = PlanningDiscoveryFocus.Enabled(state) ? PlanningDiscoveryFocus.Choices(state)[0].Id : null;
        var visible = ContractDescriptorPage(state)["items"]!.AsArray().OfType<JsonObject>()
            .Concat((describedOperations?["items"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(c => c["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var (address, page) in state.Construction!.Evidence ?? [])
        {
            if (address.StartsWith("description:", StringComparison.Ordinal) && page is JsonObject described && described["id"] is { } id) visible.Add(id.ToString());
            if (address.StartsWith("page:operations:", StringComparison.Ordinal) && page is JsonObject directory && directory["items"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>())
                    if (item["id"] is { } operationId) visible.Add(operationId.ToString());
        }
        var cached = (known?["items"] as JsonArray ?? []).Select(row => (row!["id"]!.ToString(), row["version"]!.ToString())).ToHashSet();
        return new JsonArray(InspectionCandidates(state)
            .Where(c => visible.Contains(c.Operation!.Id) && !cached.Contains((c.Operation.Id, c.Version)) &&
                state.Construction!.Evidence?.ContainsKey("contract:" + ContractIdentity(c.Operation!.Id, c.Version)) != true &&
                PlanningDiscoveryFocus.Failure(state, c.SourceId, PlanningDiscoveryFocus.Query(state, focus), focus) is null)
            .GroupBy(c => c.SourceId).Select(group => (JsonNode)new JsonObject
            {
                ["sourceId"] = group.Key, ["focusId"] = focus,
                ["operationIds"] = new JsonArray(group.Select(c => (JsonNode)JsonValue.Create(c.Operation!.Id)!).ToArray())
            }).ToArray());
    }

    private static JsonObject CompactDiscoverySchema(PlanningSession state)
    {
        var requests = PresentedProviderRequests(state)?["items"]?.AsArray() ?? [];
        IEnumerable<JsonNode?> visible = requests;
        {
            var supplied = ProviderEvidencePages(state).Concat(DescribedProviderRequestPages(state))
                .SelectMany(page => page["items"]!.AsArray())
                .DistinctBy(item => (item!["sourceId"]!.ToString(), item["cursor"]?.ToString(), item["focusId"]?.ToString())).ToArray();
            if (supplied.Length > 0) visible = supplied;
        }
        var context = DiscoveryContext(state);
        var available = visible.Where(request => (request!["canRequestPage"] ?? request["available"])!.GetValue<bool>())
            .Concat(AvailableSourceDirectoryRequests(state, context)).DistinctBy(row =>
                (row!["sourceId"]!.ToString(), row["cursor"]?.ToString(), row["focusId"]?.ToString()));
        // Factor only identical cursor branches. The accepted source/cursor pairs
        // stay exact; grouping every source with every cursor would widen authority.
        var alternatives = new JsonArray();
        foreach (var group in available.GroupBy(request => (Cursor: request!["cursor"]?.ToString(), Focus: request["focusId"]?.ToString())))
        {
            var branch = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(group.Select(r => r!["sourceId"]!.ToString()).ToArray())),
                ("cursor", group.Key.Cursor is { } cursor ? PlanningSchemas.Enum(cursor) : PlanningSchemas.Type("null")),
                ("query", PlanningSchemas.Type("null")), ("operationIds", PlanningSchemas.Type("null")));
            if (PlanningDiscoveryFocus.Enabled(state))
            {
                branch["properties"]!["focusId"] = PlanningSchemas.Enum(group.Key.Focus!);
                branch["properties"]!["justification"] = Text(state, 256);
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("focusId"));
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("justification"));
            }
            alternatives.Add((JsonNode)branch);
        }
        foreach (var option in context["contractInspection"] as JsonArray ?? [])
        {
            var branch = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(option!["sourceId"]!.ToString())),
                ("cursor", PlanningSchemas.Type("null")), ("query", PlanningSchemas.Type("null")),
                ("operationIds", PlanningSchemas.Array(PlanningSchemas.Enum(option["operationIds"]!.AsArray()
                    .Select(c => c!.ToString()).ToArray()), 1, null)));
            if (PlanningDiscoveryFocus.Enabled(state))
            {
                branch["properties"]!["focusId"] = PlanningSchemas.Enum(option["focusId"]!.ToString());
                branch["properties"]!["justification"] = Text(state, 256);
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("focusId"));
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("justification"));
            }
            alternatives.Add((JsonNode)branch);
        }
        var schema = PlanningSchemas.Object(
            ("operationIds", PlanningSchemas.Nullable(Strings(state, 1, PlanningOptimizationThresholds.Maximum(state, state.Construction!.Limits.MaxTasksPerDraft)))),
            ("discoveryRequests", alternatives.Count == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(
                PlanningSchemas.Array(alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives }, 1, PlanningOptimizationThresholds.Maximum(state, 2)))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(state, 2048), 1,
                8))),
            ("reselectStep", CanReselectDiscovery(state) ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications(PlanningTokenThresholds.OptimizationAdvisory(state.Request)))));
        {
            schema["properties"]!.AsObject().Remove("operationIds");
            schema["required"]!.AsArray().RemoveAt(0);
            var binding = new JsonObject { ["anyOf"] = new JsonArray(
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("operation")),
                        ("operationIds", Strings(state, 1, PlanningOptimizationThresholds.Maximum(state, state.Construction!.Limits.MaxTasksPerDraft))), ("groupId", PlanningSchemas.Nullable(Text(state, 96)))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("call")),
                        ("operationIds", Strings(state, 0, PlanningOptimizationThresholds.Maximum(state, state.Construction.Limits.MaxTasksPerDraft))), ("groupId", Text(state, 96))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum(TaskKinds.Where(kind => kind is not ("operation" or "call") &&
                        (kind != "close" || DiscoveryCloseCanProgress(state))).ToArray())),
                        ("operationIds", Strings(state, 0, 0)), ("groupId", PlanningSchemas.Type("null")))) };
            {
                var branches = binding["anyOf"]!.AsArray();
                branches[2]!["properties"]!["kind"] = PlanningSchemas.Enum(TaskKinds.Where(kind => kind is not ("operation" or "call" or "close")).ToArray());
                branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum(TaskKinds.Where(kind => SupportsBusinessFragment(kind) && kind is not ("operation" or "call")).ToArray())),
                    ("operationIds", Strings(state, 1, PlanningOptimizationThresholds.Maximum(state, state.Construction.Limits.MaxTasksPerDraft))), ("groupId", PlanningSchemas.Nullable(Text(state, 96)))));
                branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum(TaskKinds.Where(kind => SupportsBusinessFragment(kind) && kind is not ("operation" or "call")).ToArray())),
                    ("operationIds", EmptyArray()), ("groupId", Text(state, 96))));
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
        ConstrainDiscoveryEvidence(state, context, schema);
        ConstrainReselectionSchema(state, schema);
        return schema;
    }

    private static IEnumerable<JsonObject> AvailableSourceDirectoryRequests(PlanningSession state, JsonObject context)
    {
        var knownSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (address, page) in context["evidence"] as JsonObject ?? [])
        {
            if (page is not JsonObject || !address.StartsWith("page:sources:", StringComparison.Ordinal) ||
                page["offset"] is not JsonValue position || !position.TryGetValue<int>(out var offset) ||
                offset < 0 || offset >= state.Discovery.Sources.Count ||
                address != "page:sources:" + offset.ToString(CultureInfo.InvariantCulture) ||
                !JsonNode.DeepEquals(page, SourceDescriptors(state, offset))) continue;
            foreach (var row in page["items"]!.AsArray().OfType<JsonObject>())
                if (row["descriptionComplete"]!.GetValue<bool>()) knownSources.Add(row["id"]!.ToString());
        }
        // A complete original source description supplies the same justification
        // as reading it separately. Only its current source/focus/cursor tuples apply.
        return knownSources.Count == 0 ? [] : ProviderRequestOptions(state)
            .Where(option => option.Available && knownSources.Contains(option.SourceId)).Select(ProviderRequestIdentity);
    }

    internal static bool CanReselectDiscovery(PlanningSession state)
    {
        var construction = state.Construction!; var step = State(state).CurrentStep;
        if (step is null || step.DiscoveryComplete ||
            (step.Kind != "operation" && !(step.Kind is null)) || construction.Candidate is not null ||
            state.EditablePaths is not null || state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            !PlanningModelCalls.HasDraftRepairCapacity(state, construction.DraftCorrections)) return false;
        var diagnosed = state.Diagnostics.Any(d => d.Required);
        if (!diagnosed && !PlanningModelCalls.HasRepairCapacity(state)) return false;
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
