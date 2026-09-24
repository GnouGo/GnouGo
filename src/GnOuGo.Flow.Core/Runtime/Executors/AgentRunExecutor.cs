using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Executes one bounded adaptive stage through injected, provider-neutral contracts.</summary>
public sealed class AgentRunExecutor : IStepExecutor
{
    public string StepType => "agent.run";
    public StepContract Contract => new(
        AgentTaskContracts.InputSchema,
        JsonNode.Parse("""{"type":"object","properties":{"status":{"type":"string"},"output":{},"evidence":{"type":"array"},"artifacts":{"type":"array"},"usage":{"type":"object"},"verification":{"type":"array"}},"required":["status","output","evidence","artifacts","usage","verification"]}""")!.AsObject(),
        InputRequired: true);

    public string DslSnippet => """
        ### agent.run — Bounded adaptive work
        Input declares runner, objective, inputs, output_schema, workspace, capabilities,
        budget (max_elapsed_milliseconds, max_model_calls, max_total_tokens), and verification
        requirements (id, kind, subject, facts_schema). The host supplies the runner.
        Successful output requires observed evidence for every approved requirement.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        AgentTaskContext context;
        IAgentTaskRunner runner;
        try
        {
            var taskDefinition = AgentTaskContracts.Parse(ctx.Engine.GetResolvedInput(ctx));
            if (string.IsNullOrWhiteSpace(ctx.Limits.TenantId) || string.IsNullOrWhiteSpace(ctx.Limits.RunId))
                throw Failure(ErrorCodes.InputValidation, "Agent execution requires tenant and run identities.");
            if (!ctx.Engine.AgentTaskRunners.TryGetValue(taskDefinition.Runner, out runner!))
                throw Failure("AGENT_RUNNER_UNAVAILABLE", "The configured agent runner is unavailable.");
            context = new(ctx.Limits.TenantId, ctx.Limits.RunId, ctx.InvocationId, taskDefinition)
            {
                ExecutionId = ctx.Limits.ExecutionId, AgentId = ctx.Limits.AgentId, AgentName = ctx.Limits.AgentName,
                HumanInput = (request, phase) => ctx.AddTelemetryEvent(phase == "Waiting" ? "gnougo-flow.step.waiting_for_human" : "gnougo-flow.step.human_input_resumed",
                    [new("gnougo-flow.human.prompt", request.Prompt), new("gnougo-flow.human.request", HumanInputContract.BuildRequestPayload(request).ToJsonString()),
                     new("gnougo-flow.human.run_id", request.RunId), new("gnougo-flow.human.step_id", request.StepId), new("gnougo-flow.human.phase", phase.ToLowerInvariant())]),
                Progress = progress => ctx.AddTelemetryEvent("gnougo-flow.step.thinking",
                    [new("gnougo-flow.thinking.message", progress.Message), new("gnougo-flow.thinking.kind", progress.Kind),
                     new("gnougo-flow.thinking.source", "agent.progress"), new("gnougo-flow.invocation.id", ctx.InvocationId)])
            };
            var errors = await runner.ValidateAsync(context, ct);
            if (errors.Count != 0)
                throw Failure("AGENT_SCOPE_UNSUPPORTED", "The runner cannot enforce the approved task scope: " + string.Join("; ", errors));
        }
        catch
        {
            // No task dispatch took place. Recovery must not classify a rejected scope as an uncertain external effect.
            await ctx.RecordExternalCompletionAsync(new JsonObject { ["status"] = "rejected_before_dispatch" }, CancellationToken.None);
            throw;
        }
        var task = context.Task;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(task.Budget.MaxElapsedMilliseconds));
        AgentTaskResult result;
        try { result = await runner.RunAsync(context, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw Failure("AGENT_OUTCOME_UNCERTAIN", "The agent deadline expired; reconcile the invocation before continuing."); }
        if (result.Status != "needs_reconciliation")
            await ctx.RecordExternalCompletionAsync(JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult), CancellationToken.None);
        return await ValidateResultAsync(context, result, ctx.Engine.AgentTaskVerifier, ct,
            verified => ctx.RecordExternalCompletionAsync(JsonSerializer.SerializeToNode(verified, AgentTaskJsonContext.Default.AgentTaskResult), CancellationToken.None));
    }

    internal static async Task<JsonNode?> ValidateResultAsync(AgentTaskContext context, AgentTaskResult result, IAgentTaskVerifier verifier, CancellationToken ct, Func<AgentTaskResult, Task>? persistVerified = null)
    {
        var task = context.Task;
        if (result.Status != "completed")
            throw Failure(result.Status == "needs_reconciliation" ? "AGENT_OUTCOME_UNCERTAIN" : "AGENT_TASK_FAILED",
                "The agent did not complete the approved task.");
        if (result.Usage.ModelCalls < 0 || result.Usage.TotalTokens < 0 || !double.IsFinite(result.Usage.ElapsedMilliseconds) ||
            result.Usage.ElapsedMilliseconds < 0 || result.Usage.ModelCalls > task.Budget.MaxModelCalls ||
            result.Usage.TotalTokens > task.Budget.MaxTotalTokens || result.Usage.ElapsedMilliseconds > task.Budget.MaxElapsedMilliseconds)
            throw Failure("AGENT_BUDGET_EXCEEDED", "Observed agent usage violates the approved budget.");
        if (JsonSchemaContractValidator.ValidateInstance(result.Output, task.OutputSchema).Count > 0)
            throw Failure("AGENT_OUTPUT_INVALID", "The agent result does not satisfy its approved output contract.");
        if (result.Evidence.Any(e => string.IsNullOrWhiteSpace(e.Id)) ||
            result.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != result.Evidence.Count)
            throw Failure("AGENT_EVIDENCE_INVALID", "Execution evidence requires unique observation identities.");
        var findings = await verifier.VerifyAsync(context, result, ct);
        result = result with { Verification = findings };
        if (persistVerified is not null) await persistVerified(result);
        if (findings.Count != task.Verification.Count || findings.Any(f => !f.Passed) ||
            !findings.Select(f => f.RequirementId).Order(StringComparer.Ordinal)
                .SequenceEqual(task.Verification.Select(v => v.Id).Order(StringComparer.Ordinal)))
            throw Failure("AGENT_VERIFICATION_FAILED", "Observed execution does not establish every required task outcome.");
        var output = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult)!.AsObject();
        return output;
    }

    private static WorkflowRuntimeException Failure(string code, string message) => new(code, message, retryable: false);
}
