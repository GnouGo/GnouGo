using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Planning;

internal static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this WebApplication app)
    {
        app.MapGet("/api/chat/conversations/{conversationId}/planning", async (string conversationId, ChatPlanningService service, CancellationToken ct) =>
            Results.Json((await service.ListAsync(conversationId, ct)).ToList(), ChatJsonContext.Default.ListPlanningSessionDto));
        app.MapPost("/api/chat/conversations/{conversationId}/planning/{id}/commands", async (string conversationId, string id, PlanningCommandDto request, ChatPlanningService service, CancellationToken ct) =>
        {
            try { return Results.Json(await service.SubmitAsync(conversationId, id, new() { Kind = request.Kind, ExpectedRevision = request.ExpectedRevision, Mode = request.Mode,
                DecisionAnswer = request.DecisionAnswer is { } answer ? new(answer.DecisionId, answer.OptionId, answer.Text) : null }, ct), ChatJsonContext.Default.PlanningSessionDto); }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planner decision command."); }
        });
        app.MapGet("/api/planning", async (PlanningSessionService service, CancellationToken ct) =>
            Results.Json((await service.ListAsync(ct)).Select(ToDto).ToList(), ChatJsonContext.Default.ListPlanningSessionDto));
        app.MapGet("/api/planning/{id}", async (string id, PlanningSessionService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } state ? Results.Json(ToDto(state), ChatJsonContext.Default.PlanningSessionDto) : Results.NotFound());
        app.MapPost("/api/planning", async (PlanningStartDto request, PlanningSessionService service, CancellationToken ct) =>
        {
            try { return Results.Json(ToDto(await service.StartAsync(request.Name, request.Prompt, request.ReviseExisting, ct, mode: request.Mode)), ChatJsonContext.Default.PlanningSessionDto); }
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
                    Mode = request.Mode,
                    DecisionAnswer = request.DecisionAnswer is { } answer ? new(answer.DecisionId, answer.OptionId, answer.Text) : null,
                    ExpectedRevision = request.ExpectedRevision,
                    ArtifactHash = request.ArtifactHash,
                    Text = request.Text,
                    Answers = request.Answers,
                    Generation = request.Generation is { } options ? new() { Reasoning = options.Reasoning, MaxInputTokensPerRequest = options.MaxInputTokensPerRequest, MaxOutputTokens = options.MaxOutputTokens } : null
                }, ct);
                return Results.Json(ToDto(state), ChatJsonContext.Default.PlanningSessionDto);
            }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planning command."); }
        });
    }

    internal static PlanningSessionDto ToDto(PlanningSession state) => new(
        state.Request.SessionId, state.Request.Name, state.Revision, state.Status, state.SemanticPlan?.Summary ?? "", PlanningReviewFormatter.Diagram(state.Graph),
        state.SemanticPlan is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.SemanticPlan, PlanningJsonContext.Default.SemanticPlan)!.AsObject(),
        state.GroundedPlan is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.GroundedPlan, PlanningJsonContext.Default.GroundedPlan)!.AsObject(),
        state.Yaml, PlanningArtifactApproval.Hash(state), state.ApprovedHash,
        state.Diagnostics.Select(d => new PlanningValidationDto(d.Code, d.Location, d.Message, d.Required)
        {
            ValidationStage = d.ValidationStage, Rule = d.Rule,
            Prerequisite = d.Prerequisite is { } p ? new(p.Kind, p.Description, p.Output, p.ConsumerCapability, p.ContractPath, p.RootActionId) : null,
            Computation = d.Computation is { } c ? new(c.Expression, c.Limitation, c.ReceiverContract.DeepClone().AsObject(), c.ParameterContracts.DeepClone().AsObject(), c.OriginExpression, c.ProducerLocation) : null
        }).ToArray(),
        state.Scenarios.Select(s => new PlanningScenarioDto(s.Id, s.Outcome, s.Description)).ToArray(),
        state.Status == PlanningStatus.Clarification ? state.GetQuestions().Select(q => new PlanningQuestionDto(q.Id, q.Question, PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(q.AnswerType), state.Catalog!))).ToArray() : [],
        state.ModelCalls, state.ReplanAttempts, state.Usage?.InputTokens ?? 0, state.Usage?.OutputTokens ?? 0,
        state.Usage?.EstimatedCost ?? 0, state.Usage?.EstimatedCostCurrency ?? "", state.ActiveMilliseconds, state.HumanWaitMilliseconds, state.Phase, state.Request.Mode, state.PendingDecision is { } pending ? ToDto(pending) : null,
        state.Decisions.Select(d => new PlanningDecisionRecordDto(ToDto(d.Decision), new(d.Answer.DecisionId, d.Answer.OptionId, d.Answer.Text), d.Source, d.Reason, d.AnsweredAtUtc)).ToArray())
    {
        PendingRepair = state.PendingRepair is { } repair ? new(repair.ActionIds.ToArray(), repair.Candidate.Summary, repair.Questions.Count > 0 && repair.Answers is null) : null,
        Clarifications = state.Answers.Select(a => new PlanningClarificationHistoryDto(a.Question, a.Answers.DeepClone().AsObject())).ToArray()
    };

    internal static PlanningDecisionDto ToDto(PlanningDecision decision) => new(decision.Id, decision.Question, decision.Context, decision.Phase, decision.Scope, decision.ActionIds,
        decision.Options.Select(o => new PlanningDecisionOptionDto(o.Id, o.Label, o.Reason, o.Preferred)).ToArray(), decision.AllowCustomAnswer);
}
