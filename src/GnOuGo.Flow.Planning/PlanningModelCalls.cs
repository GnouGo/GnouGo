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
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"The complete {purpose} request needs approximately {inputTokens} input tokens; the configured limit is {inputLimit}. The action/subgraph cannot proceed within its current request budget.",
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
            var rejectedHash = PlanningGraphCompiler.Fingerprint((call.Request.StructuredOutputSchema!["properties"]?["decision"] is not null && json["decision"] is null ? json["result"] ?? json : json).ToJsonString());
            if (state.RejectedProposalHash == rejectedHash) throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated an unchanged invalid proposal.");
            state.RejectedProposalHash = rejectedHash;
            throw new PlanningResponseException(findings.Select(f => new PlanningDiagnostic("PLANNING_RESPONSE_INVALID",
                f.InstancePointer.Replace("/implementation/", "/", StringComparison.Ordinal), f.Message, ValidationStage: purpose)).ToList());
        }
        state.RejectedProposalHash = null;
        if (json["blockedActions"] is JsonArray { Count: > 0 } blockers)
        {
            var actions = SemanticPlanning.Actions(state.SemanticPlan!).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            if (blockers.Any(b => !actions.Contains(b!["actionId"]!.ToString()) || string.IsNullOrWhiteSpace(b["reason"]!.ToString())) ||
                blockers.Select(b => b!["actionId"]!.ToString()).Distinct().Count() != blockers.Count ||
                new[] { "inputs", "operations", "outputs", "subflows" }.Any(field => json[field] is JsonArray { Count: > 0 }))
                throw new PlanningResponseException([new("BINDING_BLOCKER_INVALID", "/blockedActions", "A blocked binding must name distinct existing semantic actions, explain each missing prerequisite, and contain no executable proposal.")]);
            throw new PlanningResponseException(blockers.Select(b => new PlanningDiagnostic("SEMANTIC_BINDING_BLOCKED", "/actions/" + b!["actionId"]!.ToString(), b["reason"]!.ToString(), ValidationStage: "grounding")).ToList());
        }
        return call.Request.StructuredOutputSchema!["properties"]?["decision"] is not null ? json : PlanningJsonTransport.ModelGrounded(json, call.Request.StructuredOutputSchema!, unpack: true);
    }

}
internal sealed class PlanningResponseException(List<PlanningDiagnostic> diagnostics) : Exception("The model response violated its typed contract.")
{
    internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics;
}
