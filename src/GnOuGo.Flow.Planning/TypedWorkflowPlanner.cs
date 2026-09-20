using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Discover → interpret → build/resolve → validate → approve. Only this coordinator changes session state.</summary>
public sealed class TypedWorkflowPlanner(TimeProvider? timeProvider = null) : IWorkflowPlanner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (session.SchemaVersion != 7) throw new PlanningConflictException("Unsupported planning session; start a new session.");
        if (session.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed; reload its current revision.");
        if (string.IsNullOrWhiteSpace(session.Request.TenantId) || string.IsNullOrWhiteSpace(session.Request.SessionId) || string.IsNullOrWhiteSpace(session.Request.Prompt))
            throw new ArgumentException("Tenant, session, and prompt are required.");
        if (session.Request.MaxRepairAttempts is < 0 or > 10 || session.Request.MaxModelCalls is < 1 or > 1000) throw new ArgumentException("Invalid planning limits.");
        PlanningGenerationPolicy.Validate(session.Request.Generation);
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
                    if ((state.RepairAttempts > session.RepairAttempts || session.PendingCall?.Purpose == "repair") &&
                        session.IntentPlan is not null && state.IntentPlan is not null && session.Diagnostics.Any(d => d.Required) && state.Diagnostics.Any(d => d.Required) &&
                        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(session.IntentPlan, PlanningJsonContext.Default.WorkflowIntentPlan), JsonSerializer.SerializeToNode(state.IntentPlan, PlanningJsonContext.Default.WorkflowIntentPlan)) &&
                        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(session.Fixtures, PlanningJsonContext.Default.PlanningFixtures), JsonSerializer.SerializeToNode(state.Fixtures, PlanningJsonContext.Default.PlanningFixtures)) &&
                        session.Diagnostics.SequenceEqual(state.Diagnostics))
                    { state.Diagnostics.Add(new("REPAIR_NO_PROGRESS", "$", "The unchanged intent produced the same failures.")); Stop(state); }
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
                case "edit_intent":
                    if (state.PendingCall is not null) throw new PlanningConflictException("Reconcile the pending model request before editing intent.");
                    ArgumentException.ThrowIfNullOrWhiteSpace(command.Text);
                    state.Request.Baseline = state.IntentPlan;
                    state.Request.Prompt = command.Kind == "edit_intent" ? command.Text.Trim() : state.Request.Prompt + "\nRequested revision: " + command.Text.Trim();
                    state.IntentPlan = null; state.Graph = null; state.Catalog = null; state.Fixtures = null; state.RepairAttempts = 0;
                    state.Diagnostics.Clear(); state.Scenarios.Clear(); state.Yaml = null; state.ApprovedHash = null; state.Status = PlanningStatus.Generating;
                    break;
                case "answer":
                    if (state.Status != PlanningStatus.Clarification || state.IntentPlan is null || command.Answers is null) throw new PlanningConflictException("No clarification is awaiting an answer.");
                    if (command.Answers.Any(a => !state.IntentPlan.Questions.Any(q => q.Id == a.Key))) throw new ArgumentException("Unknown clarification answer.");
                    foreach (var question in state.IntentPlan.Questions)
                    {
                        if (!command.Answers.ContainsKey(question.Id) || PlanningContractValidation.ValidateInstance(command.Answers[question.Id], PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(question.AnswerType), state.Catalog!)).Count > 0)
                            throw new ArgumentException("An answer violates the question's schema: " + question.Id);
                        state.Answers.Add(new(question.Question, new() { [question.Id] = command.Answers[question.Id]?.DeepClone() }));
                    }
                    state.IntentPlan = null; state.Graph = null; state.Diagnostics.Clear(); state.Status = PlanningStatus.Generating;
                    break;
                case "configure_generation":
                    if (state.PendingCall is not null || command.Generation is null) throw new PlanningConflictException("Generation settings cannot replace a pending request.");
                    PlanningGenerationPolicy.Validate(command.Generation); state.Request.Generation = command.Generation;
                    state.Diagnostics.RemoveAll(d => d.Code == "MODEL_INPUT_LIMIT");
                    if (state.Status == PlanningStatus.Stopped) state.Status = PlanningStatus.Generating;
                    break;
                default: throw new ArgumentException("Unsupported planning command.");
            }
        }
        catch (PlanningConflictException) { throw; }
        catch (ArgumentException) when (command.Kind != "advance") { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { state.Diagnostics.Add(new(ErrorCodes.LlmBudgetExceeded, "$", "Active planning time was exhausted.")); Stop(state); }
        catch (PlanningResponseException ex)
        {
            // A rejected correction never erases the errors in the unchanged executable proposal.
            state.Diagnostics = (state.IntentPlan is null ? [] : state.Diagnostics).Concat(ex.Diagnostics).Distinct().ToList();
            state.Status = PlanningStatus.Generating; Invalidate(state);
        }
        catch (WorkflowRuntimeException ex)
        {
            if (ex.Code == "MODEL_INPUT_LIMIT")
            { state.Diagnostics.RemoveAll(d => d.Code == ex.Code); state.Diagnostics.Add(new(ex.Code, "$", ex.Message)); }
            else state.Diagnostics.Add(new(ex.Code, "$", ex.Message));
            Stop(state);
        }
        catch (JsonException ex)
        {
            if (state.IntentPlan is null) state.Diagnostics.Clear();
            state.Diagnostics.Add(new("INTENT_SCHEMA_INVALID", state.IntentPlan is null ? "$" : "/changes", ex.Message));
            state.Status = PlanningStatus.Generating; Invalidate(state);
        }
        catch (Exception ex)
        {
            state.Diagnostics.Add(new(state.PendingCall is null ? "PLANNING_INVALID" : "MODEL_DISPATCH_UNVERIFIABLE", "$", ex.Message));
            if (state.PendingCall is not null || state.Catalog is null) Stop(state); else { state.Status = PlanningStatus.Generating; Invalidate(state); }
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
        if (state.Catalog is null) { state.Catalog = await runtime.DiscoverAsync(state.Request, ct); return; }
        if (state.PendingCall?.Purpose is "intent" or "repair" || state.IntentPlan is null || state.Diagnostics.Any(d => d.Required))
        {
            var repair = state.PendingCall is { } pending ? pending.Purpose == "repair" : state.ModelCalls > 0 && state.Diagnostics.Any(d => d.Required);
            if (repair && state.PendingCall is null)
            {
                var hostFailures = PlanningDiagnosticLocations.ForIntent(state).Where(d => d.Required && d.Code == "PLANNING_HOST_CONTRACT").ToList();
                if (hostFailures.Count > 0) { state.Diagnostics.AddRange(hostFailures); Stop(state); return; }
                if (state.RepairAttempts >= state.Request.MaxRepairAttempts) { Stop(state); return; }
            }
            WorkflowIntentPlan intent;
            if (repair && state.IntentPlan is not null)
            {
                var targets = PlanningCorrections.Batch(state);
                if (targets.Count == 0) { state.Diagnostics.Add(new("PLANNING_HOST_CONTRACT", "$", "No editable business target explains the blocking diagnostics.")); Stop(state); return; }
                var correction = await PlanningModelCalls.CallAsync(state, runtime, "repair", state.PendingCall?.Request.Prompt ?? PlanningCorrections.Prompt(state, targets),
                    state.PendingCall?.Request.StructuredOutputSchema?.AsObject() ?? PlanningCorrections.Schema(targets), ct,
                    PlanningCorrections.MaxInputTokens, PlanningCorrections.MaxOutputTokens);
                intent = PlanningCorrections.Apply(state, targets, correction);
            }
            else
            {
                var candidate = await PlanningModelCalls.CallAsync(state, runtime, repair ? "repair" : "intent", PlanningModelCalls.IntentPrompt(state), PlanningSchemas.Intent(), ct);
                intent = JsonSerializer.Deserialize(candidate, PlanningJsonContext.Default.WorkflowIntentPlan)!;
            }
            state.IntentPlan = intent; state.Graph = null; state.Diagnostics.Clear(); state.Scenarios.Clear(); Invalidate(state);
            if (intent.Questions.Count > 0)
            {
                if (++state.ClarificationRounds > 3 || intent.Questions.Count > 5 || intent.Questions.Select(q => q.Id).Distinct().Count() != intent.Questions.Count)
                { state.Diagnostics = [new("CLARIFICATION_LIMIT", "/questions", "Clarification is limited to three rounds of five distinct questions.")]; Stop(state); return; }
                foreach (var question in intent.Questions) _ = PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(question.AnswerType), state.Catalog);
                state.Status = PlanningStatus.Clarification; return;
            }
            state.Diagnostics = PlanningGraphBuilder.ValidateIntent(intent);
            if (state.Diagnostics.Count > 0) return;
            state.Graph = PlanningGraphBuilder.Build(intent, state.Catalog);
        }
        if (state.Graph is null) state.Graph = PlanningGraphBuilder.Build(state.IntentPlan!, state.Catalog);
        while (true)
        {
            var holes = PlanningHoleEligibility.Find(state.Graph, state.Catalog);
            if (holes.Count == 0) break;
            var domains = holes.Select(h => (Hole: h, Choices: PlanningHoleEligibility.Choices(state.Graph, state.Catalog, h))).ToArray();
            var singleton = domains.FirstOrDefault(d => d.Choices.Count == 1);
            if (singleton.Hole is not null)
            {
                PlanningBusinessChoices.Apply(state, [(singleton.Hole, singleton.Choices[0].Value)]);
                continue;
            }
            var ambiguous = domains.Where(d => d.Choices.Count > 1).ToList();
            if (ambiguous.Count == 0)
            {
                state.Diagnostics = domains.Select(d => new PlanningDiagnostic("HOLE_UNRESOLVED", d.Hole.Path, PlanningHoleEligibility.IsInputDefault(d.Hole)
                    ? "No valid literal default is established. Repair the input declaration: use default: null when no default is intended, or supply a contract-valid literal. Declared runtime inputs need no planning-time answer."
                    : "No valid deterministic choice exists for this " + d.Hole.Kind + " field. Supply a typed value or revise its dependencies.")).ToList();
                state.Diagnostics.AddRange(PlanningExecutableValidation.Validate(state.Graph, state.Catalog).Where(d => d.Code != "CONFIRMATION_REQUIRED"));
                state.Diagnostics.AddRange(PlanningValidationPipeline.FixtureShape(state));
                state.Diagnostics.AddRange(PlanningFixtureSamples.Validate(state));
                return;
            }
            var answers = (await PlanningModelCalls.CallAsync(state, runtime, "choices", PlanningModelCalls.ChoicePrompt(state, ambiguous), PlanningSchemas.Choices(ambiguous), ct)).AsObject();
            PlanningBusinessChoices.Apply(state, ambiguous.Select(domain =>
                (domain.Hole, domain.Choices.Single(c => c.Id == answers[domain.Hole.Id]!.GetValue<string>()).Value)).ToArray());
            return; // Persist resolved business assignments before recomputing dependent domains.
        }
        await PlanningValidationPipeline.ValidateAsync(state, runtime, ct);
    }
    private static void Invalidate(PlanningSession state) { state.Yaml = null; state.ApprovedHash = null; }
    private static void Stop(PlanningSession state) { state.Status = PlanningStatus.Stopped; Invalidate(state); }
}
