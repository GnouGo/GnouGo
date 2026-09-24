using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Runs the injected planner to a human pause or exact artifact approval.</summary>
public sealed class WorkflowPlanExecutor : IStepExecutor
{
    public string StepType => "workflow.plan";
    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires an object input.");
        var planner = ctx.Engine.WorkflowPlanner ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Workflow planning requires an injected IWorkflowPlanner.");
        var factory = ctx.Engine.PlanningRuntimeFactory ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Workflow planning requires an injected IPlanningRuntimeFactory.");
        var generator = input["generator"] as JsonObject ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "A generator is required.");
        var target = ctx.Engine.ResolveLlmTarget(generator["provider"]?.GetValue<string>(), generator["model"]?.GetValue<string>());
        var options = input.DeepClone().AsObject();
        options["generator"]!["provider"] = target.Provider; options["generator"]!["model"] = target.Model;
        var initial = new PlanningSession
        {
            Request = new()
            {
                Mode = input["planning_mode"]?.GetValue<string>() ?? ctx.Engine.DefaultPlanningMode,
                TenantId = ctx.Limits.TenantId ?? "default", Name = input["name"]?.GetValue<string>() ?? "generated",
                Prompt = input["raw_prompt"]?.GetValue<string>() ?? "", Options = options,
                MaxReplanAttempts = input["max_replan_attempts"]?.GetValue<int>() ?? 2,
                MaxModelCalls = input["llm_budget"]?["max_calls"]?.GetValue<int>() ?? 8,
                Generation = new()
                {
                    Reasoning = generator["reasoning"]?.GetValue<string>() ?? "medium",
                    MaxInputTokensPerRequest = generator["max_input_tokens"]?.GetValue<int>() ?? 12_000,
                    MaxOutputTokens = generator["max_output_tokens"]?.GetValue<int>() ?? 8_192
                },
                Policy = ctx.Engine.PlanningPolicy ?? new()
                {
                    Instructions = input["policy"]?["instructions"]?.GetValue<string>() ?? "",
                    AllowedStepTypes = (input["policy"]?["allowed_step_types"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList(),
                    DeniedCapabilityIds = (input["policy"]?["denied_capability_ids"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList(),
                    RequireExternalConfirmation = input["policy"]?["require_external_confirmation"]?.GetValue<bool>() ?? true,
                    MaxStepsTotal = input["limits"]?["max_steps_total"]?.GetValue<int>() ?? 300
                }
            }
        };
        PlanningMode.Validate(initial.Request.Mode);
        await using var owned = await factory.OpenAsync(ctx, initial, ct);
        var state = owned.Session;
        while (!PlanningStatus.IsTerminal(state.Status))
        {
            var command = new PlanningCommand { ExpectedRevision = state.Revision };
            if (state.Status == PlanningStatus.WaitingForDecision || state.Status == PlanningStatus.Clarification && state.PendingRepair is not null)
            {
                if (ctx.Engine.PlanningDecisionProvider is not { } decisions) break;
                command = await decisions.RequestAsync(state, ct);
            }
            else if (PlanningStatus.IsWaiting(state.Status))
            {
                if (ctx.Engine.HumanInputProvider is not { } human) break;
                if (state.Status == PlanningStatus.Clarification)
                {
                    var answers = new JsonObject();
                    foreach (var question in state.GetQuestions())
                    {
                        var answer = await human.RequestInputAsync(new HumanInputRequest
                        {
                            RunId = state.Request.SessionId, StepId = question.Id,
                            Prompt = question.Question + "\nReturn a JSON value of this business type: " + System.Text.Json.JsonSerializer.Serialize(question.AnswerType, PlanningJsonContext.Default.BusinessType),
                            Mode = "text", AllowAbandon = true
                        }, ct);
                        if (HumanInputContract.IsAbandoned(answer)) { command.Kind = "cancel"; break; }
                        var text = (answer is JsonObject obj ? obj["response"] : answer)?.GetValue<string>();
                        try { answers[question.Id] = JsonNode.Parse(text ?? "null"); }
                        catch (System.Text.Json.JsonException) { answers[question.Id] = text; }
                    }
                    if (command.Kind != "cancel") { command.Kind = "answer"; command.Answers = answers; }
                }
                else
                {
                    var answer = await human.RequestInputAsync(new HumanInputRequest
                    {
                        RunId = state.Request.SessionId, StepId = "review-" + state.Revision,
                        Prompt = "Review the validated workflow. Scenario checks use simulated integrations.",
                        Context = JsonValue.Create(state.SemanticPlan?.Summary + "\n\n```yaml\n" + state.Yaml + "\n```"),
                        Mode = "choice", Choices = ["approve", "revise", "cancel"], AllowAbandon = true
                    }, ct);
                    command.Kind = HumanInputContract.IsAbandoned(answer) ? "cancel" : (answer is JsonObject obj ? obj["response"] : answer)?.GetValue<string>() ?? "cancel";
                    command.ArtifactHash = state.ComputeArtifactHash();
                    if (command.Kind == "revise")
                    {
                        var edit = await human.RequestInputAsync(new HumanInputRequest
                        { RunId = state.Request.SessionId, StepId = "revision", Prompt = "Describe the requested changes.", Mode = "text", AllowAbandon = true }, ct);
                        if (HumanInputContract.IsAbandoned(edit)) command.Kind = "cancel";
                        else command.Text = (edit is JsonObject obj2 ? obj2["response"] : edit)?.GetValue<string>();
                    }
                }
            }
            state = await planner.AdvanceAsync(state, command, owned.Runtime, ct);
            if (ctx.Engine.PlanningDecisionProvider is { } observer) await observer.CheckpointedAsync(state, ct);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.status", state.Status);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.calls", state.ModelCalls);
        }
        var result = new JsonObject { ["status"] = state.Status, ["session_id"] = state.Request.SessionId, ["revision"] = state.Revision };
        if (PlanningStatus.IsWaiting(state.Status)) return result;
        if (state.Status != PlanningStatus.Approved || state.ApprovedHash is null)
            throw new WorkflowRuntimeException(state.Status == PlanningStatus.Cancelled ? ErrorCodes.WorkflowPlanAborted : ErrorCodes.TemplatePlan,
                state.Diagnostics.FirstOrDefault()?.Message ?? "Planning stopped.", details: result);
        result["artifact_hash"] = state.ApprovedHash;
        result["yaml"] = await factory.ReadApprovedYamlAsync(ctx, state.Request.SessionId, state.ApprovedHash, ct);
        return result;
    }
}
