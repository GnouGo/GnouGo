using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Calls an LLM via the configured ILLMClient.
/// </summary>
public sealed class LlmCallExecutor : IStepExecutor
{
    public string StepType => "llm.call";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object is malformed or the required `prompt` field is missing, and no runtime default `model` is available."),
        new(ErrorCodes.LlmTimeout, true, "The LLM request timed out. This is retryable and is a good candidate for `retry`."),
        new(ErrorCodes.LlmNetwork, true, "A transient network failure occurred while calling the LLM provider."),
        new(ErrorCodes.LlmNetwork, false, "The LLM client is not configured or the provider failed in a non-transient way."),
        new(ErrorCodes.LlmSchema, false, "structured_output was requested but the LLM returned a response that could not be parsed as valid JSON.")
    };

    public string DslSnippet => """
        ### llm.call — Call a language model
        IMPORTANT: use `prompt` (NOT `messages`). `prompt` is REQUIRED. `model` is required unless the runtime injects a default model.
        IMPORTANT: `temperature` and `reasoning` are optional overrides. Omit them unless the task explicitly needs them; the runtime applies defaults and removes unsupported parameters based on model capabilities.
        Basic call:
        ```yaml
        - id: summarize
          type: llm.call
          input:
            model: gpt-4                        # optional when runtime defaults are configured
            prompt: "Summarize: ${data.steps.prev.text}"  # required — plain string
            system: "You are a helpful assistant."  # optional
            temperature: 0.7                     # optional override; omit by default
            reasoning: high                      # optional override; omit by default
            max_tokens: 2048                     # optional
        ```
        Structured output:
        IMPORTANT for `strict: true` (OpenAI/GitHub Models response_format json_schema):
        - Every schema object with `properties` MUST have `required` listing EVERY key from `properties`.
        - Do NOT list only the fields that feel mandatory; strict mode rejects omitted property names.
        - Optional fields must still be listed in `required`; represent them as nullable with `anyOf: [{ type: <type> }, { type: "null" }]`.
        - Add `additionalProperties: false` on every object schema for portability. The OpenAI provider also patches it automatically in strict mode.
        ```yaml
        - id: classify
          type: llm.call
          input:
            model: gpt-4
            prompt: "Classify this ticket and return JSON"
            structured_output:
              schema_inline:
                type: object
                properties:
                  category: { type: string }
                  priority: { type: string }
                  notes:
                    anyOf:
                      - type: string
                      - type: "null"
                required: [category, priority, notes]   # every property above is listed, including nullable optional fields
                additionalProperties: false
              strict: true                       # optional
        ```
        You can also use `structured_output.schema_ref` instead of `schema_inline`.
        Output: `{ text: "...", json?: {...}, usage: { prompt_tokens, completion_tokens, total_tokens }, meta: { model } }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.LlmNetwork, "No LLM client configured");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "llm.call input must be object");

        var requestedProvider = input["provider"]?.GetValue<string>();
        var requestedModel = input["model"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "llm.call requires 'model' unless WorkflowEngine.LlmDefaults.Model is configured");
        }

        var prompt = input["prompt"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "llm.call requires 'prompt'");

        var request = new LLMRequest
        {
            Provider = provider,
            Model = model,
            Prompt = prompt,
        };

        // ── Telemetry: record request attributes ──
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", request.Provider ?? "default");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);

        if (ctx.Limits.LogStepContent)
        {
            ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user")
            });
        }

        if (input.TryGetPropertyValue("temperature", out var temp) && temp != null)
        {
            request.Temperature = ExpressionEvaluator.GetNumber(temp);
            ctx.SetTelemetryAttribute("gen_ai.request.temperature", request.Temperature);
        }

        // Reasoning / thinking effort: "minimal"|"low"|"medium"|"high"|"max"|"auto".
        // When omitted, providers fall back to their own defaults ("auto").
        if (input.TryGetPropertyValue("reasoning", out var reasoningNode) && reasoningNode != null)
        {
            var reasoning = reasoningNode.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                request.Reasoning = reasoning;
                ctx.SetTelemetryAttribute("gen_ai.request.reasoning_effort", reasoning);
            }
        }

        // Structured output
        var structuredOutput = input["structured_output"] as JsonObject;
        if (structuredOutput != null)
        {
            request.StructuredOutputSchema = structuredOutput["schema_inline"] ?? structuredOutput["schema_ref"];
            if (structuredOutput.TryGetPropertyValue("strict", out var s) && s != null)
                request.StructuredOutputStrict = s.GetValue<bool>();
        }

        // Max output tokens
        if (input.TryGetPropertyValue("max_tokens", out var maxTokensNode) && maxTokensNode != null)
        {
            request.MaxTokens = (int)ExpressionEvaluator.GetNumber(maxTokensNode);
            ctx.SetTelemetryAttribute("gen_ai.request.max_tokens", request.MaxTokens);
        }

        // ── Thinking: signal that we are calling the LLM ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Calling LLM ({model})…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        try
        {
            var response = await llmClient.CallAsync(request, ct);


            // ── Thinking: preview first 120 chars of the LLM response ──
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                var preview = response.Text.Length > 120
                    ? response.Text[..120] + "…"
                    : response.Text;
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message", preview),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "response")
                });
            }

            // ── Telemetry: record response attributes ──
            var finishReason = response.ToolCalls?.Count > 0 ? "tool_calls" : "stop";
            ctx.SetTelemetryAttribute("gen_ai.response.model", model);
            ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", finishReason);
            long? inputTokens = null;
            long? outputTokens = null;
            if (response.Usage is JsonObject usage)
            {
                if (usage.TryGetPropertyValue("prompt_tokens", out var pt) && pt != null)
                    inputTokens = pt.GetValue<int>();
                else if (usage.TryGetPropertyValue("input_tokens", out var it) && it != null)
                    inputTokens = it.GetValue<int>();

                if (usage.TryGetPropertyValue("completion_tokens", out var ct2) && ct2 != null)
                    outputTokens = ct2.GetValue<int>();
                else if (usage.TryGetPropertyValue("output_tokens", out var ot) && ot != null)
                    outputTokens = ot.GetValue<int>();

                if (inputTokens.HasValue)
                    ctx.SetTelemetryAttribute("gen_ai.usage.input_tokens", inputTokens.Value);
                if (outputTokens.HasValue)
                    ctx.SetTelemetryAttribute("gen_ai.usage.output_tokens", outputTokens.Value);

                if (usage.TryGetPropertyValue("total_tokens", out var tt) && tt != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.total_tokens", tt.GetValue<int>());

                var estimatedCost = ModelMetadataCatalog.EstimateCost(model, inputTokens, outputTokens, providerType: provider);
                if (estimatedCost.HasValue)
                    ctx.SetTelemetryAttribute("gen_ai.usage.cost", (double)estimatedCost.Value);
            }

            if (ctx.Limits.LogStepContent && (!string.IsNullOrWhiteSpace(response.Text) || response.Json != null))
            {
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", !string.IsNullOrWhiteSpace(response.Text)
                        ? response.Text
                        : response.Json?.ToJsonString()),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", finishReason)
                });
            }

            var result = new JsonObject
            {
                ["text"] = response.Text,
                ["meta"] = new JsonObject { ["model"] = model }
            };

            // Structured output: fallback parse response.Text as JSON (parity with Python)
            JsonNode? structuredJson = response.Json;
            if (structuredOutput != null && structuredJson == null && !string.IsNullOrWhiteSpace(response.Text))
            {
                var textToParse = StripMarkdownCodeFences(response.Text);
                try { structuredJson = JsonNode.Parse(textToParse); }
                catch { structuredJson = null; }
            }
            if (structuredOutput != null && structuredJson == null)
                throw new WorkflowRuntimeException(ErrorCodes.LlmSchema,
                    "llm.call structured_output expected valid JSON but the LLM returned an incompatible response", retryable: true);

            if (structuredJson != null)
                result["json"] = structuredJson.DeepClone();
            else if (response.Json != null)
                result["json"] = response.Json.DeepClone();
            if (response.Usage != null)
                result["usage"] = response.Usage.DeepClone();
            if (response.Raw != null)
                result["raw"] = response.Raw.DeepClone();

            return result;
        }
        catch (TimeoutException ex)
        {
            ctx.Engine.Logger.LogError(ex, "llm.call timed out for model '{Model}'", model);
            throw new WorkflowRuntimeException(ErrorCodes.LlmTimeout, ex.Message, retryable: true);
        }
        catch (HttpRequestException ex)
        {
            ctx.Engine.Logger.LogError(ex, "llm.call network error for model '{Model}'", model);
            throw new WorkflowRuntimeException(ErrorCodes.LlmNetwork, ex.Message, retryable: true);
        }
        catch (WorkflowRuntimeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Engine.Logger.LogError(ex, "llm.call failed for model '{Model}'", model);
            throw new WorkflowRuntimeException(ErrorCodes.LlmNetwork, $"LLM call failed: {ex.Message}", retryable: false);
        }
    }

    /// <summary>
    /// Strips markdown code fences (```json ... ``` or ``` ... ```) that LLMs
    /// sometimes wrap around structured JSON responses.
    /// </summary>
    internal static string StripMarkdownCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return trimmed;

        // Remove opening fence (```json, ```JSON, ``` etc.)
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return trimmed;

        var body = trimmed[(firstNewline + 1)..];

        // Remove closing fence
        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0)
            body = body[..lastFence];

        return body.Trim();
    }
}
