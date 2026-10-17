using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed partial class SmartFlowService
{
    internal static bool IsCompositionFailure(WorkflowFailure failure)
    {
        // Positive classification: unknown errors, host policy, resource limits and
        // transport failures never buy a planning call. A handled error is not a
        // request to automatically alter an otherwise successful authored workflow.
        var details = failure.Details;
        var envelopes = new List<JsonObject>();
        var truncated = false;
        Visit(details as JsonObject, 0);
        if (envelopes.Any(e => (e["error_code"] ?? e["code"])?.ToString() is { } nestedCode && nestedCode is not
            ("CONTRACT_UNSATISFIED" or "EXPR_TYPE_MISMATCH" or "MCP_REQUEST_EXPR_TYPE_MISMATCH" or "STEP_OUTPUT_PROPERTY_UNKNOWN" or "STEP_REFERENCE_UNKNOWN"))) return false;
        if (truncated || envelopes.Any(e => e["classification"]?.ToString() == "mapping_request_admission" ||
            e["dispatch_status"]?.ToString() is "unknown" or "uncertain" ||
            e["error_code"]?.ToString() is "RUN_NEEDS_RECONCILIATION" or "AGENT_OUTCOME_UNCERTAIN" or
                ErrorCodes.LlmBudgetExceeded or ErrorCodes.LlmBudgetUnverifiable)) return false;
        if (failure.Code == "CONTRACT_UNSATISFIED")
            return envelopes.Any(e => e["mapping_failure_kind"]?.ToString() is "program" or "data") &&
                envelopes.Any(e => e["mapping_failure_reason"]?.ToString() is "ungrounded_scalar" or "missing_scalar");
        // Generated JavaScript syntax failures can be compiler defects. The model
        // does not author that syntax, so an EXPR_PARSE is not correction authority.
        return failure.Code is ErrorCodes.ExprTypeMismatch or "MCP_REQUEST_EXPR_TYPE_MISMATCH" or
            "STEP_OUTPUT_PROPERTY_UNKNOWN" or "STEP_REFERENCE_UNKNOWN";

        void Visit(JsonObject? node, int depth)
        {
            if (node is null) return;
            if (depth > 6 || envelopes.Count >= 24) { truncated = true; return; }
            envelopes.Add(node);
            foreach (var key in new[] { "details", "last_error", "error", "inner_error", "inner" })
                if (node[key] is JsonObject child) Visit(child, depth + 1);
        }
    }
}
