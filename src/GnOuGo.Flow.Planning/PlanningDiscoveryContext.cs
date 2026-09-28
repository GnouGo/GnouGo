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
        state.Discovery.Pages.LastOrDefault(p => p.SourceId == source)?.Query;

    internal static bool CanDiscover(PlanningSession state)
    {
        if (TaskPlanRevisions.FixedOperations(state)) return false;
        var calls = PlanningModelCalls.RemainingCalls(state);
        var repairs = PlanningModelCalls.RemainingRepairs(state);
        if (PlanningModelCalls.IsRepair(state) && repairs <= 1) return false;
        return state.Plan is null ? calls > 1 + Math.Min(1, repairs) : calls > 1;
    }

    internal static List<CapabilitySummary> Candidates(PlanningSession state) => TaskPlanRevisions.FixedOperations(state) ? [] :
        CapabilityRelevance.Rank(state.Discovery.Pages.SelectMany(p => p.Capabilities)
            .Where(c => c.Operation is not null && state.Catalog!.AllowedStepTypes.Contains(c.StepType) &&
                !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(group => group.Select(c => c.Version).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()), c => SourceQuery(state, c.SourceId)).ToList();

    internal static List<PlanningOperation> Required(PlanningSession state)
    {
        var plan = state.Plan ?? state.Request.Baseline;
        var used = plan is null ? [] : TaskPlanRevisions.Tasks(plan).Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        var operations = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .Where(c => c.Kind == "registered" || used.Contains(TaskOperations.Describe(c).Id))
            .Select(TaskOperations.Describe);
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
                if (request.OperationIds is null || request.Cursor is not null || request.Query is not null ||
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
        if (request.Cursor is not null || request.Query is not null || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new PlanningResponseException([new("DISCOVERY_SELECTION_INVALID", path, "Use distinct discovered operation IDs with cursor and query null.")]);
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
        var continuations = pages.Where(p => p.NextCursor is not null && !pages.Any(seen => seen.Cursor == p.NextCursor && seen.Query == p.Query)).ToArray();
        var summary = new JsonObject
        {
            ["sourceId"] = source.Id, ["pagesRead"] = pages.Length,
            ["indexedCount"] = pages.SelectMany(p => p.Capabilities).DistinctBy(c => (c.Id, c.Version)).Count(),
            ["unavailable"] = pages.LastOrDefault()?.UnavailableReason
        };
        if (TaskPlanRevisions.FixedOperations(state)) { summary["hasMore"] = continuations.Length > 0; return (JsonNode)summary; }
        summary["hasMore"] = continuations.Length > 0;
        if (CanDiscover(state))
        {
            summary["query"] = pages.LastOrDefault()?.Query;
            summary["continuations"] = new JsonArray(continuations.Select(p => (JsonNode?)new JsonObject { ["cursor"] = p.NextCursor, ["query"] = p.Query }).ToArray());
        }
        summary["omittedFromIndex"] = 0;
        summary["index"] = new JsonArray(candidates.Where(c => c.SourceId == source.Id && !detailed.Contains(c.Operation!.Id)).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Operation?.Id ?? c.Id, ["name"] = c.Name,
                ["inputs"] = Names(c.Operation?.Inputs ?? []), ["outputs"] = Names(c.Operation?.Outputs ?? [])
            }).ToArray());
        return (JsonNode)summary;
    }).ToArray());

    private static JsonArray Names(IEnumerable<OperationPort> ports) => new(ports.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray());

    internal static async Task ResolveAsync(PlanningSession state, IPlanningRuntime runtime, CapabilitySummary summary, CancellationToken ct)
    {
        if (state.Discovery.Resolved.Any(c => c.Id == summary.Id && c.Version == summary.Version)) return;
        var resolved = await runtime.Capabilities.ResolveAsync(summary, ct);
        if (resolved.Id != summary.Id || resolved.Version != summary.Version ||
            PlanningContractValidation.ValidateSchema(resolved.InputSchema).Count > 0 ||
            resolved.OutputSchema.Count > 0 && PlanningContractValidation.ValidateSchema(resolved.OutputSchema).Count > 0 ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(TaskOperations.Describe(resolved), PlanningJsonContext.Default.PlanningOperation),
                JsonSerializer.SerializeToNode(summary.Operation, PlanningJsonContext.Default.PlanningOperation)))
            throw new PlanningConflictException("The operation contract changed after discovery.");
        state.Discovery.Resolved.Add(resolved);
    }
}
