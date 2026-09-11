using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Durable attribution; repeated observations and receipt replay do not consume counts.</summary>
internal static class PlanningConvergence
{
    internal static void Refresh(PlanningSnapshot state)
    {
        foreach (var allowance in state.RepairAllowances)
            if (!state.GateProgress.Any(g => g.WorkflowKey == allowance.WorkflowKey && g.Gate == allowance.Gate))
                state.GateProgress.Add(new() { WorkflowKey = allowance.WorkflowKey, Gate = allowance.Gate });
        foreach (var workflow in state.Construction.Workflows)
        {
            var holes = state.Construction.Holes.Where(h => h.WorkflowKey == workflow.WorkflowKey && !h.Superseded).ToArray();
            workflow.TotalHoles = holes.Length;
            workflow.ResolvedHoles = holes.Count(h => h.Resolved);
            workflow.UnresolvedHoles = holes.Count(h => !h.Resolved);
            workflow.DeterministicallyResolvedHoles = holes.Count(h => h.Resolved && h.ResolutionOrigin == "deterministic");
            workflow.ModelHoles = holes.Count(h => h.ExposedRequests.Count > 0);
            workflow.ModelHoleExposures = holes.Sum(h => h.ExposedRequests.Count);
            workflow.HoleChoices = holes.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => new PlanningHoleProgress(h.Id, h.DirectCandidateCount, h.ComputationParameterCount)).ToList();
            workflow.Gates = state.GateProgress.Where(g => g.WorkflowKey == workflow.WorkflowKey)
                .Select(g => new PlanningGateCounts(g.Gate, state.RepairAllowances.Where(a => a.WorkflowKey == g.WorkflowKey && a.Gate == g.Gate).Sum(a => a.Attempts), g.Failures)).ToList();
        }
    }
    internal static void Expose(PlanningSnapshot state, IEnumerable<PlanningHole> holes, string request)
    {
        foreach (var target in holes)
        {
            var hole = state.Construction.Holes.Single(h => h.Id == target.Id);
            if (!hole.ExposedRequests.Contains(request, StringComparer.Ordinal)) hole.ExposedRequests.Add(request);
        }
        Refresh(state);
    }
    internal static void Failure(PlanningSnapshot state, string workflow, string gate, string candidate, IEnumerable<PlanningDiagnostic> diagnostics)
    {
        var graph = state.Graph is null ? null : PlanningFieldPaths.Json(state.Graph);
        var findings = diagnostics.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, graph)).Order(StringComparer.Ordinal).ToArray();
        if (findings.Length == 0) return;
        var value = state.GateProgress.SingleOrDefault(g => g.Gate == gate && g.WorkflowKey == workflow);
        if (value is null) { value = new() { WorkflowKey = workflow, Gate = gate }; state.GateProgress.Add(value); }
        var identity = PlanningGraphCompiler.Fingerprint(candidate + "\n" + string.Join("\n", findings));
        if (value.Evaluations.Contains(identity, StringComparer.Ordinal)) return;
        value.Evaluations.Add(identity); value.Failures++;
        Refresh(state);
    }
}
