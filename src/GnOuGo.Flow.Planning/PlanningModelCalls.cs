using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Reserves exact requests before effects. Only the session coordinator mutates state.</summary>
internal static class PlanningModelCalls
{
    internal static LLMRequest Request(PlanningSnapshot state, string prompt, JsonObject schema)
    {
        var generator = state.Request.Options["generator"];
        return PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Prompt = prompt,
            Provider = generator?["provider"]?.GetValue<string>(),
            Model = generator?["model"]?.GetValue<string>() ?? "",
            StructuredOutputSchema = PlanningDecisionPages.BoundDomain(schema),
            StructuredOutputStrict = true,
            UseBackgroundMode = true
        }, state.Request.Generation);
    }

    internal static PlanningModelCall Reserve(PlanningSnapshot state, string phase, string workflow, LLMRequest request, string? gate = null, string? scope = null)
    {
        // A restart replays the exact reserved request. Governing edits are blocked while a call is pending.
        var pending = state.Construction.PendingCalls.SingleOrDefault(c => c.Phase == phase && c.WorkflowKey == workflow);
        if (pending is not null)
        {
            if (scope is not null && scope != pending.ScopeFingerprint)
                throw new PlanningConflictException("A different decision scope cannot replace a reserved request.");
            return pending;
        }
        PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.Reasoning = PlanningGenerationPolicy.ReasoningFor(state.Request.Generation, phase);
        if (request.StructuredOutputSchema is not JsonObject schema || PlanningContractValidation.ValidateSchema(schema, strict: true).Count != 0)
            throw new WorkflowRuntimeException(ErrorCodes.LlmSchema, "A valid strict typed response schema is required before dispatch.");
        if (PlanningDecisionPages.AnswerTokens(schema) > PlanningGenerationPolicy.AnswerTargetTokens)
            throw new WorkflowRuntimeException("MODEL_ANSWER_SIZE", "The indivisible response domain exceeds the structured-answer target. Split its decisions before reservation.");
        var estimate = PlanningJsonTransport.EstimateInputTokens(request.Prompt ?? "", schema);
        var target = PlanningGenerationPolicy.InputTarget(state.Request.Generation);
        if (estimate > target)
            throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"The indivisible decision needs approximately {estimate} input tokens; the dispatch target is {target}. No request was reserved or dispatched.");
        request.ClientRequestId = null;
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        gate ??= PlanningGates.Response;
        scope ??= PlanningGraphCompiler.Fingerprint(schema.ToJsonString());
        var id = state.Request.SessionId + ":" + state.Revision + ":" + (++state.Construction.ModelSequence) + ":" + phase + ":" + gate + ":" + PlanningGraphCompiler.Fingerprint(workflow)[..16] + ":" + scope + ":" + hash;
        request.ClientRequestId = id;
        var call = new PlanningModelCall { Id = id, Phase = phase, WorkflowKey = workflow, RequestHash = hash, Request = request, Gate = gate, ScopeFingerprint = scope, Revision = state.Revision };
        state.Construction.PendingCalls.Add(call);
        state.RequestAccounting.Add(new()
        {
            Id = id, Revision = state.Revision, WorkflowKey = string.IsNullOrEmpty(workflow) ? "$plan" : workflow,
            Phase = phase, Gate = gate, Reasoning = request.Reasoning, EstimatedInputTokens = estimate, Repair = phase == PlanningPhase.Repair || phase.EndsWith("_repair", StringComparison.Ordinal),
            Purpose = gate == PlanningGates.Semantic || phase.Contains("semantic", StringComparison.Ordinal) || phase.StartsWith("scenario_", StringComparison.Ordinal) ? "mandatory_validation" : phase == PlanningPhase.Repair || phase.StartsWith(PlanningPhase.Construction, StringComparison.Ordinal) ? "executable_holes" : "assessment"
        });
        return call;
    }

    internal static async Task<LLMResponse> CallAsync(PlanningSnapshot state, IPlanningRuntime runtime, string phase, LLMRequest request, CancellationToken ct, string workflow = "")
    {
        var call = Reserve(state, phase, workflow, request);
        await runtime.CheckpointAsync(state, ct);
        var response = await DispatchAsync(state, runtime, call, ct);
        state.Construction.PendingCalls.Remove(call);
        RequireComplete(response, call.Request.MaxTokens);
        return response;
    }

    internal static async Task<LLMResponse> DispatchAsync(PlanningSnapshot state, IPlanningRuntime runtime, PlanningModelCall call, CancellationToken ct)
    {
        LLMResponse response;
        try { response = await runtime.CallAsync(call.Request, call.Phase, ct); }
        catch
        {
            if (state.RequestAccounting.SingleOrDefault(a => a.Id == call.Id) is { } interrupted) interrupted.Evidence = "unverifiable";
            PlanningConvergence.Refresh(state);
            throw;
        }
        PlanningConvergence.Receipt(state, call, response);
        return response;
    }

    internal static void RequireComplete(LLMResponse response, int? ceiling = null)
    {
        if (response.CompletionStatus == "output_limit")
            throw new WorkflowRuntimeException("MODEL_OUTPUT_LIMIT", $"The model reached the output-token ceiling ({ceiling?.ToString() ?? "configured"} tokens). The verified response is incomplete; no automatic retry is permitted.");
    }

}
