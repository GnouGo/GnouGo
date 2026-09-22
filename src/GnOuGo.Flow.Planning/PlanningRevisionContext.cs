using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Non-executable business context for revising an imported workflow.</summary>
public static class PlanningRevisionContext
{
    public static string FromGraph(PlanningGraph graph) => new JsonObject
    {
        ["summary"] = graph.Summary,
        ["flows"] = new JsonArray(graph.Workflows.Select(w => (JsonNode)new JsonObject
        {
            ["purpose"] = w.Purpose,
            ["inputs"] = new JsonArray(w.Inputs.Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
            ["outcomes"] = new JsonArray(w.Outputs.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()),
            ["activities"] = new JsonArray(PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)).Select(n => (JsonNode?)JsonValue.Create(n.Purpose)).ToArray())
        }).ToArray()),
        ["limitation"] = "Imported descriptions may be incomplete. Preserve the user's stated behavior; ask for missing business requirements before replacing behavior that is not described."
    }.ToJsonString();
}
