using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private const int AutoModeBasicCyclomaticThreshold = 10;

    private sealed record WorkflowPlanModeSelection(
        string SelectedMode,
        int? CyclomaticComplexity,
        int? BranchCount,
        double? Confidence,
        string? Reason,
        bool UsedFallback,
        string? RawResponse);

    private static string? GetConfiguredPlanMode(JsonObject input)
    {
        var mode = TryGetString(input["mode"]);
        if (!string.IsNullOrWhiteSpace(mode))
            return mode.Trim();

        return input["generator"] is JsonObject generator
            ? TryGetString(generator["mode"])?.Trim()
            : null;
    }

    private async Task<WorkflowPlanModeSelection> ClassifyPlanModeAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "No LLM client configured");

        var generator = input["generator"] as JsonObject;
        var requestedModel = TryGetString(generator?["model"]);
        var requestedProvider = TryGetString(generator?["provider"]);
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        model ??= "gpt-4";

        var reasoning = TryGetString(generator?["reasoning"]);
        if (string.IsNullOrWhiteSpace(reasoning))
            reasoning = "low";

        var prompt = BuildAutoModeClassificationPrompt(input);
        using var span = ctx.BeginTelemetrySpan("workflow.plan.classify_mode", "classification", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gnougo-flow.plan.mode.requested", "auto"),
            new KeyValuePair<string, object?>("gnougo-flow.plan.auto.threshold", AutoModeBasicCyclomaticThreshold)
        });

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Classifying workflow planning complexity for auto mode."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        if (ctx.Limits.LogStepContent)
        {
            span.AddEvent("gen_ai.content.prompt", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "classification")
            });
        }

        try
        {
            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                StructuredOutputStrict = true,
                StructuredOutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["mode", "cyclomatic_complexity", "branch_count", "confidence", "reason"],
                  "properties": {
                    "mode": { "type": "string", "enum": ["basic", "pipeline"] },
                    "cyclomatic_complexity": { "type": "integer", "minimum": 1 },
                    "branch_count": { "type": "integer", "minimum": 0 },
                    "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                    "reason": { "type": "string" }
                  }
                }
                """)
            }, ct);

            span.SetAttribute("gen_ai.response.model", model);
            span.SetAttribute("gen_ai.response.finish_reason", "stop");
            AddUsageAttributes(span, response.Usage, model, provider);

            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                span.AddEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "classification")
                });
            }

            var selection = ParsePlanModeSelection(response);
            span.SetAttribute("gnougo-flow.plan.mode.selected", selection.SelectedMode);
            if (selection.CyclomaticComplexity.HasValue)
                span.SetAttribute("gnougo-flow.plan.auto.cyclomatic_complexity", selection.CyclomaticComplexity.Value);
            if (selection.BranchCount.HasValue)
                span.SetAttribute("gnougo-flow.plan.auto.branch_count", selection.BranchCount.Value);
            if (selection.Confidence.HasValue)
                span.SetAttribute("gnougo-flow.plan.auto.confidence", selection.Confidence.Value);
            if (selection.UsedFallback)
                span.SetAttribute("gnougo-flow.plan.auto.fallback", true);

            ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", selection.SelectedMode);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.mode.source", selection.UsedFallback ? "auto_fallback" : "auto");
            return selection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span.Fail(ex);
            ctx.Engine.Logger.LogWarning(ex, "workflow.plan auto mode classification failed, falling back to basic mode");
            var fallback = new WorkflowPlanModeSelection(
                "basic",
                null,
                null,
                null,
                "Classifier failed or returned invalid JSON; defaulted to basic mode.",
                true,
                null);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", fallback.SelectedMode);
            ctx.SetTelemetryAttribute("gnougo-flow.plan.mode.source", "auto_fallback");
            return fallback;
        }
    }

    private static string BuildAutoModeClassificationPrompt(JsonObject input)
    {
        var generator = input["generator"] as JsonObject;
        var rawPrompt = TryGetString(input["raw_prompt"])
            ?? TryGetString(generator?["raw_prompt"])
            ?? "";
        var instruction = TryGetString(generator?["instruction"]) ?? "";
        var context = TryGetString(generator?["context"]) ?? "";
        var policy = input["policy"]?.ToJsonString(PromptJsonOptions) ?? "{}";
        var limits = input["limits"]?.ToJsonString(PromptJsonOptions) ?? "{}";

        var sb = new StringBuilder();
        sb.AppendLine("You classify a GnOuGo workflow.plan request before workflow generation.");
        sb.AppendLine("Return ONLY JSON that matches the requested schema.");
        sb.AppendLine();
        sb.AppendLine("Decision rule:");
        sb.AppendLine($"- Choose \"basic\" when estimated cyclomatic complexity is less than {AutoModeBasicCyclomaticThreshold} branching points and the workflow can be generated coherently in one plan.");
        sb.AppendLine($"- Choose \"pipeline\" when estimated cyclomatic complexity is {AutoModeBasicCyclomaticThreshold} or more, or when the request should be decomposed into many small leaf workflows before assembling a main workflow.");
        sb.AppendLine("- Count branching points from conditions, switch/case paths, loops, retries, error handling, cleanup paths, validation branches, tool-orchestration choices, and state transitions.");
        sb.AppendLine("- Prefer \"pipeline\" when several independent phases, tools, or responsibilities would make one generated workflow brittle.");
        sb.AppendLine("- Prefer \"basic\" for simple linear flows, small conditionals, or requests with fewer than 10 meaningful branches.");
        sb.AppendLine();
        AppendPromptSection(sb, "raw_prompt", rawPrompt);
        sb.AppendLine();
        AppendPromptSection(sb, "generator_instruction", instruction);
        sb.AppendLine();
        AppendPromptSection(sb, "generator_context", context);
        sb.AppendLine();
        AppendPromptSection(sb, "policy_json", policy);
        sb.AppendLine();
        AppendPromptSection(sb, "limits_json", limits);
        return sb.ToString();
    }

    private static WorkflowPlanModeSelection ParsePlanModeSelection(LLMResponse response)
    {
        JsonObject? payload = response.Json as JsonObject;
        if (payload == null && !string.IsNullOrWhiteSpace(response.Text))
        {
            try
            {
                payload = JsonNode.Parse(StripMarkdownFences(response.Text).Trim()) as JsonObject;
            }
            catch
            {
                payload = null;
            }
        }

        if (payload == null)
            return new WorkflowPlanModeSelection("basic", null, null, null, "Classifier returned non-JSON content; defaulted to basic mode.", true, response.Text);

        var mode = TryGetString(payload["mode"]);
        var complexity = TryGetInt(payload["cyclomatic_complexity"]);
        var branchCount = TryGetInt(payload["branch_count"]);
        var confidence = TryGetDouble(payload["confidence"]);
        var reason = TryGetString(payload["reason"]);

        var hasDecisionSignal =
            string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "pipeline", StringComparison.OrdinalIgnoreCase)
            || complexity.HasValue
            || branchCount.HasValue;
        if (!hasDecisionSignal)
            return new WorkflowPlanModeSelection("basic", null, null, null, "Classifier JSON did not include a mode or complexity signal; defaulted to basic mode.", true, response.Text);

        var selectedMode = string.Equals(mode, "pipeline", StringComparison.OrdinalIgnoreCase)
            || (complexity.HasValue && complexity.Value >= AutoModeBasicCyclomaticThreshold)
            || (branchCount.HasValue && branchCount.Value >= AutoModeBasicCyclomaticThreshold)
                ? "pipeline"
                : "basic";

        return new WorkflowPlanModeSelection(
            selectedMode,
            complexity,
            branchCount,
            confidence,
            reason,
            false,
            response.Text);
    }

    private static void AttachPlanModeMetadata(JsonNode? result, string mode, WorkflowPlanModeSelection? selection)
    {
        if (result is not JsonObject resultObject)
            return;

        var meta = resultObject["meta"] as JsonObject;
        if (meta == null)
        {
            meta = new JsonObject();
            resultObject["meta"] = meta;
        }

        meta["mode"] = mode;
        if (selection == null)
            return;

        var modeSelection = new JsonObject
        {
            ["source"] = selection.UsedFallback ? "auto_fallback" : "auto",
            ["selected_mode"] = selection.SelectedMode,
            ["threshold"] = AutoModeBasicCyclomaticThreshold
        };
        if (selection.CyclomaticComplexity.HasValue)
            modeSelection["cyclomatic_complexity"] = selection.CyclomaticComplexity.Value;
        if (selection.BranchCount.HasValue)
            modeSelection["branch_count"] = selection.BranchCount.Value;
        if (selection.Confidence.HasValue)
            modeSelection["confidence"] = selection.Confidence.Value;
        if (!string.IsNullOrWhiteSpace(selection.Reason))
            modeSelection["reason"] = selection.Reason;

        meta["mode_selection"] = modeSelection;
    }

    private static string? TryGetString(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            try
            {
                var value = node.GetValue<double>();
                return (int)Math.Round(value);
            }
            catch
            {
                return int.TryParse(TryGetString(node), out var parsed) ? parsed : null;
            }
        }
    }

    private static double? TryGetDouble(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return double.TryParse(TryGetString(node), out var parsed) ? parsed : null;
        }
    }
}
