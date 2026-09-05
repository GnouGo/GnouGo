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
                var state = await service.SubmitAsync(id, new PlanningCommand { Kind = request.Kind, ExpectedRevision = request.ExpectedRevision, ArtifactHash = request.ArtifactHash, Text = request.Text, Answers = request.Answers }, ct);
                return Results.Json(ToDto(state), ChatJsonContext.Default.PlanningSessionDto);
            }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planning command."); }
        });
    }

    internal static PlanningSessionDto ToDto(PlanningSnapshot snapshot) => new(
        snapshot.Request.SessionId, snapshot.Request.Name, snapshot.Revision, snapshot.Status, snapshot.Graph?.Summary ?? "",
        PlanningReviewFormatter.Diagram(snapshot.Graph, snapshot.Preparation, snapshot.PreviousGraph ?? snapshot.ReviewedGraph), PlanningReviewFormatter.BehaviorDetails(snapshot.Graph), snapshot.Yaml, snapshot.ArtifactHash, snapshot.ApprovedHash,
        snapshot.ActiveMilliseconds, snapshot.HumanWaitMilliseconds + (snapshot.WaitingSinceUtc is { } waiting ? Math.Max(0, (DateTimeOffset.UtcNow - waiting).TotalMilliseconds) : 0),
        snapshot.Diagnostics.Select(d => new PlanningValidationDto(d.Code, d.Location, d.Message, d.Required)).ToArray(),
        snapshot.Scenarios.Select(s => new PlanningScenarioDto(s.Id, s.Outcome, s.Description)).ToArray(),
        snapshot.History.Select(r => new PlanningRevisionDto(r.Revision, r.ArtifactHash, r.Status, r.ChangedFragments)).ToArray(),
        snapshot.Question is null ? null : HumanInputContract.BuildRequestPayload(snapshot.Question),
        snapshot.Usage?.Calls ?? 0, snapshot.Usage?.InputTokens ?? 0, snapshot.Usage?.OutputTokens ?? 0, snapshot.Usage?.EstimatedCost ?? 0, snapshot.Usage?.EstimatedCostCurrency ?? "", snapshot.Outcome);
}
