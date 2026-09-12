using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningContext
{
    internal static string Intent(PlanningSnapshot state) => state.Request.Prompt +
        (BaselineText(state) is not { } baseline ? "" : "\nExisting workflow behavior to preserve except for the requested revision:\n" + baseline) +
        string.Concat(state.Intent.Answers.Select(a => "\nHuman clarification:\n" + string.Join("\n", a.Answers.Select(v => PlanningBusinessAnswers.Describe(state, v.Key, v.Value)))));

    internal static string? BaselineText(PlanningSnapshot state)
    {
        if (state.Request.Baseline is null) return null;
        if (state.BehaviorPlan is not null && state.ApprovedBehaviorHash == PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan))
            return JsonSerializer.Serialize(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan);
        return state.BehaviorRevision?.ReviewedBaselineBehavior ?? PlanningSemanticContext.Graph(state.Request.Baseline).ToJsonString();
    }

    internal static string Contracts(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(
        PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!) + "\n" +
        JsonSerializer.Serialize(state.Preparation, PlanningJsonContext.Default.PlanningPreparation) +
        (state.BusinessDecisions.Any(d => d.Status is "runtime" or "resolved") ? "\n" + PlanningBusinessAnswers.ContractFingerprint(state) : ""));

    internal static string Fixtures(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(
        state.Validation.Inputs?.ToJsonString() + "\n" + state.Validation.Observations.ToJsonString());

    internal static PlanningRequest EffectiveRequest(PlanningSnapshot state)
    {
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest), PlanningJsonContext.Default.PlanningRequest)!;
        request.Prompt = Intent(state);
        return request;
    }

    internal static PlanningGraph Clone(PlanningGraph graph) => JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
    internal static PlanningSnapshot Clone(PlanningSnapshot state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
    internal static void InvalidateArtifact(PlanningSnapshot state)
    {
        state.Yaml = null; state.ArtifactHash = null; state.ApprovedHash = null;
        if (state.Outcome is PlanningValidWorkflow) state.Outcome = null;
    }
    internal static void Stop(PlanningSnapshot state, string code, string message, string location = "$")
    {
        state.Status = PlanningStatus.Stopped;
        state.Diagnostics.Add(new(code, location, message));
        InvalidateArtifact(state);
        PlanningOutcomes.Refresh(state);
    }
}
