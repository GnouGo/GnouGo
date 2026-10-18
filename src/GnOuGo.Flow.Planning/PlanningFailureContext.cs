using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningPrompt
{
    // Only machine diagnostics, never free-form provider messages, scripts or
    // observations. Follow known error envelopes, not arbitrary payload trees.
    internal static JsonObject? FailureContext(JsonObject? evidence)
    {
        if (evidence is null) return null;
        var result = new JsonObject();
        var remaining = 24;
        Visit(evidence, 0);
        return result;

        void Visit(JsonObject node, int depth)
        {
            if (depth > 6 || remaining-- <= 0) { result["correction_blocked"] = true; return; }
            if ((node["error_code"] ?? node["code"])?.ToString() is { } code && code is not
                ("CONTRACT_UNSATISFIED" or "EXPR_TYPE_MISMATCH" or "MCP_REQUEST_EXPR_TYPE_MISMATCH" or "STEP_OUTPUT_PROPERTY_UNKNOWN" or "STEP_REFERENCE_UNKNOWN"))
                result["correction_blocked"] = true;
            var admission = node["classification"]?.ToString() is "mapping_request_admission" or "request_input_allowance" or "request_output_allowance";
            if (admission ||
                node["dispatch_status"]?.ToString() is "unknown" or "uncertain" ||
                node["error_code"]?.ToString() is "RUN_NEEDS_RECONCILIATION" or "AGENT_OUTCOME_UNCERTAIN" or "LLM_BUDGET_EXCEEDED" or "LLM_BUDGET_UNVERIFIABLE")
                result["correction_blocked"] = true;
            foreach (var name in new[] { "error_code", "workflow", "failed_workflow", "step_id", "failed_step_id", "step_type", "execution_phase",
                "mapping_result_path", "mapping_failure_kind", "mapping_failure_reason", "mapping_node_kind", "helper", "consumer_binding", "classification", "dispatch_status", "stage" })
                if (!result.ContainsKey(name) && node[name] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    text.Length is > 0 and <= 512 && text.All(c => char.IsLetterOrDigit(c) || "_-/.$:[]".Contains(c)) &&
                    !text.Contains("://", StringComparison.Ordinal)) result[name] = text;
            if (admission)
            {
                foreach (var name in new[] { "limit_kind", "measurement", "unit", "reason", "limiting_factor" })
                    if (!result.ContainsKey(name) && node[name] is JsonValue value && value.TryGetValue<string>(out var text) &&
                        text.Length is > 0 and <= 80 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) result[name] = text;
                foreach (var name in new[] { "limit", "current", "host_limit", "model_limit", "effective_limit", "framing_allowance",
                    "empty_request_bound", "smallest_complete_example_request_bound", "attempted_request_bound", "examined_items", "source_items" })
                    if (!result.ContainsKey(name) && node[name] is JsonValue value &&
                        (value.TryGetValue<long>(out var count) || value.TryGetValue<int>(out var integer) && (count = integer) >= 0) && count >= 0)
                        result[name] = count;
            }
            if (!result.ContainsKey("mapping_operator") && node["mapping_operator"] is JsonValue operation && operation.TryGetValue<string>(out var token) &&
                token.Length is > 0 and <= 32 && token.All(c => char.IsLetter(c) || "+-*/%<>=!&|^~?".Contains(c))) result["mapping_operator"] = token;
            if (!result.ContainsKey("mapping_attempts") && node["mapping_attempts"] is JsonValue attempts && attempts.TryGetValue<int>(out var used) && used >= 0)
                result["mapping_attempts"] = used;
            foreach (var name in new[] { "script_start", "script_length" })
                if (!result.ContainsKey(name) && node[name] is JsonValue count && count.TryGetValue<int>(out var number) && number is >= 0 and <= 65536)
                    result[name] = number;
            foreach (var key in new[] { "details", "last_error", "error", "inner_error", "inner" })
                if (node[key] is JsonObject child) Visit(child, depth + 1);
        }
    }
}
