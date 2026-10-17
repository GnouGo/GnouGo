using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// One contract derivation for semantic bindings, lowering and graph validation.
internal static class PlanningOperationResults
{
    internal static JsonObject Resolve(string stepType, JsonObject contract, PlanningValue? outputDeclaration)
    {
        var result = contract.DeepClone().AsObject();
        if (stepType == "agent.run" && outputDeclaration is not null)
        {
            if (!PlanningGraphValidation.IsLiteral(outputDeclaration) || PlanningGraphValidation.Literal(outputDeclaration) is not JsonObject schema)
                throw new ArgumentException("The agent output schema must be a literal JSON Schema object.");
            var findings = PlanningContractValidation.ValidateSchema(schema);
            if (findings.Count > 0) throw new ArgumentException("Invalid agent output schema: " + string.Join("; ", findings));
            // This executor validates the payload against the approved declaration.
            // No other operation may specialize its producer contract from input data.
            if (result["properties"] is JsonObject properties && properties.ContainsKey("output")) properties["output"] = schema;
        }
        return PlanningContractShapes.Producer(result);
    }
}
