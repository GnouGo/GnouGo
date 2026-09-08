using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolve opaque producer contracts before generating consumers that depend on their contents.</summary>
internal static class PlanningProducerContracts
{
    // A newly constructed action with declared object fields already has its producer
    // contract. Additional extraction belongs to an explicit transformation, not a
    // second model-written interpretation of the same response. Preserve legacy
    // structured declarations and their already-reviewed consumer bindings.
    internal static bool UsesDeclaredObject(PlanningNode node, PlanningPreparation preparation) => node.Type == "mcp.call" && node.StructuredOutput is null &&
        preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.OutputSchema is { } schema &&
        schema["type"]?.ToString() == "object" && schema["properties"] is System.Text.Json.Nodes.JsonObject { Count: > 0 };

    internal static bool RequiresStructuredResult(PlanningNode node, PlanningPreparation preparation)
        => node.Type == "mcp.call" && preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { OutputSchema.Count: 0 } &&
            node.OperationIds.Count > 0 && preparation.Capabilities.Any(c => c.InputOperationIds.Intersect(node.OperationIds, StringComparer.Ordinal).Any());

    internal static IEnumerable<PlanningDiagnostic> Findings(PlanningGraph graph, PlanningPreparation preparation)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + wi + "/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + wi + "/finally")))
                if (node.StructuredOutput is null && RequiresStructuredResult(node, preparation))
                    yield return new("OPAQUE_PRODUCER_CONTRACT_REQUIRED", path + "/structuredOutput",
                        "A declared downstream operation consumes this opaque result. Define validated structured output fields needed by its consumers before generating their computations. Do not guess fields, parse unspecified response formats, or replace missing values with examples.", ValidationStage: "dataflow");
        }
    }
}
