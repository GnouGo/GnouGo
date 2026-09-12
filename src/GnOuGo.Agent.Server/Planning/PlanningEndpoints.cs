using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Planning;

internal static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this WebApplication app)
    {
        app.MapGet("/api/planning", async (PlanningSessionService service, CancellationToken ct) =>
            Results.Json((await service.ListAsync(ct)).Select(ToDto).ToList(), ChatJsonContext.Default.ListPlanningSessionDto));
        app.MapGet("/api/planning/{id}", async (string id, PlanningSessionService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } state ? Results.Json(ToDto(state), ChatJsonContext.Default.PlanningSessionDto) : Results.NotFound());
        app.MapPost("/api/planning", async (PlanningStartDto request, PlanningSessionService service, CancellationToken ct) =>
        {
            try { return Results.Json(ToDto(await service.StartAsync(request.Name, request.Prompt, request.ReviseExisting, ct)), ChatJsonContext.Default.PlanningSessionDto); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planning request."); }
            catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
        });
        app.MapPost("/api/planning/{id}/commands", async (string id, PlanningCommandDto request, PlanningSessionService service, CancellationToken ct) =>
        {
            try
            {
                var state = await service.SubmitAsync(id, new PlanningCommand
                {
                    Kind = request.Kind,
                    ExpectedRevision = request.ExpectedRevision,
                    ArtifactHash = request.ArtifactHash,
                    Text = request.Text,
                    Answers = request.Answers,
                    Generation = request.Generation is { } options ? new() { ReasoningProfile = options.ReasoningProfile is { } profile ? new() { Routine = profile.Routine, Behavior = profile.Behavior, SemanticReview = profile.SemanticReview } : new(), MaxInputTokensPerRequest = options.MaxInputTokensPerRequest, MaxOutputTokens = options.MaxOutputTokens } : null
                }, ct);
                return Results.Json(ToDto(state), ChatJsonContext.Default.PlanningSessionDto);
            }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planning command."); }
        });
    }

    internal static PlanningSessionDto ToDto(PlanningSnapshot snapshot) => new(
        snapshot.Request.SessionId, snapshot.Request.Name, snapshot.Revision, snapshot.Status, snapshot.BehaviorPlan?.Summary ?? snapshot.Graph?.Summary ?? "",
        PlanningReviewFormatter.Diagram(DisplayGraph(snapshot), snapshot.Preparation), PlanningReviewFormatter.BehaviorDetails(DisplayGraph(snapshot)), snapshot.Yaml, snapshot.ArtifactHash, snapshot.ApprovedHash,
        snapshot.ActiveMilliseconds, snapshot.HumanWaitMilliseconds + (snapshot.WaitingSinceUtc is { } waiting ? Math.Max(0, (DateTimeOffset.UtcNow - waiting).TotalMilliseconds) : 0),
        snapshot.Diagnostics.Select(d => new PlanningValidationDto(d.Code, d.Location, d.Message, d.Required)).ToArray(),
        snapshot.Validation.Scenarios.Select(s => new PlanningScenarioDto(s.Id, s.Outcome, s.Description)).ToArray(),
        snapshot.History.Select(r => new PlanningRevisionDto(r.Revision, r.ArtifactHash, r.Status, r.ChangedWorkflows)).ToArray(),
        snapshot.Intent.Question is null ? null : HumanInputContract.BuildRequestPayload(snapshot.Intent.Question),
        snapshot.Usage?.Calls ?? 0, snapshot.Usage?.InputTokens ?? 0, snapshot.Usage?.OutputTokens ?? 0, snapshot.Usage?.EstimatedCost ?? 0, snapshot.Usage?.EstimatedCostCurrency ?? "", Outcome(snapshot.Outcome),
        PlanningPhase.Resolve(snapshot),
        snapshot.BehaviorPlan is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(snapshot.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject(),
        snapshot.ApprovedBehaviorHash, snapshot.Status == PlanningStatus.Stopped ? "Planning stopped in " + PlanningPhase.Resolve(snapshot) + ". " + snapshot.Diagnostics.Count(d => d.Required) + " required findings remain; session history is retained." : null,
        snapshot.Intent.Answers.Count, snapshot.Request.Options["generator"]?["model"]?.GetValue<string>(),
        new(snapshot.Request.Generation.ReasoningProfile.Routine, snapshot.Request.Generation.ReasoningProfile.Behavior, snapshot.Request.Generation.ReasoningProfile.SemanticReview),
        snapshot.Construction.Workflows.Select(w => new PlanningWorkflowDto(w.WorkflowKey, w.Status, w.Dependencies, w.Calls, w.RepairCalls,
            w.EstimatedInputTokens, w.InputTokenLimit, w.UnresolvedHoles, w.ResolvedHoles, w.Gate,
            snapshot.RepairAllowances.Where(a => a.WorkflowKey == w.WorkflowKey && a.Gate == w.Gate).Sum(a => a.Attempts), snapshot.Request.MaxRepairsPerWorkflowGate,
            w.TotalHoles, w.DeterministicallyResolvedHoles, w.ModelHoles, w.ModelHoleExposures,
            w.HoleChoices.Select(h => new PlanningHoleChoiceDto(h.Id, h.DirectBindings, h.ComputationParameters)).ToArray(),
            w.Gates.Select(g => new PlanningGateProgressDto(g.Gate, g.Repairs, g.Failures)).ToArray(),
            w.DeterministicSchemaHoles, w.ModelSchemaHoles, w.ModelRequired, w.ModelUsed)).ToArray(), snapshot.Construction.Dataflow?.Fingerprint,
        snapshot.Construction.Dataflow?.Bindings.Count ?? 0, snapshot.PreparationCheckpoint?.Stage,
        snapshot.Preparation?.DecisionContractVersion ?? 0, snapshot.Preparation?.Decisions.Count ?? 0,
        snapshot.GateProgress.Select(g => new PlanningGateProgressDto(g.Gate, snapshot.RepairAllowances.Where(a => a.WorkflowKey == g.WorkflowKey && a.Gate == g.Gate).Sum(a => a.Attempts), g.Failures, g.WorkflowKey)).ToArray(),
        snapshot.RequestCounts.Select(a => new PlanningRequestCountsDto(a.WorkflowKey, a.Phase, a.Gate, a.Reservations, a.ModelUsed, a.Unverifiable,
            a.EstimatedInputTokens, a.InputTokens, a.OutputTokens, a.AvoidableDispatches, a.AvoidableExtraRequests, a.Repairs, a.Failures)).ToArray(),
        snapshot.TechnicalStop is { } stop ? new(stop.Code, stop.Phase, stop.Location, stop.Unverifiable) : null,
        snapshot.DecisionPages.Select(p => new PlanningDecisionPageDto(p.Id, p.Phase, p.WorkflowKey, p.Status, p.Decisions.Count, p.EstimatedInputTokens, p.EstimatedAnswerTokens, p.InputTargetTokens, p.Correction)).ToArray());

    private static PlanningOutcomeDto? Outcome(PlanningOutcome? outcome) => outcome switch
    {
        PlanningValidWorkflow valid => new(valid.Name, valid.ArtifactHash),
        PlanningNeedUserClarification needed => new(needed.Name, Decision: new(needed.Decision.DecisionId, needed.Decision.EvidenceReferences, needed.Decision.AnswerSchema, needed.Decision.Obligations)),
        PlanningUnsupported unsupported => new(unsupported.Name, Obligations: unsupported.Obligations.Select(o => new PlanningUnsupportedObligationDto(o.ObligationId, o.Code, o.EvidenceReferences)).ToArray()),
        _ => null
    };

    private static PlanningGraph? DisplayGraph(PlanningSnapshot snapshot) => snapshot.BehaviorPlan is { } behavior ? PlanningBehaviorPlans.Display(behavior, snapshot.Preparation) : snapshot.Graph;
}
