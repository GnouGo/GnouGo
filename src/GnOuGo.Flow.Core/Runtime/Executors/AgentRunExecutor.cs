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
        JsonNode.Parse("""
        {"type":"object","required":["runner","objective","output_schema","workspace","capabilities","budget","verification"],
         "additionalProperties":false,"properties":{
          "runner":{"type":"string","minLength":1},"objective":{"type":"string","minLength":1},
          "inputs":{"type":"object"},"output_schema":{"type":"object"},"workspace":{"type":"string","minLength":1},
          "capabilities":{"type":"array","items":{"type":"string"},"uniqueItems":true},
          "budget":{"type":"object","additionalProperties":false,"required":["max_elapsed_milliseconds","max_model_calls","max_total_tokens"],
            "properties":{"max_elapsed_milliseconds":{"type":"integer","minimum":1},"max_model_calls":{"type":"integer","minimum":1},"max_total_tokens":{"type":"integer","minimum":1}}},
          "verification":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
            "required":["id","kind","subject","facts_schema"],"properties":{"id":{"type":"string"},"kind":{"type":"string"},
              "subject":{"type":"string"},"facts_schema":{"type":"object"}}}}}}
        """)!.AsObject(),
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
        var input = ctx.Engine.GetResolvedInput(ctx);
        if (JsonSchemaContractValidator.ValidateInstance(input, Contract.InputSchema).Count != 0)
            throw Failure(ErrorCodes.InputValidation, "The agent task does not satisfy its declared input contract.");
        var task = JsonSerializer.Deserialize(input, AgentTaskJsonContext.Default.AgentTaskDefinition)
            ?? throw Failure(ErrorCodes.InputValidation, "An agent task definition is required.");
        if (string.IsNullOrWhiteSpace(ctx.Limits.TenantId) || string.IsNullOrWhiteSpace(ctx.Limits.RunId))
            throw Failure(ErrorCodes.InputValidation, "Agent execution requires tenant and run identities.");
        if (!ctx.Engine.AgentTaskRunners.TryGetValue(task.Runner, out var runner))
            throw Failure("AGENT_RUNNER_UNAVAILABLE", "The configured agent runner is unavailable.");
        if (JsonSchemaContractValidator.ValidateSchema(task.OutputSchema, strictProfile: false).Count > 0 ||
            task.Verification.Any(v => string.IsNullOrWhiteSpace(v.Id) || string.IsNullOrWhiteSpace(v.Kind) ||
                string.IsNullOrWhiteSpace(v.Subject) || JsonSchemaContractValidator.ValidateSchema(v.FactsSchema, strictProfile: false).Count > 0) ||
            task.Verification.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != task.Verification.Count)
            throw Failure(ErrorCodes.InputValidation, "Agent output and verification contracts must be valid and unambiguous.");
        var context = new AgentTaskContext(ctx.Limits.TenantId, ctx.Limits.RunId, ctx.Step.Id, task);
        var errors = await runner.ValidateAsync(context, ct);
        if (errors.Count != 0)
            throw Failure("AGENT_SCOPE_UNSUPPORTED", "The runner cannot enforce the approved task scope: " + string.Join("; ", errors));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(task.Budget.MaxElapsedMilliseconds));
        AgentTaskResult result;
        try { result = await runner.RunAsync(context, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw Failure("AGENT_OUTCOME_UNCERTAIN", "The agent deadline expired; reconcile the invocation before continuing."); }
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
        var findings = await ctx.Engine.AgentTaskVerifier.VerifyAsync(context, result, ct);
        if (findings.Count != task.Verification.Count || findings.Any(f => !f.Passed) ||
            !findings.Select(f => f.RequirementId).Order(StringComparer.Ordinal)
                .SequenceEqual(task.Verification.Select(v => v.Id).Order(StringComparer.Ordinal)))
            throw Failure("AGENT_VERIFICATION_FAILED", "Observed execution does not establish every required task outcome.");
        var output = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult)!.AsObject();
        output["verification"] = JsonSerializer.SerializeToNode(findings.ToList(), AgentTaskJsonContext.Default.ListAgentVerificationFinding);
        return output;
    }

    private static WorkflowRuntimeException Failure(string code, string message) => new(code, message, retryable: false);
}
