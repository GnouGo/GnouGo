using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed class WorkflowPlanExecutor : IStepExecutor
{
    public string StepType => "workflow.plan";

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires an object input.");
        return ExecutePlanAsync(ctx, input, ct);
    }

    private async Task<JsonNode?> ExecutePlanAsync(StepExecutionContext ctx, JsonObject input, CancellationToken ct)
    {
        var planner = ctx.Engine.WorkflowPlanner ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Workflow planning requires an injected IWorkflowPlanner.");
        var generator = input["generator"] as JsonObject ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires generator settings.");
        var options = (JsonObject)input.DeepClone();
        var target = ctx.Engine.ResolveLlmTarget(generator["provider"]?.GetValue<string>(), generator["model"]?.GetValue<string>());
        options["generator"]!["provider"] = target.Provider;
        options["generator"]!["model"] = target.Model;
        var budget = PlanningBudgetOptions.Parse(input);
        var state = new PlanningSnapshot
        {
            Request = new PlanningRequest
            {
                TenantId = ctx.Limits.TenantId ?? "default",
                Name = input["name"]?.GetValue<string>() ?? "generated",
                Prompt = input["raw_prompt"]?.GetValue<string>() ?? "",
                Options = options,
                MaxConcurrency = input["max_concurrency"]?.GetValue<int>() ?? 4,
                MaxRepairs = input["max_repairs"]?.GetValue<int>() ?? 3,
                Generation = new()
                {
                    Reasoning = generator["reasoning"]?.GetValue<string>(),
                    MaxInputTokensPerRequest = generator["max_input_tokens"]?.GetValue<int>() ?? 12_000,
                    MaxOutputTokens = generator["max_output_tokens"]?.GetValue<int>() ?? 8_192
                }
            }
        };
        await using var session = await (ctx.Engine.PlanningRuntimeFactory
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Workflow planning requires an injected IPlanningRuntimeFactory."))
            .OpenAsync(ctx, state, ct);
        state = session.Snapshot;
        var runtime = session.Runtime;
        while (!PlanningStatus.IsTerminal(state.Status))
        {
            if (budget?.MaxElapsed is { } maxElapsed && state.ActiveMilliseconds >= maxElapsed.TotalMilliseconds)
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded, "The active planning time budget was exhausted.");
            var command = new PlanningCommand { ExpectedRevision = state.Revision };
            if (PlanningStatus.IsWaiting(state.Status))
            {
                var provider = ctx.Engine.HumanInputProvider ?? throw new WorkflowRuntimeException(ErrorCodes.WorkflowPlanClarificationFailed, "The typed planner requires a human-input provider for review.");
                HumanInputRequest question;
                if (state.Status == PlanningStatus.Clarification) question = state.Intent.Question!;
                else if (state.Status == PlanningStatus.Recovery) question = new HumanInputRequest
                {
                    RunId = state.Request.SessionId,
                    StepId = "recovery-" + state.Revision,
                    Prompt = PlanningPhase.Resolve(state) == PlanningPhase.Intent ? "The generated clarification could not be validated. Retry, edit the request, or cancel." : "Automatic construction paused. Inspect the current findings, retry, change the behavior, or cancel.",
                    Context = JsonValue.Create(string.Join("\n", state.Diagnostics.Select(d => d.Code + ": " + d.Message))),
                    Mode = "choice",
                    Choices = ["retry", state.ApprovedBehaviorHash is not null ? "revise" : "edit_intent", "cancel"],
                    AllowAbandon = true
                };
                else question = new HumanInputRequest
                {
                    RunId = state.Request.SessionId,
                    StepId = "review-" + state.Revision,
                    Prompt = state.Status == PlanningStatus.BehaviorReview ? "Review the proposed workflow behavior." : "Review the validated workflow. Synthetic checks do not prove live external behavior.",
                    Context = JsonValue.Create(state.Status == PlanningStatus.BehaviorReview ? state.ReviewMarkdown : state.ReviewMarkdown + "\n\nValidated YAML:\n```yaml\n" + state.Yaml + "\n```"),
                    Mode = "choice",
                    Choices = state.Status == PlanningStatus.BehaviorReview ? ["accept_behavior", "revise", "cancel"] : ["approve", "revise", "cancel"],
                    AllowAbandon = true
                };
                var answer = await provider.RequestInputAsync(question, ct);
                if (HumanInputContract.IsAbandoned(answer)) command.Kind = "cancel";
                else if (state.Status == PlanningStatus.Clarification) { command.Kind = "answer"; command.Answers = answer as JsonObject; }
                else
                {
                    command.Kind = (answer is JsonObject obj ? obj["response"] : answer)?.GetValue<string>() ?? "cancel";
                    command.ArtifactHash = state.ArtifactHash;
                    if (command.Kind is "revise" or "edit_intent")
                    {
                        var edit = await provider.RequestInputAsync(new HumanInputRequest
                        {
                            RunId = state.Request.SessionId,
                            StepId = "edit-" + state.Revision,
                            Prompt = command.Kind == "edit_intent" ? "Edit the request. Previous answers will be archived; cumulative budgets are retained." :
                                "Describe the changes to make.",
                            Mode = "text",
                            AllowAbandon = true
                        }, ct);
                        if (HumanInputContract.IsAbandoned(edit)) command.Kind = "cancel";
                        else command.Text = (edit is JsonObject edited ? edited["response"] : edit)?.GetValue<string>();
                    }
                }
            }
            state = await planner.AdvanceAsync(state, command, runtime, ct);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.version", 2);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.status", state.Status);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.phase", PlanningPhase.Resolve(state));
            ctx.SetTelemetryAttribute("gnougo-flow.plan.outcome", state.Outcome);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.active_ms", state.ActiveMilliseconds);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.human_wait_ms", state.HumanWaitMilliseconds);
        }
        if (state.Status != PlanningStatus.Approved)
            throw new WorkflowRuntimeException(state.Status == PlanningStatus.Cancelled ? ErrorCodes.WorkflowPlanAborted : ErrorCodes.TemplatePlan,
                state.Diagnostics.FirstOrDefault()?.Message ?? "Typed workflow planning stopped.");
        var catalog = await runtime.ValidateCatalogAsync(state.Preparation!, ct);
        if (catalog.Any(d => d.Required))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "The capability catalog changed after approval. Revise the planning session.");
        return new JsonObject
        {
            ["yaml"] = state.Yaml,
            ["workflow"] = new JsonObject { ["version"] = 1, ["name"] = state.Request.Name, ["workflows"] = new JsonArray(Parsing.WorkflowParser.Parse(state.Yaml!).Workflows.Keys.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()) },
            ["meta"] = new JsonObject { ["model"] = target.Model, ["attempt"] = state.Construction.Repairs + 1, ["revision"] = state.Revision, ["artifact_hash"] = state.ArtifactHash, ["capability_preflight"] = state.Preparation?.LockedContract.DeepClone() },
            ["diagnostics"] = new JsonArray()
        };
    }
}
