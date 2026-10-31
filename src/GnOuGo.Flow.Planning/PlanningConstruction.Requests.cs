using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static LLMRequest DraftRequest(PlanningSession state, bool repair)
    {
        var context = PlanningCompactConstruction.Context(state);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        var instructions = PlanningCompactConstruction.DraftGuidance(state) + (repair
            ? "\nFor a replacement extension, preserve its target scope and accepted requirements; the accepted TaskPlan is immutable."
            : "");
        return PlanningCompactConstruction.Request(state, instructions, context, schema,
            state.Phase == PlanningPhase.Replanning ? "replan" : DraftPurpose);
    }

    internal static JsonNode NormalizeDraftResponse(PlanningSession state, JsonNode response, LLMRequest issuedRequest)
    {
        var normalized = response.DeepClone();
        var issuedSchema = issuedRequest.StructuredOutputSchema!.AsObject();
        var properties = issuedSchema["properties"]!.AsObject();
        if (properties.ContainsKey("baseHash")) return normalized;
        var construction = state.Construction!;
        var step = construction.Compact?.CurrentStep;
        var contextStart = issuedRequest.Prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        var issuedContext = contextStart < 0 ? null : JsonNode.Parse(issuedRequest.Prompt[(contextStart + 1)..]) as JsonObject;
        var issuedStep = issuedContext?["step"] as JsonObject;
        var issuedScope = issuedStep?["scope"]?.ToString();
        // The request hash binds the exact issued context. Recovery must match
        // its baseline, step and scope, rather than trusting mutable step state.
        if (step is null || construction.IssuedBaseHash is not { } baseline ||
            baseline != Hash(state.Plan) || step.BaseHash != baseline || issuedContext?["baseHash"]?.ToString() != baseline ||
            issuedStep?["id"]?.ToString() != step.Id || issuedScope != step.Scope)
            throw new PlanningConflictException("The issued draft's retained step or baseline changed.");
        if (normalized.AsObject().ContainsKey("baseHash"))
            throw new PlanningConflictException("The response supplied a host-owned draft baseline.");
        normalized["baseHash"] = baseline;
        if (normalized["extension"] is JsonObject extension && ObjectShape(properties["extension"]) is { } shape &&
            !shape["properties"]!.AsObject().ContainsKey("scope"))
        {
            if (extension.ContainsKey("scope"))
                throw new PlanningConflictException("The response supplied a host-owned draft scope.");
            extension["scope"] = issuedScope;
        }
        return normalized;

        JsonObject? ObjectShape(JsonNode? node, int depth = 0)
        {
            if (depth > 32 || node is not JsonObject schema) return null;
            if (schema["$ref"]?.ToString() is { } reference)
                return reference.StartsWith("#/", StringComparison.Ordinal)
                    ? ObjectShape(PlanningSchemaReferences.Read(issuedSchema, reference[1..]), depth + 1) : null;
            if (schema["properties"] is JsonObject) return schema;
            return schema["anyOf"] is JsonArray alternatives
                ? alternatives.Select(item => ObjectShape(item, depth + 1)).FirstOrDefault(item => item is not null) : null;
        }
    }
}
