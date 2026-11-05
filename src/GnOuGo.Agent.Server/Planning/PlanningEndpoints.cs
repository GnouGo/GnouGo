using System.Text.Json.Nodes;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Agent.Server.Configuration;

namespace GnOuGo.Agent.Server.Planning;

internal static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this WebApplication app)
    {
        app.MapGet("/api/application-llm-budget", (ApplicationLlmBudgetPolicy policy) => Results.Json(policy.Describe()))
            .WithMetadata(new LocalOperatorRequired());
        app.MapPost("/api/application-llm-budget", async (JsonObject request, ApplicationLlmBudgetPolicy policy, LocalOperatorAccess access, CancellationToken ct) =>
        {
            try
            {
                if (request.Count != 1 || request["mode"] is not System.Text.Json.Nodes.JsonValue mode || !mode.TryGetValue<string>(out var selected))
                    return Results.BadRequest("A mode selection is required.");
                await policy.SetAsync(selected, access, ct); return Results.Json(policy.Describe());
            }
            catch (ArgumentException) { return Results.BadRequest("Mode must be unlimited or limited."); }
        }).WithMetadata(new LocalOperatorRequired());
        app.MapPost("/api/planning/{id}/start-with-current-settings", async (string id, PlanningSessionService service, LocalOperatorAccess access, CancellationToken ct) =>
        {
            try { return Results.Json(ToDto(await service.StartWithCurrentSettingsAsync(id, access, ct)), ChatJsonContext.Default.PlanningSessionDto); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
        }).WithMetadata(new LocalOperatorRequired());
        app.MapGet("/api/planning/antiforgery", (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new JsonObject { ["headerName"] = tokens.HeaderName, ["requestToken"] = tokens.RequestToken });
        });
        app.MapGet("/api/chat/conversations/{conversationId}/planning", async (string conversationId, ChatPlanningService service, CancellationToken ct) =>
            Results.Json((await service.ListAsync(conversationId, ct)).ToList(), ChatJsonContext.Default.ListPlanningSessionDto));
        app.MapPost("/api/chat/conversations/{conversationId}/planning/{id}/commands", async (string conversationId, string id, PlanningCommandDto request, ChatPlanningService service, CancellationToken ct) =>
        {
            try { return Results.Json(await service.SubmitAsync(conversationId, id, new() { Kind = request.Kind, ExpectedRevision = request.ExpectedRevision, Mode = request.Mode, Selections = request.Selections, EditablePaths = request.EditablePaths?.ToList(),
                Answers = request.Answers?.Select(a => a is null ? throw new ArgumentException("Invalid planner answer.") : new PlanningAnswer(a.QuestionId, a.AlternativeId, a.Text)).ToList() }, ct), ChatJsonContext.Default.PlanningSessionDto); }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planner decision command."); }
        });
        app.MapGet("/api/planning/{id}/inspection", async (string id, bool? workflow, long? after, PlanningInspectionService inspection, CancellationToken ct) =>
        {
            try { return Results.Json(await inspection.ReadAsync(id, workflow == true, after ?? 0, ct)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).WithMetadata(new LocalOperatorRequired());
        app.MapPost("/api/planning/{id}/stop", async (string id, PlanningSessionService service, CancellationToken ct) =>
        {
            try { return Results.Json(await service.ToDtoAsync(await service.StopAsync(id, ct), ct), ChatJsonContext.Default.PlanningSessionDto); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
        });
        app.MapGet("/api/planning/{id}/calls/export", async (string id, string key, bool? workflow, PlanningInspectionService inspection, CancellationToken ct) =>
        {
            try { return Results.File(System.Text.Encoding.UTF8.GetBytes((await inspection.CallAsync(id, workflow == true, key, ct)).ToJsonString()), "application/json", "planning-call-redacted.json"); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).WithMetadata(new LocalOperatorRequired());
        app.MapGet("/api/planning/{id}/calls/content", async (string id, string key, bool? workflow, int? offset, int? count, string? search, PlanningInspectionService inspection, CancellationToken ct) =>
        {
            try
            {
                var full = (await inspection.CallAsync(id, workflow == true, key, ct)).ToJsonString();
                var start = Math.Clamp(offset ?? 0, 0, full.Length); var size = Math.Clamp(count ?? 32768, 1, 65536);
                var length = Math.Min(size, full.Length - start);
                var match = string.IsNullOrEmpty(search) ? -1 : full.IndexOf(search, start, StringComparison.OrdinalIgnoreCase);
                return Results.Json(new JsonObject { ["text"] = full.Substring(start, length), ["offset"] = start, ["totalCharacters"] = full.Length,
                    ["nextOffset"] = start + length, ["complete"] = start + length == full.Length, ["nextMatch"] = match });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).WithMetadata(new LocalOperatorRequired());
        app.MapGet("/api/planning", async (PlanningSessionService service, CancellationToken ct) =>
            Results.Json((await Task.WhenAll((await service.ListAsync(ct)).Select(state => service.ToDtoAsync(state, ct)))).ToList(), ChatJsonContext.Default.ListPlanningSessionDto));
        app.MapGet("/api/planning/{id}", async (string id, PlanningSessionService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } state ? Results.Json(await service.ToDtoAsync(state, ct), ChatJsonContext.Default.PlanningSessionDto) : Results.NotFound());
        app.MapPost("/api/planning", async (PlanningStartDto request, PlanningSessionService service, LocalOperatorAccess access, CancellationToken ct) =>
        {
            try
            {
                var selection = await access.ResolveAsync(request.ThresholdMode);
                return Results.Json(ToDto(await service.StartAsync(request.Name, request.Prompt, request.ReviseExisting, ct, mode: request.Mode, constructionPreset: request.ConstructionPreset, thresholdMode: selection.Mode,
                    diagnosticGrant: selection.Grant)), ChatJsonContext.Default.PlanningSessionDto);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
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
                    ExpectedRevision = request.ExpectedRevision,
                    ArtifactHash = request.ArtifactHash,
                    RequestId = request.RequestId,
                    ReviewedRequirementIds = request.ReviewedRequirementIds?.ToList(),
                    Text = request.Text,
                    PreserveRequirements = request.PreserveRequirements,
                    EditablePaths = request.EditablePaths?.ToList(),
                    Selections = request.Selections,
                    Answers = request.Answers?.Select(a => a is null ? throw new ArgumentException("Invalid planner answer.") : new PlanningAnswer(a.QuestionId, a.AlternativeId, a.Text)).ToList(),
                    Generation = request.Generation is { } options ? new() { Reasoning = options.Reasoning, MaxInputTokensPerRequest = options.MaxInputTokensPerRequest, MaxOutputTokens = options.MaxOutputTokens } : null
                }, ct);
                return Results.Json(await service.ToDtoAsync(state, ct), ChatJsonContext.Default.PlanningSessionDto);
            }
            catch (PlanningConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest("Invalid planning command."); }
        });
    }

    internal static PlanningSessionDto ToDto(PlanningSession state) => new(
        state.Request.SessionId, state.Request.Name, state.Revision, state.Status, state.Requirements?.Summary ?? "", PlanningReviewFormatter.TaskDiagram(state.Plan),
        state.Requirements is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements)!.AsObject(),
        state.Graph is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.Graph, PlanningJsonContext.Default.PlanningGraph)!.AsObject(),
        state.Yaml, PlanningArtifactApproval.Hash(state), state.ApprovedHash,
        state.Diagnostics.Select(d => new PlanningValidationDto(d.Code, d.Location, d.Message, d.Required)
        {
            ValidationStage = d.ValidationStage, Rule = d.Rule, BudgetFailure = d.BudgetFailure is { } failure ? new JsonObject
            { ["scope"] = failure.Scope, ["origin"] = failure.Origin, ["limitKind"] = failure.LimitKind, ["allowance"] = failure.Allowance,
                ["requiredReservation"] = failure.RequiredReservation, ["currency"] = failure.Currency, ["dispatchStatus"] = failure.DispatchStatus } : null,
            Prerequisite = d.Prerequisite is { } p ? new(p.Kind, p.Description, p.Output, p.ConsumerCapability, p.ContractPath, p.RootActionId) : null,
        }).ToArray(),
        state.ValidationResults.Select(s => new PlanningValidationResultDto(s.Id, s.Outcome, s.Description)).ToArray(),
        state.GetChoices().Select(c => new PlanningChoiceDto(c.Id, c.Question, c.Alternatives.Select(a => new PlanningAlternativeDto(a.Id, a.Description, System.Text.Json.JsonSerializer.SerializeToNode(a.Value, PlanningJsonContext.Default.TaskValue))).ToArray(), c.Recommended, c.Selected)).ToArray(),
        state.ModelCalls, state.ReplanAttempts, state.Usage?.InputTokens ?? 0, state.Usage?.OutputTokens ?? 0,
        state.Usage?.EstimatedCost ?? 0, state.Usage?.EstimatedCostCurrency ?? "", state.ActiveMilliseconds, state.HumanWaitMilliseconds, state.Phase, state.Request.Mode)
    {
        SchemaVersion = state.SchemaVersion,
        CostEstimateAvailable = state.Usage?.CostEstimateAvailable == true,
        CostUnavailableReason = state.Usage?.CostUnavailableReason ?? (state.Usage is null ? "not_measured" : null),
        ThresholdMode = state.Request.Options[PlanningHostPolicy.CaptureOption]?["thresholdMode"]?.ToString() ?? "strict",
        EffectivePolicy = new JsonObject { ["phases"] = state.Request.Options["construction_limits"]?["phasePolicy"]?.DeepClone(),
            ["allocations"] = state.Request.Options["allocation_origin"]?.DeepClone(), ["model"] = state.Request.Options[GnOuGo.Flow.Integrations.Planning.CapturedPlanningModel.OptionName]?.DeepClone(),
            ["maxModelCalls"] = state.Request.MaxModelCalls, ["maxCorrections"] = state.Request.MaxReplanAttempts, ["capacity"] = state.Request.Options["model_capacity"]?.DeepClone(),
            ["budget"] = state.Request.Options["llm_budget"]?.DeepClone(), ["applicationBudget"] = state.Request.Options[PlanningApplicationBudget.OptionName]?.DeepClone(), ["construction"] = state.Request.Options["construction_limits"]?.DeepClone(),
            ["policy"] = System.Text.Json.JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy) },
        OverallProgress = PlanningCompactConstruction.InspectOverallProgress(state),
        Events = state.InspectionEvents?.Select(e => new PlanningInspectionEventDto(e.Sequence, e.TimestampUtc, e.Phase, e.State, e.Reason, e.RequestId, e.BlockId, e.StepId, e.Checkpoint, e.Objective) { PhaseOccurrence = e.PhaseOccurrence }).ToArray() ?? [],
        ThresholdWarnings = (state.ThresholdWarnings ?? []).Concat(state.Usage?.Warnings ?? [])
            .Concat(state.RequestMeasurements?.SelectMany(measurement => (measurement.Warnings ?? []).Select(warning => warning with
            { SessionId = state.Request.SessionId, RequestId = measurement.RequestId, BlockId = measurement.BlockId, StepId = measurement.StepId })) ?? [])
            .Distinct().Select(w =>
            new PlanningThresholdWarningDto(w.Kind, w.Current, w.Threshold, w.Basis, w.Stage, w.RequestId, w.BlockId)).ToArray(),
        RequestText = state.Request.Prompt,
        RevisionScope = state.RevisionScope.ToArray(),
        DiscoveryLimitations = state.Discovery.Limitations.ToArray(),
        Questions = state.PendingQuestions?.Select(q => new PlanningQuestionDto(q.Id, q.Question,
            q.Alternatives.Select(a => new PlanningAlternativeDto(a.Id, a.Description, null)).ToArray(), q.Recommended)).ToArray() ?? [],
        TaskPlan = state.Plan is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!.AsObject()
    };

}
