using GnOuGo.Flow.Core.Models;
using Xunit;

namespace GnOuGo.Flow.Tests.Models;

public class ErrorCodesTests
{
    [Fact]
    public void ErrorCodes_AllDefined()
    {
        Assert.Equal("EXPR_PARSE", ErrorCodes.ExprParse);
        Assert.Equal("EXPR_TYPE_MISMATCH", ErrorCodes.ExprTypeMismatch);
        Assert.Equal("EVAL_ERROR", ErrorCodes.EvalError);
        Assert.Equal("INPUT_VALIDATION", ErrorCodes.InputValidation);
        Assert.Equal("TEMPLATE_PLAN", ErrorCodes.TemplatePlan);
        Assert.Equal("TEMPLATE_POLICY", ErrorCodes.TemplatePolicy);
        Assert.Equal("TEMPLATE_SYNTAX", ErrorCodes.TemplateSyntax);
        Assert.Equal("TEMPLATE_RENDER", ErrorCodes.TemplateRender);
        Assert.Equal("TEMPLATE_MISSING_VAR", ErrorCodes.TemplateMissingVar);
        Assert.Equal("JSON_PARSE", ErrorCodes.JsonParse);
        Assert.Equal("LLM_TIMEOUT", ErrorCodes.LlmTimeout);
        Assert.Equal("LLM_NETWORK", ErrorCodes.LlmNetwork);
        Assert.Equal("LLM_SCHEMA", ErrorCodes.LlmSchema);
        Assert.Equal("WORKFLOW_FETCH_POLICY", ErrorCodes.WorkflowFetchPolicy);
        Assert.Equal("WORKFLOW_FETCH_NETWORK", ErrorCodes.WorkflowFetchNetwork);
        Assert.Equal("WORKFLOW_FETCH_INTEGRITY", ErrorCodes.WorkflowFetchIntegrity);
        Assert.Equal("WORKFLOW_CYCLE_DETECTED", ErrorCodes.WorkflowCycleDetected);
        Assert.Equal("LOOP_LIMIT", ErrorCodes.LoopLimit);
        Assert.Equal("PARALLEL_LIMIT", ErrorCodes.ParallelLimit);
        Assert.Equal("STEP_TYPE_UNKNOWN", ErrorCodes.StepTypeUnknown);
        Assert.Equal("SCRIPT_ERROR", ErrorCodes.ScriptError);
        Assert.Equal("MCP_CONNECTION_ERROR", ErrorCodes.McpConnectionError);
        Assert.Equal("MCP_CALL_ERROR", ErrorCodes.McpCallError);
        Assert.Equal("MCP_LIST_ERROR", ErrorCodes.McpListError);
        Assert.Equal("MCP_PROMPT_ERROR", ErrorCodes.McpPromptError);
        Assert.Equal("MCP_TIMEOUT", ErrorCodes.McpTimeout);
        Assert.Equal("MCP_SERVER_NOT_FOUND", ErrorCodes.McpServerNotFound);
    }
}

public class WorkflowRuntimeExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var ex = new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("CODE", "message", retryable: true);
        Assert.Equal("CODE", ex.Code);
        Assert.Equal("message", ex.Message);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public void ToWorkflowError_CreatesError()
    {
        var ex = new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("E1", "fail");
        var err = ex.ToWorkflowError();
        Assert.Equal("E1", err.Code);
        Assert.Equal("E1", err.Type);
        Assert.Equal("fail", err.Message);
        Assert.False(err.Retryable);
    }
}

public class RunResultTests
{
    [Fact]
    public void RunResult_DefaultSuccess()
    {
        var r = new RunResult();
        Assert.False(r.Success);
        Assert.NotNull(r.StepResults);
        Assert.Empty(r.StepResults);
    }

    [Fact]
    public void StepResult_DefaultProperties()
    {
        var sr = new StepResult();
        Assert.Equal(StepStatus.Pending, sr.Status);
        Assert.Equal(default, sr.Duration);
    }
}

public class ExecutionLimitsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var limits = new ExecutionLimits();
        Assert.Equal(10_000, limits.MaxTotalStepsExecuted);
        Assert.Equal(1_000, limits.MaxLoopIterations);
        Assert.Equal(20, limits.MaxCallDepth);
        Assert.Equal(50, limits.MaxParallelBranches);
        Assert.Equal(100_000, limits.MaxExpressionStatements);
        Assert.Equal(15, limits.ExpressionTimeoutSeconds);
        Assert.Equal(50_000_000, limits.ExpressionMemoryLimitBytes);
    }
}
