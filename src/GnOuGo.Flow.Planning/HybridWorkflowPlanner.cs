using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>One bounded planning loop: discover, propose tasks, compile, validate, revise, review.</summary>
public sealed partial class HybridWorkflowPlanner(TimeProvider? timeProvider = null) : IWorkflowPlanner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.SchemaVersion != 11) throw new PlanningConflictException("This planning format is no longer executable. Regenerate and approve the workflow.");
        if (session.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed. Reload its revision.");
        if (string.IsNullOrWhiteSpace(session.Request.TenantId) || string.IsNullOrWhiteSpace(session.Request.SessionId) || string.IsNullOrWhiteSpace(session.Request.Prompt))
            throw new ArgumentException("Tenant, session identity and request are required.");
        if (session.Request.MaxModelCalls is < 1 or > 1000 || session.Request.MaxReplanAttempts < 0 ||
            session.Request.MaxReplanAttempts > (session.Request.Options["construction_preset"]?.ToString() == PlanningConstructionPresets.Large ? 64 : 10))
            throw new ArgumentException("Invalid planning limits.");
        PlanningMode.Validate(session.Request.Mode);
        PlanningGenerationPolicy.Validate(session.Request.Generation);
        if (command.EditablePaths is not null && (command.Kind != "revise" || command.PreserveRequirements != true))
            throw new ArgumentException("EditablePaths requires an explicit implementation revision with preserveRequirements: true.");
        if (command.Kind == "advance" && (PlanningStatus.IsWaiting(session.Status) || PlanningStatus.IsTerminal(session.Status))) return session;
        if (session.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled)
            throw new PlanningConflictException("This planning session is closed.");
        if (command.Kind is "configure_generation" or "revise" && PlanningModelCalls.RemainingCalls(session) == 0)
            throw new PlanningConflictException("The session model-call budget is exhausted. Token settings and revisions cannot extend it. Start a new planning session; retained requests and accounting remain unchanged.");
        var state = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningSessionCreation.Capture(state, session);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (PlanningBudgetOptions.Parse(state.Request.Options)?.MaxElapsed is { } maximum)
        {
            var remaining = maximum.TotalMilliseconds - state.ActiveMilliseconds;
            if (remaining <= 0) deadline.Cancel(); else deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        }
        var elapsed = Stopwatch.StartNew();
        if (state.WaitingSinceUtc is { } waiting)
        { state.HumanWaitMilliseconds += Math.Max(0, (_time.GetUtcNow() - waiting).TotalMilliseconds); state.WaitingSinceUtc = null; }
        try
        {
            // Recovery must not operate on identities which no longer satisfy the
            // semantic contract, even if an older request/approval was persisted.
            if (command.Kind != "cancel" && state.Plan is { } recovered && TaskPlanCompiler.IdentityDiagnostics(recovered, includeReferences: false) is { Count: > 0 } invalidIdentities)
            { state.Diagnostics = invalidIdentities.ToList(); Invalidate(state); Stop(state); }
            else switch (command.Kind)
            {
                case "advance": await AdvancePlanAsync(state, runtime, deadline.Token); break;
                case "approve":
                    if (state.Status != PlanningStatus.FinalReview || command.ArtifactHash is null || command.ArtifactHash != state.ComputeArtifactHash())
                        throw new PlanningConflictException("Approval must identify the exact current artifact.");
                    var approvalReview = await PlanningApprovalReviews.ResolveAsync(state, command, runtime, deadline.Token);
                    PlanningArtifactApproval.Verify(state, approvalReview);
                    var requirements = state.Requirements?.Outcomes ?? [];
                    var reviewed = command.ReviewedRequirementIds ?? [];
                    if (reviewed.Count != requirements.Count || reviewed.Distinct(StringComparer.Ordinal).Count() != reviewed.Count ||
                        !reviewed.ToHashSet(StringComparer.Ordinal).SetEquals(requirements.Select(r => r.Id)))
                        throw new PlanningConflictException("REQUIREMENTS_REVIEW_REQUIRED: Compare the original request and revisions with every accepted requirement and actual task, including omitted actions, outputs, extra effects, iterations and cleanup. Confirmed violations require revision; explicitly acknowledged uncertainty remains unverified.");
                    state.Diagnostics = (await runtime.ValidateCatalogAsync(state.Catalog!, deadline.Token)).ToList();
                    if (state.Diagnostics.Any(d => d.Required)) Stop(state);
                    else
                    {
                        state.ValidationResults.AddRange(requirements.Select(r => new PlanningValidationResult("requirement:" + r.Id,
                            approvalReview is null ? "human_reviewed" : "delegated_test_reviewed", r.Description +
                            (approvalReview is null ? " — explicitly reviewed against this artifact" : " — reviewed by the explicitly delegated isolated-test reviewer") +
                            "; unverified coverage remains unverified and execution has not been observed.", [])));
                        state.ApprovalReview = approvalReview;
                        state.ApprovedHash = command.ArtifactHash; state.Status = PlanningStatus.Approved;
                    }
                    break;
                case "choose":
                    var choicePlan = state.Construction?.Candidate?.Plan ?? state.Plan;
                    if (state.Status != PlanningStatus.Clarification || command.Selections is null || choicePlan is null || state.PendingQuestions is not null)
                        throw new PlanningConflictException("No business choice is awaiting selection.");
                    var pending = choicePlan.Choices.Where(c => c.Selected is null).ToArray();
                    if (!command.Selections.Select(p => p.Key).Order().SequenceEqual(pending.Select(c => c.Id).Order()))
                        throw new ArgumentException("Select exactly the pending choices.");
                    foreach (var choice in pending)
                    {
                        var selected = command.Selections[choice.Id]?.GetValue<string>();
                        if (!choice.Alternatives.Any(a => a.Id == selected)) throw new ArgumentException("Select a declared business alternative.");
                        choice.Selected = selected;
                    }
                    Invalidate(state);
                    if (state.Construction is { Complete: false } constructionChoice)
                    {
                        if (constructionChoice.Candidate is { } candidateChoice)
                        {
                            candidateChoice.Hash = PlanningConstruction.Hash(candidateChoice.Plan);
                            candidateChoice.BusinessReviewed = false;
                            await ValidateConstructionCandidateAsync(state, runtime, deadline.Token);
                        }
                        state.Status = PlanningStatus.Generating;
                    }
                    else await CompileAsync(state, runtime, deadline.Token);
                    break;
                case "answer":
                    PlanningClarifications.Answer(state, command); break;
                case "configure_mode":
                    PlanningMode.Validate(command.Mode ?? ""); state.Request.Mode = command.Mode!;
                    if (state.Status == PlanningStatus.Clarification && state.PendingQuestions is null && state.Request.Mode == PlanningMode.Auto)
                    {
                        if (state.Construction is { Complete: false })
                        {
                            foreach (var choice in state.GetChoices().Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
                            if (state.Construction.Candidate is { } automaticChoice)
                            {
                                automaticChoice.Hash = PlanningConstruction.Hash(automaticChoice.Plan);
                                await ValidateConstructionCandidateAsync(state, runtime, deadline.Token);
                            }
                            state.Status = PlanningStatus.Generating;
                        }
                        else await CompileAsync(state, runtime, deadline.Token);
                    }
                    break;
                case "configure_generation":
                    if (state.PendingCall is not null || state.PendingQuestions is not null || command.Generation is null) throw new PlanningConflictException("Generation settings cannot replace a pending call or clarification.");
                    if (state.Construction is not null && (command.Generation.MaxInputTokensPerRequest > state.Request.Generation.MaxInputTokensPerRequest ||
                        command.Generation.MaxOutputTokens > state.Request.Generation.MaxOutputTokens))
                        throw new PlanningConflictException("Construction request ceilings were captured at creation. A larger allowance requires an explicitly configured new session; existing accounting is retained.");
                    PlanningGenerationPolicy.Validate(command.Generation); state.Request.Generation = command.Generation;
                    state.Diagnostics.RemoveAll(d => d.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT");
                    state.Status = PlanningStatus.Generating; break;
                case "revise":
                    if (state.PendingCall is not null) throw new PlanningConflictException("Reconcile the pending model request before revising.");
                    ArgumentException.ThrowIfNullOrWhiteSpace(command.Text);
                    if (command.PreserveRequirements == true && (state.Requirements is null ||
                        command.ArtifactHash != state.ComputeArtifactHash()))
                        throw new PlanningConflictException("A correction must retain current accepted requirements and identify the exact artifact, when present.");
                    if (command.EditablePaths is not null) TaskPlanRevisions.ValidateEditablePaths(state, command.EditablePaths);
                    if (state.Construction is not null)
                    {
                        ReviseConstruction(state, command);
                        PlanningProgress.RecordOriginal(state, command.Text!);
                        break;
                    }
                    PlanningClarifications.Revise(state, command.PreserveRequirements == true, command.EditablePaths); break;
                case "cancel": state.Status = PlanningStatus.Cancelled; state.ApprovedHash = null; break;
                default: throw new ArgumentException("Unsupported planning command.");
            }
        }
        catch (PlanningConflictException) { throw; }
        catch (ArgumentException) when (command.Kind != "advance") { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { state.Diagnostics.Add(new(ErrorCodes.LlmBudgetExceeded, "/", "Active planning time was exhausted.")); Stop(state); }
        catch (PlanningResponseException ex)
        {
            state.Diagnostics = state.Plan is not null && state.RevisionScope.Count > 0
                ? state.Diagnostics.Concat(ex.Diagnostics).Distinct().ToList() : ex.Diagnostics;
            Invalidate(state); state.Status = state.EditablePaths is null ? PlanningStatus.Generating : PlanningStatus.Stopped;
            if (ex.Diagnostics.Any(diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS"))
                Stop(state);
            if (state.Construction is { Candidate: null, LastProposalHash: { } proposalHash } construction &&
                !construction.FailedCandidates.Contains(proposalHash, StringComparer.Ordinal)) construction.FailedCandidates.Add(proposalHash);
        }
        catch (WorkflowRuntimeException ex)
        {
            var location = ex.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT" or "DISCOVERY_INCOMPLETE" or "DISCOVERY_NOT_ALLOWED"
                ? ex.Details?["location"]?.GetValue<string>() ?? "/" : "/";
            if (!state.Diagnostics.Any(d => d.Code == ex.Code && d.Location == location))
                state.Diagnostics.Add(new(ex.Code, location, ex.Message));
            Stop(state);
        }
        catch (LLMClientException ex)
        {
            // Never surface exception messages or provider bodies. The integration supplies
            // a redacted classification; the pending reservation remains durable evidence.
            var detail = $"{ex.Kind}; retryable: {ex.Retryable.ToString().ToLowerInvariant()}";
            if (ex.StatusCode is { } status) detail += $"; HTTP {status}";
            if (ex.SafeProviderCode is { } code) detail += $"; provider code: {code}";
            state.Diagnostics.Add(new(ex.IsRequestRejected ? ErrorCodes.ModelRequestRejected : "MODEL_DISPATCH_UNVERIFIABLE", "/",
                ex.IsRequestRejected
                    ? $"The model provider rejected the request ({detail}). Correct the request or provider configuration, then start a new planning session."
                    : $"The model request has no retained completion ({detail}). Usage remains unconfirmed."));
            Stop(state);
        }
        catch (JsonException)
        { state.Diagnostics = [new("PLANNING_RESPONSE_INVALID", "/", "The response does not satisfy the issued planning contract.")]; Stop(state); }
        catch (Exception)
        { state.Diagnostics.Add(new(state.PendingCall is null ? "PLANNING_HOST_FAILURE" : "MODEL_DISPATCH_UNVERIFIABLE", "/", "Planning could not safely complete this operation.")); Stop(state); }
        if (command.Kind == "advance") PlanningProgress.Record(state);
        state.ActiveMilliseconds += elapsed.Elapsed.TotalMilliseconds;
        state.Revision++; state.UpdatedAtUtc = _time.GetUtcNow();
        if (PlanningStatus.IsWaiting(state.Status)) state.WaitingSinceUtc = state.UpdatedAtUtc;
        await runtime.CheckpointAsync(state, ct);
        return state;
    }

    private static Task AdvancePlanAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.Construction is null) throw new PlanningConflictException("The active construction journal is missing. Start a new session.");
        return AdvanceConstructionAsync(state, runtime, ct);
    }

    private static async Task CompileAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var plan = state.Plan!;
        if (PlanningClarifications.InputFindings(state) is { Count: > 0 } inputFindings)
        { state.Diagnostics = inputFindings; state.Graph = null; Stop(state); return; }
        var selectedOperations = TaskPlanRevisions.Tasks(plan).Where(t => t.Kind == "operation").Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in state.Catalog!.Capabilities.Where(c => c.Kind != "registered" && !selectedOperations.Contains(TaskOperations.Describe(c).Id)))
            if (!state.Discovery.Resolved.Any(c => c.Id == capability.Id && c.Version == capability.Version)) state.Discovery.Resolved.Add(capability);
        // Only selected contracts authorize execution. Discovery receipts retain resolved contracts across repairs.
        state.Catalog!.Capabilities.RemoveAll(c => !selectedOperations.Contains(TaskOperations.Describe(c).Id) && c.Kind != "registered");
        // Validate the recommendation by compiling it before either automatic selection or a human pause.
        var candidate = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        foreach (var choice in candidate.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var compilation = new TaskPlanCompiler().Compile(candidate, state.Catalog!, state.Request);
        if (compilation.Diagnostics.Count > 0) { SemanticFailure(state, compilation.Diagnostics); return; }
        if (plan.Choices.Any(c => c.Selected is null))
        {
            state.Diagnostics.Clear(); state.Status = PlanningStatus.Clarification; return;
        }
        var graph = compilation.Graph!;
        var outputFindings = PlanningClarifications.OutputFindings(state, graph);
        var coverage = PlanningCommitments.Assess(state);
        foreach (var result in coverage)
            Activity.Current?.AddEvent(new("planning.coverage.checked", tags: new ActivityTagsCollection
            { ["clause"] = result.Id, ["outcome"] = result.Outcome, ["diagnostics"] = string.Join(',', result.Diagnostics.Select(d => d.Code)) }));
        var violations = coverage.SelectMany(r => r.Diagnostics).Where(d => d.Required).ToList();
        violations = outputFindings.Concat(violations).Distinct()
            .OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ToList();
        if (violations.Count > 0)
        {
            SemanticFailure(state, violations);
            Stop(state); // Only the captured correction policy authorizes repairing new coverage obligations.
            state.ValidationResults = coverage;
            return;
        }
        var findings = PlanningGeneratedGraph.Validate(graph, state.Catalog!, TaskPlanCompiler.UsesCompactBindings(state.Request)).Select(compilation.Locate).ToList();
        if (findings.Count > 0)
        {
            state.Diagnostics = findings.Select(d => d with { Code = "TASK_COMPILER_VALIDATION", Message = d.Code + ": " + d.Message }).ToList();
            Stop(state); return;
        }
        PlanningConfirmationGuards.Apply(graph, state.Catalog!);
        findings = PlanningExecutableValidation.Validate(graph, state.Catalog!).Select(compilation.Locate).ToList();
        if (findings.Count == 0)
        {
            string yaml;
            try
            {
                yaml = new PlanningGraphCompiler().Compile(graph, state.Catalog!, state.Request.Name, TaskPlanCompiler.UsesNormalExports(state.Request), TaskPlanCompiler.UsesFusedBindings(state.Request), TaskPlanCompiler.UsesConsumerBindings(state.Request), TaskPlanCompiler.UsesDirectProjections(state.Request), TaskPlanCompiler.UsesNativeMappings(state.Request), TaskPlanCompiler.UsesReadableMappings(state.Request), TaskPlanCompiler.UsesLeanMappings(state.Request), TaskPlanCompiler.UsesCompactProjections(state.Request), TaskPlanCompiler.UsesInlineConsumers(state.Request), TaskPlanCompiler.UsesDirectValues(state.Request), TaskPlanCompiler.UsesCheckedControls(state.Request), TaskPlanCompiler.UsesReadableBranches(state.Request), TaskPlanCompiler.UsesSimplifiedLocals(state.Request));
            }
            catch (GnOuGo.Flow.Core.Compilation.WorkflowCompilationException ex)
            {
                state.Diagnostics = ex.Errors.Select(e => new PlanningDiagnostic(e.Code,
                    "workflow:" + e.WorkflowName + "/step:" + e.StepId + "/field:" + e.Field, e.Message))
                    .Select(d => compilation.Locate(PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph)))
                    .Select(d => d with { Code = "TASK_COMPILER_VALIDATION", Message = d.Code + ": " + d.Message }).ToList();
                Stop(state); return;
            }
            findings.AddRange((await runtime.ValidateAsync(new(yaml, state.Request, state.Catalog!, PlanningGraphCompiler.CapabilityBindings(graph)), ct))
                .Select(d => compilation.Locate(PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph))));
            if (findings.Count == 0)
            {
                state.Graph = graph; state.Yaml = yaml; state.ApprovedHash = null;
                var unseen = state.Discovery.Sources.Count(s => state.Discovery.Pages.All(p => p.SourceId != s.Id));
                if (unseen > 0) state.Discovery.Limitations.Add($"Discovery is incomplete: {unseen} sources were not inspected.");
                if (state.Discovery.Pages.Any(p => p.NextCursor is { } next && !state.Discovery.Pages.Any(seen => seen.SourceId == p.SourceId && seen.Cursor == next && seen.Query == p.Query && seen.ProducedArtifactKind == p.ProducedArtifactKind)))
                    state.Discovery.Limitations.Add("Additional operation pages remain uninspected.");
                state.Discovery.Limitations = state.Discovery.Limitations.Distinct(StringComparer.Ordinal).ToList();
                state.Diagnostics.Clear(); state.ValidationResults = [new("static", "passed", "Business bindings, contracts and control flow validated. External execution has not been observed.", [])];
                state.ValidationResults.AddRange(PlanningReviewFormatter.Operations(state));
                state.ValidationResults.AddRange(PlanningInputReadiness.Assess(compilation, 2, state.Plan));
                state.ValidationResults.AddRange(coverage);
                state.Status = PlanningStatus.FinalReview;
                state.Phase = PlanningPhase.Review; return;
            }
        }
        // A valid semantic contract must compile to valid plumbing. Never ask the model to repair compiler output.
        state.Diagnostics = findings.Select(d => d with { Code = "TASK_COMPILER_VALIDATION", Message = d.Code + ": " + d.Message }).ToList();
        Stop(state);
    }

    private static void SemanticFailure(PlanningSession state, IReadOnlyList<PlanningDiagnostic> findings)
    {
        state.Diagnostics = findings.ToList(); state.Graph = null;
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan!, findings).ToList();
        Invalidate(state);
        var structural = PlanningStructuralRepair.Slots(state, PlanningSchemas.TaskPlanGrammar(state)["$defs"]!.AsObject());
        state.RevisionScope.RemoveAll(p => findings.Any(d => d.Code == "TASK_KIND_INVALID" && d.Location == p) &&
            !structural.Any(s => s.Kind == "task" && p == s.Location + "/kind"));
        var unsupported = findings.Where(d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING").Any(d =>
            !structural.Any(s => s.Kind == "prerequisites" && s.Location.Replace("/prerequisites/", "/inputs/", StringComparison.Ordinal) == d.Location));
        if (unsupported) state.RevisionScope.RemoveAll(p => findings.Any(d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING" && d.Location == p));
        var immutable = TaskPlanRevisions.UnrepairableRequirements(state).ToArray();
        state.Diagnostics.AddRange(immutable);
        if (state.RevisionScope.Count == 0 || unsupported || immutable.Length > 0 || findings.Any(d => d.Code == "TASK_COMPILER_VALIDATION")) Stop(state);
        else state.Status = PlanningStatus.Generating;
    }

    private static void ValidateRequirements(PlanningSession state, PlanningRequirements? requirements)
    {
        if (requirements is null && state.Requirements is not null) return;
        if (requirements is null)
        {
            Reject("REQUIREMENTS_INVALID", "/requirements", "The first TaskPlan must include requirements. None are accepted; null is valid only during discovery or clarification.");
            return;
        }
        if (string.IsNullOrWhiteSpace(requirements.Summary))
            Reject("REQUIREMENTS_INVALID", "/requirements/summary", "Describe the requested business outcome in a nonblank summary.");
        if (requirements.Outcomes.Count == 0)
            Reject("REQUIREMENTS_INVALID", "/requirements/outcomes", "Declare at least one concrete business outcome.");
        var outcomeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < requirements.Outcomes.Count; i++)
        {
            var outcome = requirements.Outcomes[i];
            if (string.IsNullOrWhiteSpace(outcome.Id))
                Reject("REQUIREMENTS_INVALID", $"/requirements/outcomes/{i}/id", "Give each business outcome a nonblank identifier.");
            if (string.IsNullOrWhiteSpace(outcome.Description))
                Reject("REQUIREMENTS_INVALID", $"/requirements/outcomes/{i}/description", "Describe this business outcome with nonblank text.");
            if (!outcomeIds.Add(outcome.Id))
                Reject("REQUIREMENTS_INVALID", $"/requirements/outcomes/{i}/id", "This business outcome identifier is duplicated; use distinct identifiers.");
        }
        if (state.Requirements is { } accepted && (accepted.Summary != requirements!.Summary || !(PlanningCommitments.SameOutcomes(state, requirements)) ||
                accepted.Inputs is not null && !PlanningClarifications.SameInputs(accepted.Inputs, requirements.Inputs) ||
                accepted.Outputs is not null && !PlanningClarifications.SameInputs(accepted.Outputs, requirements.Outputs)))
            Reject("REQUIREMENTS_CHANGED", "/requirements", "Preserve accepted requirements. Only an explicit user revision may change their scope.");
        if (PlanningCommitments.Validate(state, requirements, final: false) is { Count: > 0 } invalidCommitments)
            throw new PlanningResponseException(invalidCommitments);
        if (requirements?.Outputs is { } outputs)
        {
            if (outputs.Any(o => string.IsNullOrWhiteSpace(o.Name) || o.Default is not null) || outputs.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
                Reject("REQUIREMENTS_INVALID", "/requirements/outputs", "Declare distinct business outputs without defaults; a default is not evidence of a produced result.");
            try { foreach (var output in outputs) _ = TaskPlanCompiler.TypeSchema(output.Type); }
            catch (ArgumentException) { Reject("REQUIREMENTS_INVALID", "/requirements/outputs", "Declare valid business output types."); }
        }
    }

    private static void Reject(string code, string location, string message) => throw new PlanningResponseException([new(code, location, message)]);
    private static void Invalidate(PlanningSession state) { state.Yaml = null; state.ApprovedHash = null; state.ApprovalReview = null; state.ValidationResults.Clear(); }
    private static void Stop(PlanningSession state)
    {
        state.Status = PlanningStatus.Stopped;
        // Repair still consumes the original typed diagnostics. A terminal unsatisfied
        // business dependency has one public code, retaining its precise reason/location.
        // Issued repair authority includes the exact diagnosed codes. Do not change
        // them while a retained receipt still has to be applied against that request.
        if (state.PendingCall is null) state.Diagnostics = state.Diagnostics.Select(d => d.Code is "TASK_INPUT_REQUIRED" or "TASK_INPUT_TYPE" or
            "TASK_GROUP_INPUTS" or "TASK_GROUP_INPUT_TYPE" or "TASK_REFERENCE_UNKNOWN" or "TASK_FIELD_UNKNOWN" or "TASK_FIELD_TYPE" or "TASK_OUTPUT_CONTRACT"
            ? d with { Code = "CONTRACT_UNSATISFIED", ValidationStage = "contract:" + d.Code, Message = d.Code + ": " + d.Message } : d).ToList();
        Invalidate(state);
    }
}
