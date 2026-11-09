using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static partial class PlanningModelCalls
{
    // Active responses use Unicode code-point bounds; exact requests still govern recovery.
    internal const string UnicodeStringLengths = "GnOuGo planning response: string length limits count Unicode code points.";
    internal static IReadOnlyList<PlanningInstanceFinding> ValidateResponseFindings(JsonNode? response, JsonNode issuedSchema) =>
        JsonSchemaInstanceValidator.ValidateInstanceFindings(response, issuedSchema,
            unicodeStringLengths: true);
    internal static int CallLimit(PlanningSession state) => Math.Min(state.Request.MaxModelCalls,
        PlanningBudgetOptions.Parse(state.Request.Options)?.MaxCalls ?? state.Request.MaxModelCalls);
    internal static long CallsUsed(PlanningSession state) => Math.Max(state.ModelCalls, state.Usage?.Calls ?? 0);
    internal static long RemainingCalls(PlanningSession state) => Math.Max(0, CallLimit(state) - CallsUsed(state));
    internal static int RemainingRepairs(PlanningSession state) => Math.Max(0, state.Request.MaxReplanAttempts - state.ReplanAttempts);
    internal static bool HasCallCapacity(PlanningSession state, long required = 1) =>
        PlanningApplicationBudget.TrackingOnly(state.Request) || PlanningTokenThresholds.OptimizationAdvisory(state.Request) || RemainingCalls(state) >= required;
    internal static bool HasRepairCapacity(PlanningSession state) =>
        PlanningTokenThresholds.OptimizationAdvisory(state.Request) || RemainingRepairs(state) > 0;
    internal static bool HasDraftRepairCapacity(PlanningSession state, int corrections) =>
        PlanningTokenThresholds.OptimizationAdvisory(state.Request) || corrections < state.Construction!.Limits.MaxCorrectionsPerDraft;
    internal static bool IsRepair(PlanningSession state) => state.Diagnostics.Any(d => d.Required &&
        d.Code is not (ErrorCodes.LlmBudgetExceeded or "DISCOVERY_INCOMPLETE" or "DISCOVERY_NOT_ALLOWED"));
    internal static int ConstructionOutputLimit(PlanningSession state, string purpose) =>
        PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).OutputTokens;

    internal static void EnsureCallAvailable(PlanningSession state)
    {
        if (PlanningTokenThresholds.OptimizationAdvisory(state.Request) && CallsUsed(state) >= CallLimit(state))
            PlanningOptimizationThresholds.Record(state, "dispatch", "model_calls", CallsUsed(state) + 1, CallLimit(state));
        if (!HasCallCapacity(state))
            throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded,
                $"The session model-call budget was exhausted ({CallsUsed(state)}/{CallLimit(state)} calls). Input/output token settings do not extend this cumulative allowance. Start a new planning session; retained requests and accounting remain unchanged.",
                details: new JsonObject { ["scope"] = "planning_session", ["origin"] = state.Request.Options[PlanningApplicationBudget.OptionName]?["origin"]?.ToString() ?? "captured_planning_request",
                    ["limit_kind"] = "calls", ["allowance"] = CallLimit(state), ["required_reservation"] = CallsUsed(state) + 1,
                    ["dispatch_status"] = "not_started" });
    }

    internal static LLMRequest PrepareRequest(PlanningSession state, string purpose, string prompt, JsonObject schema, int? outputLimit = null,
        int? requiredReviewCalls = null) => PrepareRequest(state, purpose, prompt, schema, outputLimit, requiredReviewCalls, out _);

    internal static LLMRequest PrepareRequest(PlanningSession state, string purpose, string prompt, JsonObject schema, int? outputLimit,
        int? requiredReviewCalls, out PlanningEvidenceAddresses? evidenceAddresses)
    {
        {
            schema = schema.DeepClone().AsObject();
            schema["title"] = purpose;
            schema["$comment"] = UnicodeStringLengths;
        }
        var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
        {
            Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
            Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "",
            Prompt = prompt, StructuredOutputSchema = schema, StructuredOutputStrict = true, UseBackgroundMode = true,
            MaxTokens = outputLimit
        }, state.Request, purpose);
        if (PlanningTransportPolicies.Read(state.Request) is not null)
            request.RequiredReviewCalls = Math.Max(0, (requiredReviewCalls ?? PlanningCompactConstruction.RequiredReviewCalls(state)) -
                    (purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.GlobalPurpose ? 1 : 0));
        request.Prompt = PlanningPromptPresentation.ShareDuplicateContractSchemas(request.Prompt);
        evidenceAddresses = PrepareEvidenceAddresses(state, request);
        if (request.StructuredOutputSchema?["$defs"] is JsonObject)
            request.StructuredOutputSchema = PlanningSchemas.Compact(request.StructuredOutputSchema.AsObject(), preserveDescriptions: true);
        if (purpose == PlanningCompactConstruction.DiscoveryPurpose)
            request.StructuredOutputSchema = PlanningEvidenceSelectionWire.Project(
                PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(request.StructuredOutputSchema!));
        if (purpose == PlanningCompactConstruction.IntentPurpose)
            request.Prompt = PlanningPromptPresentation.PresentIntentDeclarationFields(request.Prompt);
        if (purpose == PlanningCompactConstruction.SelectPurpose)
            request.Prompt = PlanningPromptPresentation.PresentBusinessDependencyFields(request.Prompt);
        var compactPrompt = request.Prompt;
        var groupComposition = purpose is PlanningCompactConstruction.ImplementationPurpose or PlanningCompactConstruction.DiscoveryPurpose &&
            state.Plan?.Groups.Any(group => !state.Construction!.OpenScopes.Contains("/groups/" + group.Id + "/body", StringComparer.Ordinal)) == true &&
            !PlanningCompactConstruction.CompletionOnly(state);
        if (groupComposition)
            request.Prompt = PlanningPromptPresentation.PresentGroupCompositionGuidance(request.Prompt);
        var materialization = purpose == PlanningCompactConstruction.DiscoveryPurpose &&
            state.Construction?.Compact is not null &&
            state.Construction.Evidence?.Any(item => item.Key.StartsWith("description:", StringComparison.Ordinal) && item.Value is JsonObject) == true &&
            !PlanningCompactConstruction.CompletionOnly(state);
        if (materialization)
            request.Prompt = PlanningPromptPresentation.PresentContractMaterializationGuidance(request.Prompt);
        if (purpose == PlanningCompactConstruction.DiscoveryPurpose)
            request.Prompt = PlanningPromptPresentation.PresentDiscoveryResponseFields(request.Prompt, request.StructuredOutputSchema!);
        var compactJson = purpose is PlanningCompactConstruction.DiscoveryPurpose or PlanningConstruction.DraftPurpose;
        if (compactJson) request.Prompt = PlanningPromptPresentation.PresentCompactJsonGuidance(request.Prompt);
        if (purpose == PlanningConstruction.DraftPurpose)
        {
            var beforeInputGuidance = request.Prompt;
            if (PlanningPromptPresentation.OffersRuntimePrerequisite(request.StructuredOutputSchema!))
                request.Prompt = PlanningPromptPresentation.PresentRuntimePrerequisiteGuidance(request.Prompt);
            request.Prompt = PlanningPromptPresentation.PresentInferenceInputGuidance(request.Prompt);
            if (!PlanningTokenThresholds.OptimizationAdvisory(state.Request) &&
                ModelRequestAdmission.EstimateTokens(request) > PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).TargetInputTokens)
                request.Prompt = beforeInputGuidance;
        }
        if (purpose is PlanningCompactConstruction.SelectPurpose or PlanningCompactConstruction.ImplementationPurpose or
            PlanningCompactConstruction.DiscoveryPurpose or PlanningConstruction.DraftPurpose or
            PlanningConstruction.LocalPurpose or PlanningConstruction.GlobalPurpose)
        {
            var beforeFailureGuidance = request.Prompt;
            request.Prompt = PlanningPromptPresentation.PresentFailureObligationGuidance(request.Prompt);
            if (!PlanningTokenThresholds.OptimizationAdvisory(state.Request) &&
                ModelRequestAdmission.EstimateTokens(request) > PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).TargetInputTokens)
                request.Prompt = beforeFailureGuidance;
        }
        string? beforeLifecycleGuidance = null;
        if (purpose is PlanningCompactConstruction.ImplementationPurpose or PlanningCompactConstruction.DiscoveryPurpose or PlanningConstruction.DraftPurpose)
        {
            beforeLifecycleGuidance = request.Prompt;
            request.Prompt = PlanningPromptPresentation.PresentLifecycleScopeGuidance(request.Prompt);
        }
        // Bind the evidence basis after fresh field presentation; issued recovery
        // keeps its exact captured prompt/schema instead of preparing another one.
        var hash = IssuedIdentityHash(request, null);
        request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
            (evidenceAddresses is null ? "" : evidenceAddresses.MapHash + ":") + hash;
        if (beforeLifecycleGuidance is not null && !PlanningTokenThresholds.OptimizationAdvisory(state.Request) &&
            ModelRequestAdmission.EstimateTokens(request) > PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).TargetInputTokens)
        {
            // Measure the complete issued identity as well as prompt and schema.
            request.Prompt = beforeLifecycleGuidance;
            hash = IssuedIdentityHash(request, null);
            request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
                (evidenceAddresses is null ? "" : evidenceAddresses.MapHash + ":") + hash;
        }
        if ((compactJson || groupComposition || materialization) && !PlanningTokenThresholds.OptimizationAdvisory(state.Request) &&
            ModelRequestAdmission.EstimateTokens(request) > PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).MaxInputTokens)
        {
            // Optional guidance never displaces mandatory context or makes an
            // otherwise admissible strict follow-up fail its captured ceiling.
            request.Prompt = compactPrompt;
            hash = IssuedIdentityHash(request, null);
            request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
                (evidenceAddresses is null ? "" : evidenceAddresses.MapHash + ":") + hash;
        }
        if (evidenceAddresses is not null)
        {
            _ = IssuedIdentityHash(request, evidenceAddresses);
            evidenceAddresses.RequestHash = RequestHash(request);
        }
        return request;
    }

    internal static async Task<JsonNode> CallAsync(PlanningSession state, IPlanningRuntime runtime, string purpose, string prompt, JsonObject schema, CancellationToken ct, int? outputLimit = null,
        PlanningCorrectionAttempt? correction = null, Action? issued = null, Action<PlanningModelCall, JsonNode>? received = null)
    {
        ct.ThrowIfCancellationRequested();
        PlanningRequestMeasurement? activeMeasurement = state.PendingCall is { } pendingMeasurement
            ? state.RequestMeasurements?.SingleOrDefault(measurement => measurement.RequestId == pendingMeasurement.Id) : null;
        using var warnings = PlanningTokenThresholds.Observe((stage, kind, current, threshold) =>
        {
            if (activeMeasurement is not null) PlanningInputWarnings.Add(activeMeasurement, stage, kind, current, threshold);
        });
        if (state.PendingCall is null)
        {
            PlanningProgress.EnsureCallAvailable(state);
            EnsureCallAvailable(state);
            var request = PrepareRequest(state, purpose, prompt, schema, outputLimit, null, out var evidenceAddresses);
            schema = request.StructuredOutputSchema!.AsObject();
            var inputTokens = ModelRequestAdmission.EstimateTokens(request);
            var inputLimit = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose);
            PlanningRequestMeasurement? measurement = null;
            {
                var measured = PlanningRequestMeasurements.Measure(request, purpose, inputLimit, state.Construction?.Compact?.CurrentStep?.Id);
                measured.BlockId = state.Construction?.Compact?.CurrentBlockId;
                if (purpose == PlanningConstruction.GlobalPurpose)
                    measured.BaselineHash ??= state.Construction!.Candidate?.BaseHash;
                var retainedMeasurements = state.RequestMeasurements ??= [];
                measurement = retainedMeasurements.SingleOrDefault(m => m.RequestHash == measured.RequestHash);
                if (measurement is null) { measurement = measured; retainedMeasurements.Add(measurement); }
            }
            activeMeasurement = measurement;
            measurement.IssuedOutputTokens = request.MaxTokens;
            PlanningInputWarnings.Record(state, purpose, measurement);
            var violations = PlanningContractValidation.ValidateSchema(schema, strict: true);
            if (violations.Count != 0)
            {
                await RejectMeasurement();
                throw new WorkflowRuntimeException("MODEL_SCHEMA_INVALID",
                    "The response schema is outside the supported structured-output profile. Planning stopped before dispatch without consuming a model call or repair. " + string.Join("; ", violations),
                    details: new JsonObject { ["location"] = "/phases/" + purpose });
            }
            if (inputTokens > inputLimit && !PlanningTokenThresholds.Advisory(state.Request))
            {
                await RejectMeasurement();
                var message = $"The conservative input estimate for the complete {purpose} request (prompt and response schema) is {inputTokens} tokens; the captured limit is {inputLimit}. Planning stopped before dispatch without consuming another model call or repair. Required context must fit the captured phase allowance; retained requests and cumulative budgets remain unchanged.";
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", message,
                    details: new JsonObject { ["location"] = "/phases/" + purpose, ["limit_kind"] = "request_input_allowance",
                        ["limit"] = inputLimit, ["current"] = inputTokens, ["measurement"] = "estimated_tokens", ["unit"] = "tokens",
                        ["stage"] = purpose, ["dispatch_status"] = "not_started" });
            }
            try { if (runtime is WorkflowPlanningRuntime actual) await actual.AdmitAsync(request, inputLimit, purpose, ct, PlanningTokenThresholds.Advisory(state.Request)); }
            catch { await RejectMeasurement(); throw; }
            correction?.Reserve();
            if (measurement is not null) measurement.Admission = "admitted";
            PlanningProgress.Initialize(state);
            state.ModelCalls++;
            if (purpose == "replan" && correction?.Enabled != true) state.ReplanAttempts++;
            state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = purpose, Request = request,
                CorrectionAccounting = correction?.IssuedMode, EvidenceAddresses = evidenceAddresses };
            PlanningInspection.Record(state, purpose, "active", "Exact request prepared and admitted; waiting for the provider.");
            issued?.Invoke();
            await runtime.CheckpointAsync(state, ct);

            async Task RejectMeasurement()
            {
                if (measurement is null) return;
                measurement.Admission = "rejected_before_dispatch";
                await runtime.CheckpointAsync(state, ct);
            }
        }
        var call = state.PendingCall;
        if (call.Purpose != purpose) throw new PlanningConflictException("Complete the pending request before changing planning phases.");
        ValidateEvidenceAddresses(call);
        System.Diagnostics.Activity.Current?.SetTag("gnougo.llm.stage", purpose);
        System.Diagnostics.Activity.Current?.SetTag("gnougo.planning.block_id", state.Construction?.Compact?.CurrentBlockId);
        System.Diagnostics.Activity.Current?.SetTag("gnougo.planning.step_id", state.Construction?.Compact?.CurrentStep?.Id);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        LLMResponse response;
        try { response = await runtime.CallAsync(call.Request, purpose, ct); }
        catch (Exception error)
        {
            var refused = error is WorkflowRuntimeException failure &&
                failure.Code is ErrorCodes.LlmBudgetExceeded or ErrorCodes.LlmBudgetUnverifiable &&
                failure.Details?["dispatch_status"]?.ToString() == "not_started" &&
                (failure.Details["request_id"] is null || failure.Details["request_id"]?.ToString() == call.Id);
            if (activeMeasurement is not null) activeMeasurement.Result = refused ? "financial_refusal_before_dispatch" : "no_retained_completion";
            PlanningInspection.Record(state, purpose, ct.IsCancellationRequested ? "cancelled" : "blocked", refused
                ? "Financial admission refused before provider dispatch; retained reservations remain unchanged."
                : "No verified completion; inspect the retained HTTP attempts.", call.Id);
            throw;
        }
        finally
        {
            if (activeMeasurement is not null)
            {
                activeMeasurement.DurationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                activeMeasurement.DurationBasis = "host_invocation_including_receipt_recovery";
            }
        }
        if (activeMeasurement is not null)
        {
            activeMeasurement.Result = response.CompletionStatus ?? "complete";
            activeMeasurement.IssuedOutputTokens = call.Request.MaxTokens;
            activeMeasurement.InputTokens = Tokens(response.Usage, "input_tokens", "prompt_tokens");
            activeMeasurement.OutputTokens = Tokens(response.Usage, "output_tokens", "completion_tokens");
            activeMeasurement.ReasoningTokens = Tokens(response.Usage, "reasoning_tokens") ??
                Tokens((response.Usage as JsonObject)?["output_tokens_details"], "reasoning_tokens") ??
                Tokens((response.Usage as JsonObject)?["completion_tokens_details"], "reasoning_tokens");
            var admitted = state.RequestMeasurements!.Where(measurement => measurement.Admission == "admitted").ToArray();
            activeMeasurement.CumulativeInputTokens = CumulativeTokens(admitted.Select(measurement => measurement.InputTokens));
            activeMeasurement.CumulativeOutputTokens = CumulativeTokens(admitted.Select(measurement => measurement.OutputTokens));
        }
        PlanningInspection.Record(state, purpose, "received", "Provider response retained; response validation is pending.", call.Id);
        correction?.Received();
        state.PendingCall = null;
        // Estimates remain advisory. Contradictory measured provider usage is a
        // physical/request-contract failure; the receipt and charge stay retained.
        if (state.Request.Options["model_capacity"] is JsonObject capacity)
        {
            var input = activeMeasurement?.InputTokens;
            var output = activeMeasurement?.OutputTokens;
            var maximumOutput = Math.Min(capacity["maxOutputTokens"]!.GetValue<int>(), call.Request.MaxTokens!.Value);
            if (input > capacity["maxInputTokens"]!.GetValue<int>() || output > maximumOutput ||
                input + output > capacity["contextWindowTokens"]!.GetValue<int>())
            {
                if (activeMeasurement is not null) activeMeasurement.Result = "physical_limit_exceeded";
                throw new WorkflowRuntimeException("MODEL_PHYSICAL_LIMIT", "Measured provider usage contradicts the captured model/deployment capacity or issued output allocation. No result was accepted; the exact receipt and charge remain retained.",
                    details: new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output, ["origin"] = capacity["origin"]?.DeepClone(), ["replay_allowed"] = false });
            }
        }
        if (response.CompletionStatus == "output_limit") throw new WorkflowRuntimeException("MODEL_OUTPUT_LIMIT",
            $"The provider returned incomplete output: allocation {call.Request.MaxTokens}, reported output {activeMeasurement?.OutputTokens?.ToString() ?? "unknown"}, " +
            $"reasoning {activeMeasurement?.ReasoningTokens?.ToString() ?? "unknown"}. No fragment was accepted; this response cannot authorize replay or allocation escalation.",
            details: new JsonObject { ["location"] = "/phases/" + purpose, ["limit_kind"] = "provider_output_allocation",
                ["limit"] = call.Request.MaxTokens, ["output_tokens"] = activeMeasurement?.OutputTokens,
                ["reasoning_tokens"] = activeMeasurement?.ReasoningTokens, ["completion_status"] = response.CompletionStatus,
                ["request_id"] = call.Id, ["replay_allowed"] = false });
        if (response.CompletionStatus is "refused" or "failed" or "incomplete")
            throw new WorkflowRuntimeException("MODEL_COMPLETION_REJECTED",
                "The provider's terminal outcome does not authorize accepting its content. The receipt and accounting remain retained; no fragment or business operation was accepted.",
                details: new JsonObject { ["completion_status"] = response.CompletionStatus, ["request_id"] = call.Id, ["replay_allowed"] = false });
        JsonNode json;
        try { json = response.Json?.DeepClone() ?? JsonNode.Parse(response.Text) ?? throw new JsonException("The model returned an empty response."); }
        catch (JsonException)
        {
            if (activeMeasurement is not null) activeMeasurement.Result = "invalid_json";
            throw;
        }
        if (state.Construction is { } construction)
        {
            if (call.Request.MaxTokens is { } maximum)
            {
                long? reported = null;
                if (response.Usage is JsonObject usage)
                    foreach (var name in new[] { "output_tokens", "completion_tokens" })
                        if (usage[name] is JsonValue value && long.TryParse(value.ToString(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var count)) reported = Math.Max(reported ?? 0, count);
                var measured = reported ?? (System.Text.Encoding.UTF8.GetByteCount(json.ToJsonString()) + 2L) / 3L;
                if (measured > maximum && (PlanningTokenThresholds.OptimizationAdvisory(state.Request) ||
                    reported is null && PlanningTokenThresholds.Advisory(state.Request)))
                    PlanningTokenThresholds.Warn(purpose, reported.HasValue ? "reported_response_tokens" : "estimated_response_tokens", measured, maximum);
                if (measured > maximum && !PlanningTokenThresholds.OptimizationAdvisory(state.Request) && (reported.HasValue || !PlanningTokenThresholds.Advisory(state.Request)))
                {
                    if (activeMeasurement is not null) activeMeasurement.Result = "output_allowance_exceeded";
                    throw new WorkflowRuntimeException("MODEL_OUTPUT_LIMIT", "The completed construction response exceeded its issued output allowance; no fragment was accepted.", details: new JsonObject
                    { ["location"] = "/phases/" + purpose, ["limit"] = maximum, ["current"] = measured,
                        ["measurement"] = reported.HasValue ? "reported_output_tokens" : "conservative_serialized_response_tokens", ["unit"] = "tokens", ["stage"] = purpose });
                }
            }
        }
        received?.Invoke(call, json.DeepClone());
        // The issued schema, not today's counters/policy, governs recovered responses.
        var findings = ValidateResponseFindings(json, call.Request.StructuredOutputSchema!);
        if (findings.Count != 0)
        {
            if (activeMeasurement is not null) activeMeasurement.Result = "invalid_response";
            PlanningInspection.Record(state, purpose, "correction", "Response rejected by the exact issued schema.", call.Id);
            PlanningProgress.AssessAttempt(state, purpose == "replan" ? PlanningConstruction.DraftPurpose : purpose,
                json, call.Id, prefix: "invalid:", issuedSchema: CanonicalEvidenceRequest(call).StructuredOutputSchema,
                strategyResponse: InvalidResponseStrategy(json, call.EvidenceAddresses, call.Request.StructuredOutputSchema!), exactIssuedSchema: call.Request.StructuredOutputSchema);
            throw new PlanningResponseException(findings.Select(f => new PlanningDiagnostic("PLANNING_RESPONSE_INVALID",
                f.InstancePointer, f.Message, ValidationStage: purpose)).ToList())
            {
                ReceivedReport = purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.GlobalPurpose &&
                    HybridWorkflowPlanner.ConstructionReviewIssued(call.Request.StructuredOutputSchema) ? json.DeepClone() : null
            };
        }
        state.RejectedProposalHash = null;
        PlanningInspection.Record(state, purpose, "succeeded", "Response satisfies the issued schema; semantic validation and business review remain separate.", call.Id);
        var normalized = NormalizeEvidenceResponse(PlanningEvidenceSelectionWire.CanonicalResponse(json, call.Request.StructuredOutputSchema!), call.EvidenceAddresses);
        return normalized;
    }

    private static long? Tokens(JsonNode? usage, params string[] names)
    {
        if (usage is not JsonObject fields) return null;
        foreach (var name in names)
            if (long.TryParse(fields[name]?.ToString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0) return count;
        return null;
    }

    private static long? CumulativeTokens(IEnumerable<long?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value is null || value.Value < 0 || value.Value > long.MaxValue - total) return null;
            total += value.Value;
        }
        return total;
    }

}

