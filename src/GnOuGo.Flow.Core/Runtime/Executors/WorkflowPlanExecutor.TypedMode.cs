using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private async Task<JsonNode?> ExecuteTypedPlanAsync(StepExecutionContext ctx, JsonObject input, CancellationToken ct)
    {
        var planner = ctx.Engine.WorkflowPlanner ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Version 2 requires an injected IWorkflowPlanner. No legacy fallback was attempted.");
        var generator = input["generator"] as JsonObject ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires generator settings.");
        var options = (JsonObject)input.DeepClone();
        var target = ctx.Engine.ResolveLlmTarget(generator["provider"]?.GetValue<string>(), generator["model"]?.GetValue<string>());
        options["generator"]!["provider"] = target.Provider;
        options["generator"]!["model"] = target.Model;
        var budget = ParseLLMUsageBudget(input);
        if (budget is not null)
        {
            var metered = budget with { MaxElapsed = null };
            if (metered.MaxCalls is null && metered.MaxTotalTokens is null && metered.MaxEstimatedCost is null && metered.MaxEstimatedCostUsd is null)
                metered = metered with { MaxCalls = 100 };
            ctx.LLMUsageBudget = ctx.LLMUsageBudget?.CreateChild(metered) ?? new LLMUsageBudgetScope(metered, exchangeRateProvider: ctx.Engine.ExchangeRateProvider);
        }
        var state = new PlanningSnapshot
        {
            Request = new PlanningRequest
            {
                TenantId = ctx.Limits.TenantId ?? "default",
                Name = input["name"]?.GetValue<string>() ?? input["document_name"]?.GetValue<string>() ?? "generated",
                Prompt = input["raw_prompt"]?.GetValue<string>() ?? generator["instruction"]?.GetValue<string>() ?? "",
                ExistingYaml = input["surgical_repair"]?["original_yaml"]?.GetValue<string>(),
                Options = options
            }
        };
        var runtime = new WorkflowPlanningRuntime(ctx);
        while (!PlanningStatus.IsTerminal(state.Status))
        {
            if (budget?.MaxElapsed is { } maxElapsed && state.ActiveMilliseconds >= maxElapsed.TotalMilliseconds)
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded, "The active planning time budget was exhausted.");
            var command = new PlanningCommand { ExpectedRevision = state.Revision };
            if (PlanningStatus.IsWaiting(state.Status))
            {
                var provider = ctx.Engine.HumanInputProvider ?? throw new WorkflowRuntimeException(ErrorCodes.WorkflowPlanClarificationFailed, "The typed planner requires a human-input provider for review.");
                HumanInputRequest question;
                if (state.Status == PlanningStatus.Clarification) question = state.Question!;
                else question = new HumanInputRequest
                {
                    RunId = state.Request.SessionId, StepId = "review-" + state.Revision,
                    Prompt = state.Status == PlanningStatus.BehaviorReview ? "Review the proposed workflow behavior." : "Review the validated workflow. Synthetic checks do not prove live external behavior.",
                    Context = JsonValue.Create(state.Status == PlanningStatus.BehaviorReview ? state.ReviewMarkdown : state.ReviewMarkdown + "\n\nValidated YAML:\n```yaml\n" + state.Yaml + "\n```"),
                    Mode = "choice", Choices = state.Status == PlanningStatus.BehaviorReview ? ["accept_behavior", "revise", "cancel"] : ["approve", "revise", "edit_yaml", "cancel"], AllowAbandon = true
                };
                var answer = await provider.RequestInputAsync(question, ct);
                if (HumanInputContract.IsAbandoned(answer)) command.Kind = "cancel";
                else if (state.Status == PlanningStatus.Clarification) { command.Kind = "answer"; command.Answers = answer as JsonObject; }
                else
                {
                    command.Kind = (answer is JsonObject obj ? obj["response"] : answer)?.GetValue<string>() ?? "cancel";
                    command.ArtifactHash = state.ArtifactHash;
                    if (command.Kind is "revise" or "edit_yaml")
                    {
                        var edit = await provider.RequestInputAsync(new HumanInputRequest
                        {
                            RunId = state.Request.SessionId, StepId = "edit-" + state.Revision,
                            Prompt = command.Kind == "revise" ? "Describe the changes to make." : "Paste the edited YAML for complete revalidation.",
                            Mode = "text", AllowAbandon = true
                        }, ct);
                        if (HumanInputContract.IsAbandoned(edit)) command.Kind = "cancel";
                        else command.Text = (edit is JsonObject edited ? edited["response"] : edit)?.GetValue<string>();
                    }
                }
            }
            state = await planner.AdvanceAsync(state, command, runtime, ct);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.version", 2);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.outcome", state.Status);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.active_ms", state.ActiveMilliseconds);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.human_wait_ms", state.HumanWaitMilliseconds);
        }
        if (state.Status != PlanningStatus.Approved)
            throw new WorkflowRuntimeException(state.Status == PlanningStatus.Cancelled ? ErrorCodes.WorkflowPlanAborted : ErrorCodes.TemplatePlan,
                state.Diagnostics.FirstOrDefault()?.Message ?? "Typed workflow planning stopped.");
        return new JsonObject
        {
            ["yaml"] = state.Yaml,
            ["workflow"] = new JsonObject { ["version"] = 1, ["name"] = state.Request.Name, ["workflows"] = new JsonArray(Parsing.WorkflowParser.Parse(state.Yaml!).Workflows.Keys.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()) },
            ["meta"] = new JsonObject { ["model"] = target.Model, ["planner_version"] = 2, ["attempt"] = state.RepairAttempt + 1, ["revision"] = state.Revision, ["artifact_hash"] = state.ArtifactHash, ["capability_preflight"] = state.Preparation?.LockedContract.DeepClone() },
            ["diagnostics"] = new JsonArray()
        };
    }
}
