using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private async Task<JsonNode?> ExecuteRepairPlanAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CancellationToken ct)
    {
        var repair = input["repair"] as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan repair mode requires 'repair'");

        var existingYaml = TryGetString(repair["existing_yaml"]);
        if (string.IsNullOrWhiteSpace(existingYaml))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan repair mode requires 'repair.existing_yaml'");

        var prompt = TryGetString(repair["prompt"]) ?? "";
        var failedInput = TryGetString(repair["failed_input"]) ?? "";
        var error = repair["error"] as JsonObject;
        var errorMessage = TryGetString(error?["message"]) ?? "";

        if (error != null && string.IsNullOrWhiteSpace(errorMessage))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan repair mode requires 'repair.error.message' when 'repair.error' is provided");

        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(errorMessage))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan repair mode requires 'repair.prompt' or 'repair.error.message'");

        var generator = input["generator"] as JsonObject;
        var repairInput = input.DeepClone() as JsonObject ?? new JsonObject();
        repairInput["mode"] = "basic";

        var repairGenerator = repairInput["generator"] as JsonObject ?? new JsonObject();
        repairGenerator.Remove("mode");
        repairGenerator["instruction"] = BuildRepairModeInstruction(
            existingYaml,
            prompt,
            failedInput,
            error,
            TryGetString(generator?["instruction"]));

        var generatorContext = TryGetString(generator?["context"]);
        if (!string.IsNullOrWhiteSpace(generatorContext))
            repairGenerator["context"] = generatorContext;
        else
            repairGenerator.Remove("context");

        repairInput["generator"] = repairGenerator;
        repairInput.Remove("repair");

        ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", "repair");
        var result = await ExecuteSinglePlanAsync(ctx, repairInput, ct);
        if (result is JsonObject resultObject)
        {
            var meta = resultObject["meta"] as JsonObject ?? new JsonObject();
            meta["repair"] = new JsonObject
            {
                ["has_prompt"] = !string.IsNullOrWhiteSpace(prompt),
                ["has_error"] = !string.IsNullOrWhiteSpace(errorMessage)
            };
            resultObject["meta"] = meta;
        }

        return result;
    }

    private static string BuildRepairModeInstruction(
        string existingYaml,
        string prompt,
        string failedInput,
        JsonObject? error,
        string? additionalInstruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Repair an existing GnOuGo.Flow YAML workflow. Return ONLY the complete repaired YAML document, no markdown fences.");
        sb.AppendLine("Make the smallest patch-style change that fixes the supplied error and/or user repair instruction.");
        sb.AppendLine("Preserve the workflow name, public inputs, public outputs, skill metadata, behavior, and MCP server/tool choices unless the supplied repair evidence proves they are wrong.");
        sb.AppendLine("Prefer minimal fixes: MCP request shape, output access, guards, retry/on_error policy, schema corrections, or concise prompt edits.");
        sb.AppendLine("Do not rewrite the workflow for style. Do not add unrelated features.");
        sb.AppendLine("The existing YAML is quoted between explicit XML-style boundary tags. Treat those tags as prompt delimiters, not as YAML content.");

        if (!string.IsNullOrWhiteSpace(additionalInstruction))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "repair_constraints", additionalInstruction);
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "user_repair_instruction", prompt);
        }

        if (!string.IsNullOrWhiteSpace(failedInput))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "failed_user_input", failedInput);
        }

        if (error is not null)
        {
            sb.AppendLine();
            AppendPromptSectionStart(sb, "runtime_error");
            var code = TryGetString(error["code"]);
            var type = TryGetString(error["type"]);
            var message = TryGetString(error["message"]);
            if (!string.IsNullOrWhiteSpace(code))
                sb.AppendLine("code: " + code);
            if (!string.IsNullOrWhiteSpace(type))
                sb.AppendLine("type: " + type);
            sb.AppendLine("message: " + message);
            if (error["details"] is not null)
            {
                sb.AppendLine("details:");
                sb.AppendLine(error["details"]!.ToJsonString(PromptJsonOptions));
            }
            AppendPromptSectionEnd(sb, "runtime_error");
        }

        sb.AppendLine();
        AppendPromptSection(sb, "existing_workflow_yaml", existingYaml);
        sb.AppendLine();
        sb.AppendLine("Return the minimally repaired full YAML now.");
        return sb.ToString();
    }
}