// A known response and its classification are persisted by the same coordinator
// checkpoint. An interruption before it reuses the issued mode and saved receipt;
// it never refunds a historical charge or treats an uncertain dispatch as evidence.
internal sealed class PlanningCorrectionAttempt : IDisposable
{
    private readonly PlanningSession _state;
    private int _mode;
    private bool _received;
    private bool _evidence;
    private bool _disposed;
    internal bool Enabled { get; }
    internal int? IssuedMode => Enabled ? _mode : null;

    internal PlanningCorrectionAttempt(PlanningSession state, bool correcting, bool draft = false)
    {
        _state = state;
        Enabled = state.PendingCall is { } pending ? pending.CorrectionAccounting is not null
            : true;
        _mode = state.PendingCall?.CorrectionAccounting ?? (correcting ? draft ? 3 : 1 : 0);
        if (Enabled && _mode is not (0 or 1 or 3))
            throw new PlanningConflictException("The issued correction accounting mode is unsupported.");
    }

    internal void Reserve() => Reserve(_mode);

    private void Reserve(int mode)
    {
        if (!Enabled || mode == 0) return;
        if (!PlanningModelCalls.HasRepairCapacity(_state) || _state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            mode == 3 && !PlanningModelCalls.HasDraftRepairCapacity(_state, _state.Construction!.Candidate?.Corrections ?? _state.Construction!.DraftCorrections))
            throw new WorkflowRuntimeException("CONSTRUCTION_CORRECTION_ALLOWANCE", "The captured allowance cannot reserve this correction attempt.");
    }

    internal void Received() => _received = true;
    internal void EvidenceAccepted() => _evidence = true;
    internal void Reselect()
    {
        if (!Enabled) return;
        Reserve(3);
        _mode = 3;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Enabled || !_received || _evidence || _mode == 0) return;
        _state.ReplanAttempts++;
        _ = PlanningOptimizationThresholds.Exceeded(_state, "correction", "session_corrections", _state.ReplanAttempts, _state.Request.MaxReplanAttempts);
        if (_mode == 3)
        {
            if (_state.Construction!.Candidate is { } candidate) candidate.Corrections++;
            else _state.Construction.DraftCorrections++;
            _ = PlanningOptimizationThresholds.Exceeded(_state, "correction", "draft_corrections",
                _state.Construction.Candidate?.Corrections ?? _state.Construction.DraftCorrections, _state.Construction.Limits.MaxCorrectionsPerDraft);
        }
    }
}
internal sealed class PlanningResponseException(List<PlanningDiagnostic> diagnostics) : Exception("The model response violated its typed contract.")
{
    internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics;
    // Available only after a complete, output-bounded parsed review response was
    // received. It never represents transport uncertainty or authorizes a retry.
    internal JsonNode? ReceivedReport { get; init; }
}
