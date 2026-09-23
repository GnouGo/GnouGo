using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Semantic planning → grounding → typed validation → lowering → scenarios → approval. Only this coordinator changes session state.</summary>
public sealed class TypedWorkflowPlanner(TimeProvider? timeProvider = null) : IWorkflowPlanner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.SchemaVersion != 8) throw new PlanningConflictException("Unsupported planning session; start a new session.");
        if (session.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed; reload its current revision.");
        if (string.IsNullOrWhiteSpace(session.Request.TenantId) || string.IsNullOrWhiteSpace(session.Request.SessionId) || string.IsNullOrWhiteSpace(session.Request.Prompt))
            throw new ArgumentException("Tenant, session, and prompt are required.");
        if (session.Request.MaxReplanAttempts is < 0 or > 10 || session.Request.MaxModelCalls is < 1 or > 1000) throw new ArgumentException("Invalid planning limits.");
        PlanningGenerationPolicy.Validate(session.Request.Generation);
        PlanningMode.Validate(session.Request.Mode);
        if (command.Kind == "advance" && (PlanningStatus.IsWaiting(session.Status) || PlanningStatus.IsTerminal(session.Status))) return session;
        if (session.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled) throw new PlanningConflictException("The session is closed.");
        var state = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (PlanningBudgetOptions.Parse(state.Request.Options)?.MaxElapsed is { } maximum)
        {
            var remaining = maximum.TotalMilliseconds - state.ActiveMilliseconds;
            if (remaining <= 0) deadline.Cancel(); else deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        }
        if (state.WaitingSinceUtc is { } waiting)
        { state.HumanWaitMilliseconds += Math.Max(0, (_time.GetUtcNow() - waiting).TotalMilliseconds); state.WaitingSinceUtc = null; }
        try
        {
            switch (command.Kind)
            {
                case "advance":
                    await AdvanceAsync(state, runtime, deadline.Token);
                    break;
                case "answer_decision":
                    PlanningDecisions.Answer(state, command.DecisionAnswer ?? throw new ArgumentException("A decision answer is required."), "user");
                    break;
                case "configure_mode":
                    PlanningMode.Validate(command.Mode ?? "");
                    state.Request.Mode = command.Mode!;
                    if (state.PendingDecision is { } pendingDecision && command.Mode == PlanningMode.Auto)
                        PlanningDecisions.Answer(state, new(pendingDecision.Id, pendingDecision.Options.Single(o => o.Preferred).Id), "auto");
                    break;
                case "cancel": state.Status = PlanningStatus.Cancelled; state.ApprovedHash = null; break;
                case "approve":
                    if (state.Status != PlanningStatus.FinalReview || command.ArtifactHash is null || command.ArtifactHash != PlanningArtifactApproval.Hash(state))
                        throw new PlanningConflictException("Approval must target the exact current review revision and artifact.");
                    PlanningArtifactApproval.Verify(state);
                    var approvalFindings = (await runtime.ValidateCatalogAsync(state.Catalog!, deadline.Token)).ToList();
                    if (!approvalFindings.Any(d => d.Required)) approvalFindings.AddRange(await runtime.ValidateAsync(new(state.Yaml!, state.Request, state.Catalog!, PlanningGraphCompiler.CapabilityBindings(state.Graph!)), deadline.Token));
                    if (approvalFindings.Any(d => d.Required)) { state.Diagnostics = approvalFindings; Stop(state); }
                    else { state.ApprovedHash = command.ArtifactHash; state.Status = PlanningStatus.Approved; }
                    break;
                case "revise":
                case "edit_semantic":
                    if (state.PendingCall is not null) throw new PlanningConflictException("Reconcile the pending model request before editing intent.");
                    ArgumentException.ThrowIfNullOrWhiteSpace(command.Text);
                    state.Request.Baseline = state.SemanticPlan;
                    state.Request.Prompt = command.Kind == "edit_semantic" ? command.Text.Trim() : state.Request.Prompt + "\nRequested revision: " + command.Text.Trim();
                    state.PendingDecision = null; state.DecisionContinuation = null;
                    state.RejectedProposalHash = null; state.SemanticPlan = null; state.Grounding = null; state.BindingProgress = null; state.GroundedPlan = null; state.Graph = null; state.Catalog = null; state.Fixtures = null; state.ReplanAttempts = 0;
                    state.Diagnostics.Clear(); state.Scenarios.Clear(); state.Yaml = null; state.ApprovedHash = null; state.Status = PlanningStatus.Generating; state.Phase = PlanningPhase.Semantic;
                    break;
                case "answer":
                    if (state.Status != PlanningStatus.Clarification || state.SemanticPlan is null || command.Answers is null) throw new PlanningConflictException("No clarification is awaiting an answer.");
                    if (command.Answers.Any(a => !state.SemanticPlan.Questions.Any(q => q.Id == a.Key))) throw new ArgumentException("Unknown clarification answer.");
                    foreach (var question in state.SemanticPlan.Questions)
                    {
                        if (!command.Answers.ContainsKey(question.Id) || PlanningContractValidation.ValidateInstance(command.Answers[question.Id], PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(question.AnswerType), state.Catalog!)).Count > 0)
                            throw new ArgumentException("An answer violates the question's schema: " + question.Id);
                        state.Answers.Add(new(question.Question, new() { [question.Id] = command.Answers[question.Id]?.DeepClone() }));
                    }
                    state.SemanticPlan = null; state.Grounding = null; state.BindingProgress = null; state.GroundedPlan = null; state.Graph = null; state.Diagnostics.Clear(); state.Status = PlanningStatus.Generating; state.Phase = PlanningPhase.Semantic;
                    break;
                case "configure_generation":
                    if (state.PendingCall is not null || command.Generation is null) throw new PlanningConflictException("Generation settings cannot replace a pending request.");
                    PlanningGenerationPolicy.Validate(command.Generation); state.Request.Generation = command.Generation;
                    state.Diagnostics.RemoveAll(d => d.Code == "MODEL_INPUT_LIMIT");
                    if (state.Status == PlanningStatus.Stopped) state.Status = PlanningStatus.Generating; state.Phase = PlanningPhase.Semantic;
                    break;
                default: throw new ArgumentException("Unsupported planning command.");
            }
        }
        catch (PlanningDecisionPauseException) { }
        catch (PlanningConflictException) { throw; }
        catch (ArgumentException) when (command.Kind != "advance") { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { state.Diagnostics.Add(new(ErrorCodes.LlmBudgetExceeded, "$", "Active planning time was exhausted.")); Stop(state); }
        catch (PlanningResponseException ex)
        {
            // A rejected replan never erases the errors in the unchanged executable proposal.
            state.Diagnostics = (state.GroundedPlan is null ? [] : state.Diagnostics).Concat(ex.Diagnostics).Distinct().ToList();
            state.Status = PlanningStatus.Generating; Invalidate(state);
        }
        catch (WorkflowRuntimeException ex)
        {
            var location = ex.Details?["location"]?.ToString() ?? "$";
            if (ex.Code == "MODEL_INPUT_LIMIT")
            { state.Diagnostics.RemoveAll(d => d.Code == ex.Code); state.Diagnostics.Add(new(ex.Code, location, ex.Message)); }
            else state.Diagnostics.Add(new(ex.Code, location, ex.Message));
            Stop(state);
        }
        catch (JsonException ex)
        {
            if (state.GroundedPlan is null) state.Diagnostics.Clear();
            state.Diagnostics.Add(new("INTENT_SCHEMA_INVALID", state.GroundedPlan is null ? "$" : "/changes", ex.Message));
            state.Status = PlanningStatus.Generating; Invalidate(state);
        }
        catch (Exception ex)
        {
            state.Diagnostics.Add(new(state.PendingCall is null ? "PLANNING_INVALID" : "MODEL_DISPATCH_UNVERIFIABLE", "$", ex.Message));
            // Only explicit candidate diagnostics are eligible for replanning. Unexpected host failures
            // must not repeat indefinitely or spend model calls on a broken engine state.
            Stop(state);
        }
        state.ActiveMilliseconds += clock.Elapsed.TotalMilliseconds;
        state.Revision++; state.UpdatedAtUtc = _time.GetUtcNow();
        if (PlanningStatus.IsWaiting(state.Status)) state.WaitingSinceUtc = state.UpdatedAtUtc;
        await runtime.CheckpointAsync(state, ct);
        return state;
    }

    private static async Task AdvanceAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        state.Status = PlanningStatus.Generating;
        if (state.PendingCall?.Purpose == "fixtures" || state.PendingCall is null && state.Graph is not null && state.Fixtures is null &&
            state.Diagnostics.Count > 0 && state.Diagnostics.All(d => d.Code == "SCENARIO_FIXTURE_REQUIRED"))
        {
            await ScenarioFixtureGeneration.ApplyAsync(state, runtime, ct); return;
        }
        if (state.PendingCall?.Purpose == "replan" || state.PendingCall is null && state.Diagnostics.Any(d => d.Required))
        {
            if (state.PendingCall is null && state.ReplanAttempts >= state.Request.MaxReplanAttempts) { Stop(state); return; }
            state.Phase = PlanningPhase.Replanning;
            if (state.Diagnostics.Any(d => d.Code == "SEMANTIC_BINDING_BLOCKED")) await SemanticReplanning.ApplyAsync(state, runtime, ct);
            else if (state.BindingProgress is not null) await GroundedBindingBatches.ApplyAsync(state, runtime, ct, replan: true);
            else if (state.GroundedPlan is not null) await GroundedReplanning.ApplyAsync(state, runtime, ct);
            else if (state.Grounding?.Selections is not null)
            {
                var schema = PlanningSchemas.Grounded(state.Grounding.Selections.SelectMany(s => s.CapabilityIds));
                var prompt = CapabilityGrounder.BindingPrompt(state) + "\nReplace the invalid complete binding response. Diagnostics: " + PlanningJsonTransport.Prompt(PlanningJsonTransport.Diagnostics(state.Diagnostics));
                var replacement = await PlanningModelCalls.CallAsync(state, runtime, "replan", prompt, schema, ct);
                state.GroundedPlan = JsonSerializer.Deserialize(replacement, PlanningJsonContext.Default.GroundedPlan)!; state.Diagnostics.Clear();
            }
            else if (state.Grounding is not null && state.Grounding.Pages.FirstOrDefault(p => !state.Grounding.Results.Any(r => r.PageId == p.Id)) is { } incomplete)
            {
                var replacement = await PlanningModelCalls.CallAsync(state, runtime, "replan", CapabilityGrounder.Prompt(state, incomplete), CapabilityGrounder.Schema(incomplete), ct);
                state.Grounding.Results.Add(CapabilityGrounder.Read(incomplete, replacement)); state.Diagnostics.Clear();
            }
            else if (state.Grounding is not null && !state.Diagnostics.Any(d => d.Code == "NONE_OF_THE_ABOVE"))
            {
                await CapabilitySelection.ApplyAsync(state, runtime, ct, "replan"); state.Diagnostics.Clear();
            }
            else await SemanticReplanning.ApplyAsync(state, runtime, ct);
            return;
        }
        if (state.SemanticPlan is null)
        {
            state.Phase = PlanningPhase.Semantic;
            var json = await PlanningDecisions.CallAsync(state, runtime, "semantic", "semantic", "/", [], SemanticPlanning.Prompt(state), SemanticPlanning.Schema(),
                candidate => { var findings = SemanticPlanning.Validate(JsonSerializer.Deserialize(candidate, PlanningJsonContext.Default.SemanticPlan)!); if (findings.Count > 0) throw new PlanningResponseException(findings); }, ct);
            state.SemanticPlan = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SemanticPlan)!;
            state.Diagnostics = SemanticPlanning.Validate(state.SemanticPlan);
            if (state.SemanticPlan.Questions.Count > 0 && state.Diagnostics.Count == 0)
            {
                if (++state.ClarificationRounds > 3) { state.Diagnostics.Add(new("CLARIFICATION_LIMIT", "/questions", "The clarification allowance was exhausted.")); Stop(state); }
                else state.Status = PlanningStatus.Clarification;
            }
            return;
        }
        if (state.Catalog is null) { state.Catalog = await runtime.DiscoverAsync(state.Request, ct); return; }
        state.Grounding ??= CapabilityGrounder.Create(state);
        var page = state.Grounding.Pages.FirstOrDefault(p => !state.Grounding.Results.Any(r => r.PageId == p.Id));
        if (page is not null)
        {
            state.Phase = PlanningPhase.Grounding;
            if (page.CapabilityIds.Count == 0)
                state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(id => new GroundingDecision(id, "none_of_the_above", [], "The authorized catalog has no capabilities.")).ToList()));
            else
            {
                var response = await PlanningModelCalls.CallAsync(state, runtime, "grounding", CapabilityGrounder.Prompt(state, page), CapabilityGrounder.Schema(page), ct);
                state.Grounding.Results.Add(CapabilityGrounder.Read(page, response));
            }
            return;
        }
        var decisions = CapabilityGrounder.Decisions(state);
        state.Diagnostics = decisions.Where(d => d.Outcome == "none_of_the_above" && SemanticPlanning.Actions(state.SemanticPlan).Any(a => a.Id == d.ActionId && SemanticPlanning.External(a)))
            .Select(d => new PlanningDiagnostic("NONE_OF_THE_ABOVE", "/actions/" + d.ActionId, d.Reason, ValidationStage: "grounding")).ToList();
        if (state.Diagnostics.Count > 0) return;
        if (state.Grounding.Selections is null)
        {
            state.Phase = PlanningPhase.Grounding;
            await CapabilitySelection.ApplyAsync(state, runtime, ct); return;
        }
        if (state.GroundedPlan is null)
        {
            state.Phase = PlanningPhase.Binding;
            if (GroundedBindingBatches.Required(state))
            {
                await GroundedBindingBatches.ApplyAsync(state, runtime, ct);
                if (state.GroundedPlan is null) return;
            }
            else
            {
            var ids = state.Grounding.Selections.SelectMany(s => s.CapabilityIds).Distinct();
            var json = await PlanningModelCalls.CallAsync(state, runtime, "binding", CapabilityGrounder.BindingPrompt(state), PlanningSchemas.Grounded(ids), ct);
            state.GroundedPlan = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GroundedPlan)!;
            }
        }
        state.Phase = PlanningPhase.Validation;
        state.Diagnostics = CapabilityGrounder.ValidateBindings(state);
        if (state.Diagnostics.Count > 0) return;
        var validation = GroundedPlanValidator.Validate(state.GroundedPlan, state.Catalog);
        state.Diagnostics = validation.Diagnostics.ToList();
        if (validation.Plan is null) return;
        state.Diagnostics = CapabilityGrounder.ValidateBusinessOutputs(state, validation.Plan);
        if (state.Diagnostics.Count > 0) return;
        state.Graph = PlanningGraphBuilder.Build(validation.Plan);
        state.Phase = PlanningPhase.Scenarios;
        await PlanningValidationPipeline.ValidateAsync(state, runtime, ct);
        if (state.Status == PlanningStatus.FinalReview) state.Phase = PlanningPhase.Review;
    }
    private static void Invalidate(PlanningSession state) { state.Yaml = null; state.ApprovedHash = null; }
    private static void Stop(PlanningSession state) { state.Status = PlanningStatus.Stopped; Invalidate(state); }
}
