using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static LLMRequest DraftRequest(PlanningSession state, bool repair) => Request(state,
        (state.Construction!.Version == 2 ? PlanningCompactConstruction.DraftGuidance(state) : Instructions) + (repair ? "\nCorrect only the retained tentative extension. Preserve its target scope and accepted requirements; the accepted TaskPlan is immutable." : ""),
        operations => Schema(state, operations), state.Construction!.Limits.DraftOutputTokens).Request;

    // Only fresh presentations may select optional contracts. Issued requests and
    // saved review contexts remain authoritative during recovery.
    internal static (JsonObject Context, LLMRequest Request) Request(PlanningSession state, string instructions,
        Func<IReadOnlySet<string>, JsonObject> schema, int outputTokens, bool review = false, bool intermediate = false,
        Action<JsonObject>? enrich = null)
    {
        var context = Context(state, review, intermediate, out var required);
        enrich?.Invoke(context);
        if (state.Construction!.Version == 2)
        {
            var purpose = review ? intermediate ? IntermediatePurpose : LocalPurpose : DraftPurpose;
            if (enrich is not null || state.Phase == GnOuGo.Flow.Core.Planning.PlanningPhase.Replanning) purpose = "replan";
            var responseSchema = schema(required);
            if (!review && enrich is null && PlanningCompactConstruction.UsesDeferredBinding(state))
                PlanningCompactConstruction.ConstrainDraftEvidence(state, context, responseSchema);
            return (context, PlanningCompactConstruction.Request(instructions, context, responseSchema, purpose));
        }
        var operations = context["operations"]!.AsArray();
        var optional = operations.Where(o => !required.Contains(o!["id"]!.ToString())).ToArray();
        if (optional.Length > 0)
        {
            // Keep stable identities available for explicit evidence retrieval;
            // omit whole suggestions, never trim an authoritative contract.
            context["optionalOperationContracts"] = new JsonObject
            {
                ["selection"] = "Exact optional contracts appear in operations only when the complete request fits. Request any retained contract using contract:<id>.",
                ["available"] = new JsonArray(optional.Select(o => (JsonNode)new JsonObject
                    { ["id"] = o!["id"]!.DeepClone(), ["version"] = o["version"]?.DeepClone() }).ToArray())
            };
            foreach (var operation in optional) operations.Remove(operation);
        }
        var request = Render();
        if (Estimate(request) > state.Request.Generation.MaxInputTokensPerRequest && context.Remove("optionalOperationContracts"))
            request = Render();
        // Mandatory context still reaches normal admission unchanged and fails
        // explicitly if it cannot fit. No allowance or accounting is changed.
        if (Estimate(request) > state.Request.Generation.MaxInputTokensPerRequest) return (context, request);
        foreach (var operation in optional)
        {
            operations.Add(operation);
            var candidate = Render();
            if (Estimate(candidate) <= state.Request.Generation.MaxInputTokensPerRequest) request = candidate;
            else operations.Remove(operation);
        }
        return (context, request);

        LLMRequest Render() => new()
        {
            Prompt = instructions + "\n" + PlanningJsonTransport.Prompt(context),
            StructuredOutputSchema = schema(operations.Select(o => o!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal))
        };
        long Estimate(LLMRequest candidate) => ModelRequestAdmission.EstimateTokens(PlanningGenerationPolicy.Apply(new()
        {
            Prompt = candidate.Prompt, StructuredOutputSchema = candidate.StructuredOutputSchema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = outputTokens,
            ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" + new string('0', 64),
            Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
            Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? ""
        }, state.Request.Generation));
    }
}
