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

    private static string PresentationQuery(PlanningSession state) => state.Discovery.PresentationQuery ?? Query(state);

    internal static bool CanDiscover(PlanningSession state)
    {
        if (TaskPlanRevisions.FixedOperations(state)) return false;
        var calls = PlanningModelCalls.RemainingCalls(state);
        var repairs = PlanningModelCalls.RemainingRepairs(state);
        if (PlanningModelCalls.IsRepair(state) && repairs <= 1) return false;
        return state.Plan is null ? calls > 1 + repairs : calls > 1;
    }

    internal static IEnumerable<CapabilitySummary> Index(PlanningSession state, string source)
    {
        var page = state.Discovery.Pages.LastOrDefault(p => p.SourceId == source);
        if (page is null) return [];
        return (page.Query is null
            ? CapabilityRelevance.Rank(state.Discovery.Pages.Where(p => p.SourceId == source).SelectMany(p => p.Capabilities).DistinctBy(c => (c.Id, c.Version)), PresentationQuery(state))
            : page.Capabilities).Take(8);
    }

    internal static List<CapabilitySummary> Candidates(PlanningSession state) => TaskPlanRevisions.FixedOperations(state) ? [] :
        CapabilityRelevance.Rank(state.Discovery.Pages.SelectMany(p => p.Capabilities)
            .Where(c => c.Operation is not null && state.Catalog!.AllowedStepTypes.Contains(c.StepType) &&
                !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(group => group.Select(c => c.Version).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()), PresentationQuery(state)).ToList();

    internal static List<PlanningOperation> Required(PlanningSession state)
    {
        var plan = state.Plan ?? state.Request.Baseline;
        var used = plan is null ? [] : TaskPlanRevisions.Tasks(plan).Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        return state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .Where(c => c.Kind == "registered" || used.Contains(TaskOperations.Describe(c).Id))
            .Select(TaskOperations.Describe).OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    internal static JsonArray Coverage(PlanningSession state) => new(state.Discovery.Sources.Select(source =>
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
        summary["query"] = pages.LastOrDefault()?.Query;
        summary["continuations"] = new JsonArray(continuations.Select(p => (JsonNode?)new JsonObject { ["cursor"] = p.NextCursor, ["query"] = p.Query }).ToArray());
        summary["index"] = new JsonArray(Index(state, source.Id).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Operation?.Id ?? c.Id, ["name"] = c.Name,
                ["description"] = c.Description[..Math.Min(c.Description.Length, 200)], ["descriptionTruncated"] = c.Description.Length > 200,
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
