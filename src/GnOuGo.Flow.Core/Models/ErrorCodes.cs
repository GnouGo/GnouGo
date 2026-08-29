
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
    public const string CapabilityPreflightUnavailable = "CAPABILITY_PREFLIGHT_UNAVAILABLE";
    public const string CapabilityPreflightDiscoveryFailed = "CAPABILITY_PREFLIGHT_DISCOVERY_FAILED";
    public const string CapabilityPreflightInferenceFailed = "CAPABILITY_PREFLIGHT_INFERENCE_FAILED";
    public const string CapabilityPreflightRedundantArtifactProducer = "CAPABILITY_PREFLIGHT_REDUNDANT_ARTIFACT_PRODUCER";
    public const string WorkflowPlanClarificationFailed = "WORKFLOW_PLAN_CLARIFICATION_FAILED";
    public const string WorkflowPlanCannotPlanSafely = "WORKFLOW_PLAN_CANNOT_PLAN_SAFELY";
    public const string WorkflowPlanAborted = "WORKFLOW_PLAN_ABORTED";
    public const string WorkflowPlanRepairStalled = "WORKFLOW_PLAN_REPAIR_STALLED";
    public const string WorkflowFinalizationFailed = "WORKFLOW_FINALIZATION_FAILED";
    public const string WorkflowFinalizationTimeout = "WORKFLOW_FINALIZATION_TIMEOUT";
    public const string JsonParse = "JSON_PARSE";
    public const string LlmTimeout = "LLM_TIMEOUT";
    public const string LlmNetwork = "LLM_NETWORK";
    public const string LlmProvider = "LLM_PROVIDER";
    public const string LlmBudgetExceeded = "LLM_BUDGET_EXCEEDED";
    public const string LlmBudgetUnverifiable = "LLM_BUDGET_UNVERIFIABLE";
    public const string LlmSchema = "LLM_SCHEMA";
    public const string WorkflowFetchPolicy = "WORKFLOW_FETCH_POLICY";
    public const string WorkflowFetchNetwork = "WORKFLOW_FETCH_NETWORK";
    public const string WorkflowFetchIntegrity = "WORKFLOW_FETCH_INTEGRITY";
    public const string WorkflowCycleDetected = "WORKFLOW_CYCLE_DETECTED";
    public const string LoopLimit = "LOOP_LIMIT";
    public const string ParallelLimit = "PARALLEL_LIMIT";
    public const string ScriptError = "SCRIPT_ERROR";
    public const string StepTypeUnknown = "STEP_TYPE_UNKNOWN";
    public const string SkillRequired = "SKILL_REQUIRED";


    // MCP
    public const string McpConnectionError = "MCP_CONNECTION_ERROR";
    public const string McpCallError = "MCP_CALL_ERROR";
    public const string McpListError = "MCP_LIST_ERROR";
    public const string McpPromptError = "MCP_PROMPT_ERROR";
    public const string McpTimeout = "MCP_TIMEOUT";
    public const string McpServerNotFound = "MCP_SERVER_NOT_FOUND";
}
