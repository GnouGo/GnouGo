using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDiscoveryFocus
{
    internal sealed record Focus(string Id, string Query, string Basis);
    internal static bool Enabled(PlanningSession state) => state.Construction?.Compact?.CurrentStep?.DiscoveryProtocol == 2;
    internal static IReadOnlyList<Focus> Choices(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        var result = new List<Focus> { Create(step.Objective, "implementation_objective", []) };
        foreach (var need in step.Prerequisites ?? []) result.Add(Create(need.Need, "retained_prerequisite", need.EvidenceAddresses));
        return result.DistinctBy(f => f.Id).ToArray();
        Focus Create(string text, string basis, IEnumerable<string> evidence) => new(
            "focus_" + PlanningGraphCompiler.Fingerprint(string.Join("\n", new[] { state.Request.TenantId, state.Request.SessionId, step.Id, step.BaseHash, basis, text }.Concat(evidence)))[..24], text, basis);
    }
    internal static string Query(PlanningSession state, string? focus)
    {
        if (!Enabled(state))
        {
            if (focus is not null) throw new PlanningConflictException("An old discovery action cannot acquire a new focus.");
            return state.Construction!.Compact!.CurrentStep!.Objective;
        }
        return Choices(state).SingleOrDefault(f => f.Id == focus)?.Query
            ?? throw new PlanningConflictException("The discovery focus does not belong to this implementation step.");
    }
    internal static JsonArray Presentation(PlanningSession state) => new(Choices(state).Select(f => (JsonNode)new JsonObject
        { ["id"] = f.Id, ["need"] = f.Query, ["basis"] = f.Basis }).ToArray());
    internal static string SourceKey(string source, string query, string? focus = null) => source + "\n" + query + (focus is null ? "" : "\n" + focus);
    internal static IEnumerable<CapabilityPage> Pages(PlanningSession state, string source, string query, string? focus = null)
    {
        var active = state.Discovery.ActiveSourceActions?.GetValueOrDefault(SourceKey(source, query, focus));
        return state.Discovery.Pages.Where(p => p.SourceId == source && p.Query == query && p.ProducedArtifactKind is null &&
            (active is null || p.ActionId == active) && (focus is null || p.FocusId == focus));
    }
    internal static CapabilityDiscoveryFailure? Failure(PlanningSession state, string source, string query, string? focus = null) =>
        state.Discovery.Failures?.LastOrDefault(f => f.SourceId == source && f.Query == query &&
            (f.FocusId is not null ? f.FocusId == focus : f.StepId == state.Construction?.Compact?.CurrentStep?.Id) &&
            !(state.Discovery.SupersededFailures?.Contains(f.Id, StringComparer.Ordinal) ?? false));
}
