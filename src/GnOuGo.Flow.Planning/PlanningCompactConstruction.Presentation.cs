using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static JsonObject DiscoveryStep(PlanningSession state)
    {
        var compact = State(state);
        var original = JsonSerializer.SerializeToNode(compact.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep)!.AsObject();
        if (!IsCompactPresentation(state)) return original;

        // Discovery selects a producer for the current action. Draft authority and
        // requirement closure remain in the retained step; exact requirement IDs,
        // original clauses and global constraints are supplied beside this view.
        var view = new JsonObject();
        foreach (var field in new[] { "id", "scope", "kind", "objective", "expectedOutputs" })
            view[field] = original[field]?.DeepClone();
        view["inputs"] = PlanningJsonTransport.TaskPlanPart(original["inputs"]);
        foreach (var field in new[] { "inputTaskIds", "dependencyTaskIds", "operationIds", "schemaPointers" })
            if (original[field] is JsonArray { Count: > 0 } values) view[field] = values.DeepClone();
        if (UsesDeferredBinding(state) && original["prerequisites"] is JsonArray { Count: > 0 } prerequisites)
            view["prerequisites"] = prerequisites.DeepClone();
        return view;
    }
}
