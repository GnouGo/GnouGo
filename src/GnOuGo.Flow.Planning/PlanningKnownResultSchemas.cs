using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Add only result fields whose complete existing value contract can be retained exactly.</summary>
internal static class PlanningKnownResultSchemas
{
    internal static JsonObject? Complete(PlanningGraph graph, PlanningConstructionUnit unit, PlanningPreparation preparation)
    {
        if (unit.Kind != "contracts" || unit.ProducerReviewBaseline is null || unit.Candidate is null) return null;
        var workflow = graph.Workflows.Single(w => w.Key == unit.WorkflowKey);
        var resolve = PlanningGraphValidation.OptionalValueContractResolver(graph, workflow, preparation);
        var candidate = unit.Candidate.DeepClone().AsObject(); var count = 0;
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key) && n.Type == "set" && n.Input.Kind == "object"))
        {
            if (node.OutputSchema is not { CapabilityId: null, SchemaPointer: null, Type: "object" } schema ||
                candidate["nodes"]?[node.Key]?["outputSchema"] is not JsonObject declaration || declaration["properties"] is not JsonArray properties) continue;
            foreach (var field in node.Input.Members.Where(m => !schema.Properties.Any(p => p.Name == m.Name)))
                try
                {
                    if (count >= 4) break;
                    var actual = resolve(field.Value);
                    if (actual is null || actual.Count == 0) continue;
                    if (schema.AdditionalProperties is { } additional && PlanningGraphValidation.TypesFit(actual, PlanningGraphCompiler.ToJsonSchema(additional, preparation))) continue;
                    var typed = PlanningGraphImporter.Schema(actual);
                    // Unsupported constraints, unions, open objects and opaque values
                    // remain unresolved. Import must not silently narrow or erase them.
                    if (!JsonNode.DeepEquals(actual, PlanningGraphCompiler.ToJsonSchema(typed, preparation))) continue;
                    var port = new PlanningPort { Name = field.Name, Required = true, Schema = typed };
                    properties.Add(PlanningModelValues.Compact(JsonSerializer.SerializeToNode(port, PlanningJsonContext.Default.PlanningPort)));
                    count++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { }
        }
        if (count == 0) return null;
        PlanningProducerRepair.Preserve(unit.ProducerReviewBaseline, candidate);
        return candidate;
    }
}
