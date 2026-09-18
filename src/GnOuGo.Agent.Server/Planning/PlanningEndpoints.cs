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
        state.Request.SessionId, state.Request.Name, state.Revision, state.Status, state.IntentPlan?.Summary ?? "", PlanningReviewFormatter.Diagram(state.Graph),
        state.IntentPlan is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.IntentPlan, PlanningJsonContext.Default.WorkflowIntentPlan)!.AsObject(),
        state.Yaml, PlanningArtifactApproval.Hash(state), state.ApprovedHash,
        state.Graph is null || state.Catalog is null ? [] : PlanningHoleEligibility.Find(state.Graph, state.Catalog).Select(h => new PlanningHoleDto(h.Id, h.Kind, h.Path)).ToArray(),
        state.Diagnostics.Select(d => new PlanningValidationDto(d.Code, d.Location, d.Message, d.Required)).ToArray(),
        state.Scenarios.Select(s => new PlanningScenarioDto(s.Id, s.Outcome, s.Description)).ToArray(),
        state.Status == PlanningStatus.Clarification ? state.IntentPlan!.Questions.Select(q => new PlanningQuestionDto(q.Id, q.Question, PlanningGraphCompiler.ToJsonSchema(q.AnswerSchema, state.Catalog!))).ToArray() : [],
        state.ModelCalls, state.RepairAttempts, state.Usage?.InputTokens ?? 0, state.Usage?.OutputTokens ?? 0,
        state.Usage?.EstimatedCost ?? 0, state.Usage?.EstimatedCostCurrency ?? "", state.ActiveMilliseconds, state.HumanWaitMilliseconds);
}
