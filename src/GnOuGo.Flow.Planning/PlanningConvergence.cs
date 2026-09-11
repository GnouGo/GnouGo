using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using System.Text.Json.Nodes;

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
            workflow.DeterministicSchemaHoles = holes.Count(h => h.Kind == "schema" && h.Resolved && h.ResolutionOrigin == "deterministic");
            workflow.ModelSchemaHoles = holes.Count(h => h.Kind == "schema" && h.Resolved && h.ResolutionOrigin == "model");
            // Keep the semantic need established at deterministic closure after a
            // model resolves the field; otherwise every completed workflow reports
            // zero required decisions while still reporting its actual model use.
            workflow.ModelRequired = holes.Any(h => h.ModelRequiredReason is null && (!h.Resolved || h.ResolutionOrigin != "deterministic"))
                ? null : holes.Count(h => h.ModelRequiredReason is not null);
            var evidence = state.RequestAccounting.Where(a => a.WorkflowKey == workflow.WorkflowKey).ToArray();
            workflow.ModelUsed = holes.Any(h => h.Resolved && h.ResolutionOrigin != "deterministic" && h.ExposedRequests.Count == 0 ||
                h.ExposedRequests.Any(id => !evidence.Any(a => a.Id == id))) ? null :
                holes.Count(h => h.ExposedRequests.Any(id => evidence.Any(a => a.Id == id && a.Evidence == "receipt")));
            workflow.HoleChoices = holes.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => new PlanningHoleProgress(h.Id, h.DirectCandidateCount, h.ComputationParameterCount)).ToList();
            workflow.Gates = state.GateProgress.Where(g => g.WorkflowKey == workflow.WorkflowKey)
                .Select(g => new PlanningGateCounts(g.Gate, state.RepairAllowances.Where(a => a.WorkflowKey == g.WorkflowKey && a.Gate == g.Gate).Sum(a => a.Attempts), g.Failures)).ToList();
        }
        var keys = state.RequestAccounting.Select(a => (a.WorkflowKey, a.Phase, a.Gate)).Concat(state.GateProgress.SelectMany(g => g.EvaluationPhases.Values.Select(p => (g.WorkflowKey, Phase: p, g.Gate)))).Distinct().ToArray();
        state.RequestCounts = keys.Select(key =>
        {
            var g = state.RequestAccounting.Where(a => a.WorkflowKey == key.WorkflowKey && a.Phase == key.Phase && a.Gate == key.Gate).ToArray();
            var gate = state.GateProgress.SingleOrDefault(p => p.WorkflowKey == key.WorkflowKey && p.Gate == key.Gate);
            return new PlanningRequestCounts(
            key.WorkflowKey, key.Phase, key.Gate, g.Length, g.Count(a => a.Evidence == "receipt"), g.Count(a => a.Evidence == "unverifiable"),
            g.Sum(a => a.EstimatedInputTokens), g.All(a => a.InputTokens.HasValue) ? g.Sum(a => a.InputTokens!.Value) : null,
            g.All(a => a.OutputTokens.HasValue) ? g.Sum(a => a.OutputTokens!.Value) : null,
            g.All(a => a.AvoidableDispatches.HasValue) ? g.Sum(a => a.AvoidableDispatches!.Value) : null,
            g.All(a => a.AvoidableExtraRequests.HasValue) ? g.Sum(a => a.AvoidableExtraRequests!.Value) : null,
            g.All(a => a.Repair.HasValue) ? g.Count(a => a.Repair == true) : null,
            gate is null ? 0 : gate.Evaluations.Count == gate.EvaluationPhases.Count ? gate.EvaluationPhases.Values.Count(p => p == key.Phase) : null);
        }).ToList();
    }

    internal static void AttributeHoles(PlanningSnapshot state, PlanningModelCall call, PlanningWorkflow workflow, IReadOnlyList<PlanningHole> holes)
    {
        var record = state.RequestAccounting.SingleOrDefault(a => a.Id == call.Id);
        if (record is null || record.HoleReasons.Count > 0) return; // Historical attribution remains unknown; replay is not a new exposure.
        var forcedCount = 0;
        foreach (var hole in holes)
        {
            var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
            var forced = hole.Kind == "value" && (PlanningBindingResolution.Unique(domain) is not null || PlanningBindingResolution.ForcedLiteral(hole, domain, out _));
            if (forced) forcedCount++;
            hole.ModelRequiredReason = forced ? null : hole.Kind == "schema" ? "business_schema" : domain.Direct.Count > 1 || domain.Direct.Count > 0 && (domain.Omission || domain.Literal) ? "binding_choice" : domain.Parameters.Count > 0 ? "computation" : "literal_business_value";
            record.HoleReasons[hole.Id] = hole.ModelRequiredReason ?? "deterministic_available";
        }
        // A request is avoidable only if none of its fields requires a model.
        record.AvoidableDispatches = holes.Count > 0 && forcedCount == holes.Count ? 1 : 0;
        var packed = PlanningWorkflowConstruction.Batch(state, workflow);
        record.AvoidableExtraRequests = packed.Holes.Any(h => !holes.Any(selected => selected.Id == h.Id)) ? 1 : 0;
        Refresh(state);
    }

    internal static void Receipt(PlanningSnapshot state, PlanningModelCall call, LLMResponse response)
    {
        var record = state.RequestAccounting.SingleOrDefault(a => a.Id == call.Id);
        if (record is null || record.Evidence == "receipt") return;
        record.Evidence = "receipt";
        record.InputTokens = Tokens("input_tokens", "prompt_tokens", "inputTokens");
        record.OutputTokens = Tokens("output_tokens", "completion_tokens", "outputTokens");
        Refresh(state);
        long? Tokens(params string[] keys)
        {
            foreach (var key in keys)
                if (response.Usage?[key] is JsonValue value && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0) return count;
            return null;
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
        value.EvaluationPhases[identity] = state.RequestAccounting.LastOrDefault(a => a.WorkflowKey == workflow && a.Gate == gate)?.Phase ?? state.CurrentPhase ?? "unknown";
        Refresh(state);
    }
}
