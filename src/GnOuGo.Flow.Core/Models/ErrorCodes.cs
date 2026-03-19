
namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Standard error codes for workflow errors.
/// </summary>
public static class ErrorCodes
{
    public const string ExprParse = "EXPR_PARSE";
    public const string ExprTypeMismatch = "EXPR_TYPE_MISMATCH";
    public const string EvalError = "EVAL_ERROR";
    public const string InputValidation = "INPUT_VALIDATION";
    public const string TemplatePlan = "TEMPLATE_PLAN";
    public const string TemplatePolicy = "TEMPLATE_POLICY";
    public const string TemplateSyntax = "TEMPLATE_SYNTAX";
    public const string TemplateRender = "TEMPLATE_RENDER";
    public const string TemplateMissingVar = "TEMPLATE_MISSING_VAR";
    public const string JsonParse = "JSON_PARSE";
    public const string LlmTimeout = "LLM_TIMEOUT";
    public const string LlmNetwork = "LLM_NETWORK";
    public const string LlmSchema = "LLM_SCHEMA";
    public const string WorkflowFetchPolicy = "WORKFLOW_FETCH_POLICY";
    public const string WorkflowFetchNetwork = "WORKFLOW_FETCH_NETWORK";
    public const string WorkflowFetchIntegrity = "WORKFLOW_FETCH_INTEGRITY";
    public const string WorkflowCycleDetected = "WORKFLOW_CYCLE_DETECTED";
    public const string LoopLimit = "LOOP_LIMIT";
    public const string ParallelLimit = "PARALLEL_LIMIT";
    public const string ScriptError = "SCRIPT_ERROR";
    public const string StepTypeUnknown = "STEP_TYPE_UNKNOWN";


    // MCP
    public const string McpConnectionError = "MCP_CONNECTION_ERROR";
    public const string McpCallError = "MCP_CALL_ERROR";
    public const string McpListError = "MCP_LIST_ERROR";
    public const string McpPromptError = "MCP_PROMPT_ERROR";
    public const string McpTimeout = "MCP_TIMEOUT";
    public const string McpServerNotFound = "MCP_SERVER_NOT_FOUND";
}

