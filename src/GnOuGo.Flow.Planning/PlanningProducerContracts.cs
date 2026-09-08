using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolve opaque producer contracts before generating consumers that depend on their contents.</summary>
internal static class PlanningProducerContracts
{
    // A newly constructed action with declared object fields already has its producer
    // contract. Additional extraction belongs to an explicit transformation, not a
    // second model-written interpretation of the same response. Preserve legacy
    // structured declarations and their already-reviewed consumer bindings.
    internal static bool UsesDeclaredObject(PlanningNode node, PlanningPreparation preparation) => node.Type == "mcp.call" && node.StructuredOutput is null && !StructuredDecisions(node, preparation).Any() &&
        preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.OutputSchema is { } schema &&
        schema["type"]?.ToString() == "object" && schema["properties"] is System.Text.Json.Nodes.JsonObject { Count: > 0 };

    internal static bool RequiresStructuredResult(PlanningNode node, PlanningPreparation preparation)
        => StructuredDecisions(node, preparation).Any() || node.Type == "mcp.call" && preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { OutputSchema.Count: 0 } &&
            node.OperationIds.Count > 0 && preparation.Capabilities.Any(c => c.InputOperationIds.Intersect(node.OperationIds, StringComparer.Ordinal).Any());

    internal static IEnumerable<PlanningDecisionContract> StructuredDecisions(PlanningNode node, PlanningPreparation preparation)
        => preparation.Decisions.Where(d => d.ContractSource == "structured_output" && d.SourceCapabilityId == node.CapabilityId && node.OperationIds.Contains(d.SourceOperationId));

    internal static IEnumerable<PlanningDiagnostic> Findings(PlanningGraph graph, PlanningPreparation preparation)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + wi + "/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + wi + "/finally")))
            {
                var decisions = StructuredDecisions(node, preparation).ToArray();
                if (node.StructuredOutput is null && RequiresStructuredResult(node, preparation))
                    yield return new(decisions.Length > 0 ? "DECISION_PRODUCER_CONTRACT_REQUIRED" : "OPAQUE_PRODUCER_CONTRACT_REQUIRED", path + "/structuredOutput",
                        decisions.Length > 0 ? "Locked routing consumes a structured decision from this exact producer. Declare its required decision fields before constructing dependent routing; the original tool response cannot substitute for this result."
                        : "A declared downstream operation consumes this opaque result. Define validated structured output fields needed by its consumers before generating their computations. Do not guess fields, parse unspecified response formats, or replace missing values with examples.", ValidationStage: "dataflow");
                if (node.StructuredOutput is not null)
                    foreach (var decision in decisions)
                    {
                        JsonObject? actual = null;
                        try
                        {
                            var schema = PlanningGraphCompiler.ToJsonSchema(node.StructuredOutput.Schema, preparation);
                            var pointer = decision.SourcePointer.StartsWith("/json/", StringComparison.Ordinal) ? decision.SourcePointer[5..] : decision.SourcePointer;
                            foreach (var token in pointer.Split('/').Skip(1))
                            {
                                var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                                if (!(schema["required"] as JsonArray ?? []).Any(v => v?.ToString() == name)) throw new InvalidOperationException("Decision fields must be required.");
                                schema = schema["properties"]?[name] as JsonObject ?? throw new InvalidOperationException("Missing decision field.");
                            }
                            actual = schema;
                        }
                        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException) { }
                        if (actual is null || !JsonNode.DeepEquals(actual["type"], decision.ResponseSchema["type"]) ||
                            !(actual["enum"] as JsonArray ?? []).Select(v => v?.ToJsonString()).Order(StringComparer.Ordinal)
                                .SequenceEqual((decision.ResponseSchema["enum"] as JsonArray ?? []).Select(v => v?.ToJsonString()).Order(StringComparer.Ordinal)))
                            yield return new("DECISION_PRODUCER_CONTRACT_INVALID", path + "/structuredOutput/schema",
                                "The structured result must establish locked decision " + decision.SourcePointer + " with its exact response type and finite outcomes: " + decision.ResponseSchema.ToJsonString() + ". Preserve the original response separately.", ValidationStage: "dataflow");
                    }
            }
        }
    }
}
