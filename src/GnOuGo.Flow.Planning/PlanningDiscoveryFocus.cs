using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDiscoveryFocus
{
    internal sealed record Focus(string Id, string Query, string Basis);
    internal static bool Enabled(PlanningSession state) => state.Construction?.Compact?.CurrentStep?.DiscoveryProtocol == 2;
    internal static IReadOnlyList<Focus> Choices(PlanningSession state) => StepChoices(state).DistinctBy(f => f.Id).ToArray();

    private static List<Focus> StepChoices(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        var result = new List<Focus> { Create(step.Objective, "implementation_objective", []) };
        foreach (var need in step.Prerequisites ?? []) result.Add(Create(need.Need, "retained_prerequisite", need.EvidenceAddresses));
        return result;
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
    internal static void PresentInStep(PlanningSession state, JsonObject step)
    {
        var choices = StepChoices(state);
        step["focusId"] = choices[0].Id;
        if (step["prerequisites"] is JsonArray prerequisites)
            for (var i = 0; i < prerequisites.Count; i++)
                prerequisites[i]!["focusId"] = choices[i + 1].Id;
    }
    internal static string SourceKey(string source, string query, string? focus = null) => source + "\n" + query + (focus is null ? "" : "\n" + focus);
    // Active action references select metadata for decisions. Historical pages
    // remain retained and available through their exact versioned evidence.
    internal static IEnumerable<CapabilityPage> VisiblePages(PlanningSession state) => state.Discovery.Pages.Where(page =>
        page.Query is null || state.Discovery.ActiveSourceActions?.GetValueOrDefault(SourceKey(page.SourceId, page.Query, page.FocusId)) is not { } action ||
        page.ActionId == action);
    internal static IEnumerable<CapabilityPage> Pages(PlanningSession state, string source, string query, string? focus = null)
    {
        var active = state.Discovery.ActiveSourceActions?.GetValueOrDefault(SourceKey(source, query, focus));
        return state.Discovery.Pages.Where(p => p.SourceId == source && p.Query == query && p.ProducedArtifactKind is null &&
            (active is null || p.ActionId == active) && (focus is null || p.FocusId == focus));
    }

    internal static int? CompleteDirectoryCount(PlanningSession state, string source)
    {
        var pages = VisiblePages(state).Where(p => p.SourceId == source).ToArray();
        if (pages.FirstOrDefault() is not { SourceCapabilityCount: >= 0 and var count, SnapshotVersion: { Length: > 0 } version } ||
            pages.Any(p => p.UnavailableReason is not null || p.FailureId is not null || p.SnapshotVersion != version ||
                p.SourceCapabilityCount != count || p.ProducedArtifactKind is not null) ||
            state.Discovery.Failures?.Any(f => f.SourceId == source &&
                state.Discovery.SupersededFailures?.Contains(f.Id, StringComparer.Ordinal) != true) == true) return null;
        var capabilities = pages.SelectMany(p => p.Capabilities).DistinctBy(c => (c.Id, c.Version)).ToArray();
        return capabilities.Length == count && capabilities.All(c => c.SourceId == source) &&
            capabilities.DistinctBy(c => c.Id).Count() == count ? count : null;
    }
    internal static CapabilityDiscoveryFailure? Failure(PlanningSession state, string source, string query, string? focus = null) =>
        state.Discovery.Failures?.LastOrDefault(f => f.SourceId == source && f.Query == query &&
            (f.FocusId is not null ? f.FocusId == focus : f.StepId == state.Construction?.Compact?.CurrentStep?.Id) &&
            !(state.Discovery.SupersededFailures?.Contains(f.Id, StringComparer.Ordinal) ?? false));
}
