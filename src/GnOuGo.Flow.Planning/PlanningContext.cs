using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningContext
{
    internal static string Intent(PlanningSnapshot state) => state.Request.Prompt +
        (state.Request.Baseline is null ? "" : "\nExisting workflow behavior to preserve except for the requested revision:\n" + JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.PlanningGraph)) +
        string.Concat(state.Intent.Answers.Select(a => "\nHuman clarification:\n" + a.Answers.ToJsonString()));

    internal static string Contracts(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(
        PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!) + "\n" +
        JsonSerializer.Serialize(state.Preparation, PlanningJsonContext.Default.PlanningPreparation));

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
    }
    internal static void Stop(PlanningSnapshot state, string code, string message, string location = "$")
    {
        state.Status = PlanningStatus.Recovery;
        state.Diagnostics.Add(new(code, location, message));
        InvalidateArtifact(state);
    }
}
