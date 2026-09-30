using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning;

// Transient presentation only: receipts and executable contracts remain authoritative.
internal static class PlanningDiscoveryContext
{
    internal static string Query(PlanningSession state) => CapabilityRelevance.Query(state.Request.Prompt + " " +
        state.Requirements?.Summary + " " + string.Join(' ', state.Requirements?.Outcomes.Select(o => o.Description) ?? []));

    // Source queries refine presentation without replacing accepted intent or
    // transferring one source's focus to unrelated sources. Receipt cursors stay unchanged.
    private static string SourceQuery(PlanningSession state, string source) => Query(state) + " " +
        state.Discovery.Pages.LastOrDefault(p => p.SourceId == source && p.ProducedArtifactKind is null)?.Query;

    internal static bool CanDiscover(PlanningSession state)
    {
        if (TaskPlanRevisions.FixedOperations(state)) return false;
        var calls = PlanningModelCalls.RemainingCalls(state);
        var repairs = PlanningModelCalls.RemainingRepairs(state);
        if (PlanningModelCalls.IsRepair(state) && repairs <= 1) return false;
        return state.Plan is null ? calls > 1 + Math.Min(1, repairs) : calls > 1;
    }

    internal static List<CapabilitySummary> Candidates(PlanningSession state) => TaskPlanRevisions.FixedOperations(state) ? [] :
        PrioritizePrerequisites(state, CapabilityRelevance.Rank(state.Discovery.Pages.SelectMany(p => p.Capabilities)
            .Where(c => c.Operation is not null && state.Catalog!.AllowedStepTypes.Contains(c.StepType) &&
                !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(group => group.Select(c => c.Version).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()), c => SourceQuery(state, c.SourceId))).ToList();

    internal static GnOuGo.Flow.Core.Runtime.McpArtifactContract? Artifacts(PlanningSession state, CapabilitySummary summary) =>
        summary.ArtifactContract ?? state.Discovery.Resolved.Concat(state.Catalog!.Capabilities)
            .FirstOrDefault(c => c.Id == summary.Id && c.Version == summary.Version)?.ArtifactContract;

    internal static string[] Prerequisites(PlanningSession state) => state.Discovery.Pages.SelectMany(p => p.Capabilities)
        .Where(c => state.Catalog!.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
        .GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Select(c => c.Version).Distinct(StringComparer.Ordinal).Count() == 1)
        .SelectMany(g => Artifacts(state, g.First())?.Consumes ?? []).Where(a => a.Required)
        .Select(a => a.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<CapabilitySummary> PrioritizePrerequisites(PlanningSession state, IReadOnlyList<CapabilitySummary> ranked)
    {
        var kinds = Prerequisites(state).ToHashSet(StringComparer.Ordinal);
        var matches = state.Discovery.Pages.Where(p => p.ProducedArtifactKind is { } kind && kinds.Contains(kind))
            .SelectMany(p => p.Capabilities).Select(c => (c.Id, c.Version)).ToHashSet();
        // Promote prerequisite search results, not every alternative producer of a
        // kind whose producer was already visible. Keep relevance within each tier.
        return ranked.OrderByDescending(c => matches.Contains((c.Id, c.Version)));
    }

    internal static List<PlanningOperation> Required(PlanningSession state)
    {
        var plan = state.Plan ?? state.Request.Baseline;
        var used = plan is null ? [] : TaskPlanRevisions.Tasks(plan).Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        var operations = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .Where(c => c.Kind == "registered" || used.Contains(TaskOperations.Describe(c).Id))
            .Select(PlanningCapabilityArguments.Editable);
        return operations.Concat(Inspected(state).Select(c => c.Operation!)).DistinctBy(o => o.Id)
            .OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    internal static List<CapabilitySummary> Inspected(PlanningSession state)
    {
        if (TaskPlanRevisions.FixedOperations(state)) return [];
        var selected = new List<CapabilitySummary>();
        try
        {
            foreach (var request in state.Discovery.Inspections ?? [])
            {
                if (request.OperationIds is null || request.Cursor is not null || request.Query is not null || request.ProducedArtifactKind is not null ||
                    !state.Discovery.Sources.Any(s => s.Id == request.SourceId) || state.Discovery.Inspections!.Count(i => i.SourceId == request.SourceId) != 1)
                    throw new PlanningResponseException([new("DISCOVERY_SELECTION_INVALID", "/discoveryRequests", "The retained inspection selection is invalid.")]);
                selected.AddRange(Inspection(state, request, "/discoveryRequests"));
            }
        }
        catch (PlanningResponseException)
        { throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("DISCOVERY_SELECTION_INVALID", "The retained contract inspection selection is no longer unambiguous and policy-allowed. Start a new planning session."); }
        return selected;
    }

    internal static List<CapabilitySummary> Inspection(PlanningSession state, PlanningDiscoveryRequest request, string path)
    {
        var result = new List<CapabilitySummary>();
        var ids = request.OperationIds!;
        if (request.Cursor is not null || request.Query is not null || request.ProducedArtifactKind is not null || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new PlanningResponseException([new("DISCOVERY_SELECTION_INVALID", path, "Use distinct discovered operation IDs with cursor, query and producedArtifactKind null.")]);
        foreach (var id in ids)
        {
            var matches = state.Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.Operation?.Id == id).ToArray();
            if (matches.Length == 0 || matches.Any(c => c.SourceId != request.SourceId) ||
                matches.Select(c => (c.Id, c.Version)).Distinct().Count() != 1 ||
                !state.Catalog!.AllowedStepTypes.Contains(matches[0].StepType) || state.Catalog.Policy.DeniedCapabilityIds.Contains(matches[0].Id))
                throw new PlanningResponseException([new("DISCOVERY_SELECTION_INVALID", path + "/operationIds", "Choose an unambiguous, policy-allowed operation already discovered from this source.")]);
            result.Add(matches[0]);
        }
        return result;
    }

    internal static JsonArray Coverage(PlanningSession state, IReadOnlySet<string> detailed, IReadOnlyList<CapabilitySummary> candidates) => new(state.Discovery.Sources.Select(source =>
    {
        var pages = state.Discovery.Pages.Where(p => p.SourceId == source.Id).ToArray();
        var continuations = pages.Where(p => p.NextCursor is not null && !pages.Any(seen => seen.Cursor == p.NextCursor && seen.Query == p.Query && seen.ProducedArtifactKind == p.ProducedArtifactKind)).ToArray();
        var summary = new JsonObject
        {
            ["sourceId"] = source.Id, ["pagesRead"] = pages.Length,
            ["indexedCount"] = pages.SelectMany(p => p.Capabilities).DistinctBy(c => (c.Id, c.Version)).Count()
        };
        if (pages.LastOrDefault()?.UnavailableReason is { } unavailable) summary["unavailable"] = unavailable;
        if (TaskPlanRevisions.FixedOperations(state)) { summary["hasMore"] = continuations.Length > 0; return (JsonNode)summary; }
        summary["hasMore"] = continuations.Length > 0;
        if (CanDiscover(state))
        {
            summary["query"] = pages.LastOrDefault()?.Query;
            summary["continuations"] = new JsonArray(continuations.Select(p =>
            {
                var continuation = new JsonObject { ["cursor"] = p.NextCursor, ["query"] = p.Query };
                if (p.ProducedArtifactKind is not null) continuation["producedArtifactKind"] = p.ProducedArtifactKind;
                return (JsonNode)continuation;
            }).ToArray());
        }
        summary["omittedFromIndex"] = 0;
        summary["index"] = new JsonArray(candidates.Where(c => c.SourceId == source.Id && !detailed.Contains(c.Operation!.Id)).Select(c =>
        {
            var inputs = EditableInputs(c).ToArray();
            var item = new JsonObject { ["id"] = c.Operation?.Id ?? c.Id, ["name"] = c.Name,
                ["inputs"] = Names(inputs), ["outputs"] = Names(c.Operation?.Outputs ?? []) };
            var constraints = new JsonObject(inputs.Select(p => (p.Name, Domain: TaskOperations.FiniteDomain(p.Schema)))
                .Where(p => p.Domain.Count > 0).Select(p => new KeyValuePair<string, JsonNode?>(p.Name, p.Domain)));
            if (constraints.Count > 0) item["constraints"] = constraints;
            if (TaskOperations.ArtifactPorts(c.Operation!, Artifacts(state, c)) is { Count: > 0 } artifacts) item["artifacts"] = artifacts;
            return (JsonNode)item;
        }).ToArray());
        return (JsonNode)summary;

        IEnumerable<OperationPort> EditableInputs(CapabilitySummary capability)
        {
            var exact = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved)
                .FirstOrDefault(c => c.Id == capability.Id && c.Version == capability.Version);
            return exact is null ? capability.Operation?.Inputs ?? [] : PlanningCapabilityArguments.Editable(exact).Inputs;
        }
    }).ToArray());

    private static JsonArray Names(IEnumerable<OperationPort> ports) => new(ports.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray());

    internal static async Task ResolveAsync(PlanningSession state, IPlanningRuntime runtime, CapabilitySummary summary, CancellationToken ct)
    {
        var cached = state.Discovery.Resolved.SingleOrDefault(c => c.Id == summary.Id && c.Version == summary.Version);
        var resolved = cached ?? await runtime.Capabilities.ResolveAsync(summary, ct);
        if (resolved.Id != summary.Id || resolved.Version != summary.Version ||
            summary.ArtifactContract is not null && !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(summary.ArtifactContract, PlanningJsonContext.Default.McpArtifactContract),
                JsonSerializer.SerializeToNode(resolved.ArtifactContract, PlanningJsonContext.Default.McpArtifactContract)) ||
            PlanningContractValidation.ValidateSchema(resolved.InputSchema).Count > 0 ||
            resolved.OutputSchema.Count > 0 && PlanningContractValidation.ValidateSchema(resolved.OutputSchema).Count > 0 ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(TaskOperations.Describe(resolved), PlanningJsonContext.Default.PlanningOperation),
                JsonSerializer.SerializeToNode(summary.Operation, PlanningJsonContext.Default.PlanningOperation)) &&
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(PlanningCapabilityArguments.Editable(resolved), PlanningJsonContext.Default.PlanningOperation),
                JsonSerializer.SerializeToNode(summary.Operation, PlanningJsonContext.Default.PlanningOperation)))
            throw new PlanningConflictException("The operation contract changed after discovery.");
        if (cached is null) state.Discovery.Resolved.Add(resolved);
    }
}
