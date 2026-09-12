using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>The sole planner: explicit session transitions over a typed graph.</summary>
public sealed class TypedWorkflowPlanner(TimeProvider? timeProvider = null) : IWorkflowPlanner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly PlanningWorkflowConstruction _construction = new();
    private readonly PlanningValidationPipeline _validation = new();

    public async Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(runtime);
        if (snapshot.SchemaVersion != 5) throw new PlanningConflictException("Unsupported planning snapshot. Start a new session.");
        if (snapshot.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed. Reload the current revision.");
        if (string.IsNullOrWhiteSpace(snapshot.Request.TenantId) || string.IsNullOrWhiteSpace(snapshot.Request.SessionId) || string.IsNullOrWhiteSpace(snapshot.Request.Prompt))
            throw new ArgumentException("Tenant, session, and intent are required.");
        if (snapshot.Request.MaxConcurrency is < 1 or > 16 || snapshot.Request.MaxRepairsPerWorkflowGate is < 0 or > 10) throw new ArgumentException("Invalid planning limits.");
        if (command.Kind is not ("advance" or "answer" or "accept_behavior" or "approve" or "revise" or "edit_intent" or "configure_generation" or "cancel"))
            throw new ArgumentException("Unsupported planning command.");
        PlanningGenerationPolicy.Validate(snapshot.Request.Generation);
        var state = PlanningContext.Clone(snapshot);
        if (command.Kind == "advance" && (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status))) return state;
        if (state.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled) throw new PlanningConflictException("The planning session is closed.");
        var clock = Stopwatch.StartNew();
        var callerCancellation = ct;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = deadline.Token;
        if (state.WaitingSinceUtc is { } waiting)
        { state.HumanWaitMilliseconds += Math.Max(0, (_time.GetUtcNow() - waiting).TotalMilliseconds); state.WaitingSinceUtc = null; }
        try
        {
            if (command.Kind is "advance" or "approve" && PlanningBudgetOptions.Parse(state.Request.Options)?.MaxElapsed is { } maximum)
            {
                var remaining = maximum.TotalMilliseconds - state.ActiveMilliseconds;
                if (remaining <= 0) deadline.Cancel(); else deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            }
            ct.ThrowIfCancellationRequested();
            switch (command.Kind)
            {
                case "advance": await AdvancePhaseAsync(state, runtime, ct); break;
                case "cancel": state.Status = PlanningStatus.Cancelled; state.ApprovedHash = null; state.PendingCommand = null; break;
                case "answer":
                    if (state.Status != PlanningStatus.Clarification || state.Intent.Question is null || command.Answers is null)
                        throw new PlanningConflictException("No matching clarification is pending.");
                    if (state.Intent.Question.Fields is null || state.Intent.Question.Fields.Any(f => f.Required &&
                        (command.Answers[f.Name] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))) ||
                        command.Answers.Any(a => !state.Intent.Question.Fields.Any(f => f.Name == a.Key)))
                        throw new PlanningConflictException("Submit a nonempty answer for every pending question.");
                    if (state.Outcome is not PlanningNeedUserClarification clarification ||
                        PlanningContractValidation.ValidateInstance(command.Answers, clarification.Decision.AnswerSchema).Count != 0)
                        throw new PlanningConflictException("The answer does not satisfy the current typed business question.");
                    var acceptedAnswers = command.Answers.DeepClone().AsObject();
                    foreach (var field in state.Intent.Question.Fields)
                        if (field.OptionDefinitions?.SingleOrDefault(o => o.Value == acceptedAnswers[field.Name]?.ToString()) is { } selected)
                            acceptedAnswers[field.Name] = selected.Description;
                    state.Intent.Answers.Add(new(state.Intent.Question.Prompt + "\n" + string.Join("\n", state.Intent.Question.Fields.Select(f => f.Description ?? f.Name)), acceptedAnswers));
                    state.Outcome = null; state.TechnicalStop = null;
                    state.Intent.Question = null; state.Intent.Assessment = new(); state.Intent.Checked = false; state.Preparation = null; state.PreparationCheckpoint = null;
                    state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Intent; break;
                case "accept_behavior":
                    RequireReview(state, command, PlanningStatus.BehaviorReview);
                    if (state.BehaviorPlan is null || state.ArtifactHash != PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan)) throw new PlanningConflictException("The behavior changed before acceptance.");
                    var behaviorFindings = PlanningBehaviorPlans.Validate(state.BehaviorPlan, state.Preparation!);
                    if (behaviorFindings.Any(d => d.Required)) { state.Diagnostics = behaviorFindings.ToList(); state.Status = PlanningStatus.Stopped; break; }
                    state.ApprovedBehaviorHash = state.ArtifactHash;
                    PlanningGraphSkeleton.Create(state);
                    PlanningContext.InvalidateArtifact(state);
                    state.CurrentPhase = PlanningPhase.Dataflow; state.Status = PlanningStatus.Generating; break;
                case "approve": await ApproveAsync(state, command, runtime, ct); break;
                case "revise":
                case "edit_intent":
                    if (string.IsNullOrWhiteSpace(command.Text)) throw new ArgumentException("A revision requires intent text.");
                    if (state.Construction.PendingCalls.Count != 0) throw new PlanningConflictException("Reconcile pending model requests before revising their contracts.");
                    if (command.Kind == "edit_intent" && state.ApprovedBehaviorHash is not null) throw new PlanningConflictException("Use a behavior revision after acceptance.");
                    var retainedBehavior = command.Kind == "revise" ? state.BehaviorAssessment.Candidate?.DeepClone().AsObject() ??
                        (state.BehaviorPlan is null ? null : JsonSerializer.SerializeToNode(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject()) : null;
                    var reviewedBaseline = state.BehaviorPlan is not null && state.ApprovedBehaviorHash == PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan)
                        ? JsonSerializer.Serialize(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan) : null;
                    PlanningIntentAssessment.ArchiveIntent(state);
                    state.Request.Prompt = command.Kind == "edit_intent" ? command.Text.Trim() : state.Request.Prompt + "\n\nRequested revision:\n" + command.Text.Trim();
                    if (command.Kind == "revise")
                    {
                        state.Request.Prompt += string.Concat(state.Intent.Answers.Select(a => "\nHuman clarification: " + a.Question + "\n" + a.Answers.ToJsonString()));
                        if (state.Graph is not null) state.Request.Baseline = PlanningContext.Clone(state.Graph);
                    }
                    ResetForIntent(state);
                    if (retainedBehavior is not null)
                    {
                        state.BehaviorAssessment.Candidate = retainedBehavior;
                        state.BehaviorRevision = new() { Text = command.Text.Trim(), ReviewedBaselineBehavior = reviewedBaseline };
                    }
                    break;
                case "configure_generation":
                    if (command.Generation is null || !(PlanningStatus.IsWaiting(state.Status) || state.Status is PlanningStatus.Stopped or PlanningStatus.Failed or PlanningStatus.Unsupported)) throw new PlanningConflictException("Generation settings require a paused session.");
                    if (state.Construction.PendingCalls.Count != 0) throw new PlanningConflictException("Reconcile pending requests before changing generation settings.");
                    PlanningGenerationPolicy.Validate(command.Generation);
                    state.GenerationHistory.Add(new(state.Revision, state.Request.Generation)); state.Request.Generation = command.Generation;
                    state.ApprovedHash = null;
                    if (state.Status == PlanningStatus.FinalReview) { PlanningContext.InvalidateArtifact(state); state.Status = PlanningStatus.Validating; }
                    break;
                default: throw new ArgumentException("Unsupported planning command.");
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (PlanningConflictException) { throw; }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        { PlanningContext.Stop(state, ErrorCodes.LlmBudgetExceeded, "The active planning time budget was exhausted. Pending dispatches must be reconciled before further work."); }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested) { throw; }
        catch (PlanningSemanticReview.SemanticAssessmentException error)
        { state.Diagnostics = error.Diagnostics; state.Status = PlanningStatus.Stopped; state.CurrentPhase = "semantic_review"; PlanningContext.InvalidateArtifact(state); }
        catch (WorkflowRuntimeException error) when (error.Code == "PLANNING_CLARIFICATION_REQUIRED")
        {
            var question = error.Details?["question"] is { } json ? JsonSerializer.Deserialize(json, PlanningJsonContext.Default.HumanInputRequest) : null;
            var count = question?.Fields?.Count ?? 0;
            if (state.Outcome is not PlanningNeedUserClarification || question is null || count == 0 || state.Intent.Forms >= 3 || state.Intent.Questions + count > 15)
                PlanningContext.Stop(state, "CLARIFICATION_LIMIT", "Required clarification cannot be completed within the remaining allowance.");
            else { state.Intent.Question = question; state.Intent.Forms++; state.Intent.Questions += count; state.Status = PlanningStatus.Clarification; }
        }
        catch (WorkflowRuntimeException error) when (state.CurrentPhase == PlanningPhase.Capabilities)
        {
            state.Diagnostics = PlanningPreparationDiagnostics.FromException(error);
            state.Status = error.Code == ErrorCodes.CapabilityPreflightUnavailable ? PlanningStatus.Unsupported : PlanningStatus.Stopped;
            if (state.PreparationCheckpoint is not null) state.PreparationCheckpoint.Diagnostics = state.Diagnostics.ToList();
            PlanningContext.InvalidateArtifact(state);
        }
        catch (PlanningHoleUnavailableException error)
        { PlanningContext.Stop(state, "HOLE_DOMAIN_UNRESOLVED", error.Message, error.Location); }
        catch (LLMClientException error)
        {
            state.Diagnostics = [new("LLM_PROVIDER_" + error.Kind.ToString().ToUpperInvariant(), "$",
                error.Message + (error.StatusCode is { } status ? " HTTP status: " + status + "." : "") +
                (error.SafeProviderCode is { } code ? " Provider code: " + code + "." : ""))];
            state.Status = PlanningStatus.Stopped; PlanningContext.InvalidateArtifact(state);
        }
        catch (Exception error)
        {
            var code = error is WorkflowRuntimeException flow ? flow.Code : error is LLMClientException ? "MODEL_TRANSPORT_FAILURE" : "PLANNING_INVALID";
            PlanningContext.Stop(state, code, error.Message);
        }
        PlanningOutcomes.Refresh(state);
        state.ActiveMilliseconds += clock.Elapsed.TotalMilliseconds;
        PlanningConvergence.Refresh(state);
        state.Revision++; state.UpdatedAtUtc = _time.GetUtcNow();
        if (PlanningStatus.IsWaiting(state.Status)) state.WaitingSinceUtc = state.UpdatedAtUtc;
        state.Events.Add(new("transition", PlanningPhase.Resolve(state), state.UpdatedAtUtc));
        await runtime.CheckpointAsync(state, callerCancellation);
        return state;
    }

    private async Task AdvancePhaseAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.Status == PlanningStatus.Created)
        {
            if (!state.Intent.Checked)
            {
                state.CurrentPhase = PlanningPhase.Intent;
                await new PlanningIntentAssessment(_time).AssessAsync(state, runtime, ct);
                if (state.Status == PlanningStatus.Created) state.Intent.Checked = true;
            }
            else if (state.Preparation is null)
            {
                state.CurrentPhase = PlanningPhase.Capabilities;
                var progress = await runtime.PrepareAsync(state, ct);
                state.PreparationCheckpoint = progress.Checkpoint; state.Preparation = progress.Preparation;
            }
            else await new PlanningBehaviorAssessment(_time).AssessAsync(state, runtime, ct);
            return;
        }
        if (state.Status == PlanningStatus.Generating)
        {
            if (state.Construction.Dataflow is null)
            { state.CurrentPhase = PlanningPhase.Dataflow; PlanningDataflowResolver.Resolve(state); return; }
            if (state.CurrentPhase == PlanningPhase.Repair)
            {
                if (state.Construction.Candidates.Count > 0) await new PlanningHoleRepair().AdvanceAsync(state, runtime, ct);
                else await new PlanningTypedRepair(_validation).AdvanceAsync(state, runtime, ct);
                return;
            }
            state.CurrentPhase = PlanningPhase.Construction;
            await _construction.AdvanceAsync(state, runtime, ct);
            return;
        }
        if (state.Status == PlanningStatus.Validating)
        { state.CurrentPhase = PlanningStatus.Validating; await _validation.AdvanceAsync(state, runtime, ct); return; }
        throw new PlanningConflictException("The current phase cannot advance.");
    }

    private async Task ApproveAsync(PlanningSnapshot state, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        RequireReview(state, command, PlanningStatus.FinalReview);
        PlanningArtifactApproval.Verify(state);
        var yaml = state.Yaml!;
        var findings = await runtime.ValidateCatalogAsync(state.Preparation!, ct);
        if (findings.Count == 0) findings = await runtime.ValidateAsync(new(yaml, PlanningContext.EffectiveRequest(state), state.Preparation!, PlanningGraphCompiler.CapabilityBindings(state.Graph!)), ct);
        if (findings.Any(d => d.Required))
        { state.Diagnostics = findings.ToList(); PlanningContext.Stop(state, "GOVERNING_CONTRACT_REVIEW_REQUIRED", "Current contracts require renewed review before approval."); return; }
        state.ApprovedHash = state.ArtifactHash; state.Status = PlanningStatus.Approved;
        state.Outcome = new PlanningValidWorkflow(state.ArtifactHash!);
    }

    private static void RequireReview(PlanningSnapshot state, PlanningCommand command, string status)
    {
        if (state.Status != status || string.IsNullOrWhiteSpace(command.ArtifactHash) || command.ArtifactHash != state.ArtifactHash)
            throw new PlanningConflictException("Approval must target the exact current review revision and hash.");
    }
    private static void ResetForIntent(PlanningSnapshot state)
    {
        state.Outcome = null; state.TechnicalStop = null;
        state.Intent.Checked = false;
        state.Intent.Question = null; state.Intent.Assessment = new(); state.Intent.Answers.Clear();
        state.Preparation = null; state.PreparationCheckpoint = null; state.BehaviorPlan = null; state.ApprovedBehaviorHash = null;
        state.BehaviorAssessmentCalls = 0; state.BehaviorAssessment = new(); state.Graph = null; state.Diagnostics.Clear();
        state.BehaviorRevision = null;
        state.Construction = new() { ModelSequence = state.Construction.ModelSequence };
        state.Validation = new(); state.PendingCommand = null; state.ReviewMarkdown = null;
        PlanningContext.InvalidateArtifact(state);
        state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Intent;
    }
}
