using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningModelCalls
{
    internal static async Task<JsonNode> CallAsync(PlanningSession state, IPlanningRuntime runtime, string purpose, string prompt, JsonObject schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.PendingCall?.Request.StructuredOutputSchema?["properties"] is JsonObject previous &&
            (previous.ContainsKey("sourceId") || previous.ContainsKey("cursor")))
            throw new WorkflowRuntimeException("PLANNING_REQUEST_INCOMPATIBLE", "The pending request uses a superseded discovery response contract. Start a new planning session and regenerate the workflow. Its original request, reservation and accounting are retained.");
        if (state.PendingCall is null)
        {
            if (state.ModelCalls >= state.Request.MaxModelCalls) throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded, "The session model-call budget was exhausted.");
            var request = PlanningGenerationPolicy.Apply(new LLMRequest
            {
                Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
                Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "",
                Prompt = prompt, StructuredOutputSchema = schema, StructuredOutputStrict = true, UseBackgroundMode = true
            }, state.Request.Generation);
            var violations = PlanningContractValidation.ValidateSchema(schema, strict: true);
            if (violations.Count != 0) throw new InvalidOperationException("Invalid planner response schema: " + string.Join("; ", violations));
            var inputTokens = PlanningJsonTransport.EstimateInputTokens(prompt, schema);
            var inputLimit = state.Request.Generation.MaxInputTokensPerRequest;
            if (inputTokens > inputLimit)
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"The conservative input estimate for the complete {purpose} request (prompt and response schema) is {inputTokens} tokens; the configured limit is {inputLimit}. Planning stopped before dispatch without consuming another model call or repair. Increase the input token limit in generation settings and explicitly resume planning; cumulative session budgets remain unchanged.",
                    details: new JsonObject { ["location"] = "/phases/" + purpose });
            var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            request.ClientRequestId = state.Request.SessionId + ":" + (++state.ModelCalls) + ":" + hash;
            if (purpose == "replan") state.ReplanAttempts++;
            state.PendingCall = new() { Id = request.ClientRequestId, Purpose = purpose, Request = request };
            await runtime.CheckpointAsync(state, ct);
        }
        var call = state.PendingCall;
        if (call.Purpose != purpose) throw new PlanningConflictException("Complete the pending request before changing planning phases.");
        var response = await runtime.CallAsync(call.Request, purpose, ct);
        state.PendingCall = null;
        if (response.CompletionStatus == "output_limit") throw new WorkflowRuntimeException("MODEL_OUTPUT_LIMIT", "The model response was truncated; no output limit escalation is performed.", details: new JsonObject { ["location"] = "/phases/" + purpose });
        var json = response.Json?.DeepClone() ?? JsonNode.Parse(response.Text) ?? throw new JsonException("The model returned an empty response.");
        var findings = PlanningContractValidation.ValidateInstanceFindings(json, call.Request.StructuredOutputSchema!);
        if (findings.Count != 0)
        {
            var rejectedHash = PlanningGraphCompiler.Fingerprint(json.ToJsonString());
            if (state.RejectedProposalHash == rejectedHash) throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated an unchanged invalid proposal.");
            state.RejectedProposalHash = rejectedHash;
            throw new PlanningResponseException(findings.Select(f => new PlanningDiagnostic("PLANNING_RESPONSE_INVALID",
                f.InstancePointer, f.Message, ValidationStage: purpose)).ToList());
        }
        state.RejectedProposalHash = null;
        return json;
    }

}
internal sealed class PlanningResponseException(List<PlanningDiagnostic> diagnostics) : Exception("The model response violated its typed contract.")
{
    internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics;
}
