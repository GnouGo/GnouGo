using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
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
        new(ErrorCodes.InputValidation, false, "The input object is malformed or required `model`/`prompt` fields are missing."),
        new(ErrorCodes.LlmTimeout, true, "The LLM request timed out. This is retryable and is a good candidate for `retry`."),
        new(ErrorCodes.LlmNetwork, true, "A transient network failure occurred while calling the LLM provider."),
        new(ErrorCodes.LlmNetwork, false, "The LLM client is not configured or the provider failed in a non-transient way.")
    };

    public string DslSnippet => """
        ### llm.call — Call a language model
        IMPORTANT: use `prompt` (NOT `messages`). `model` and `prompt` are REQUIRED.
        Basic call:
        ```yaml
        - id: summarize
          type: llm.call
          input:
            model: gpt-4                        # required
            prompt: "Summarize: ${data.steps.prev.text}"  # required — plain string
            system: "You are a helpful assistant."  # optional
            temperature: 0.7                     # optional
            max_tokens: 2048                     # optional
        ```
        Structured output:
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
                required: [category, priority]
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

        var model = input["model"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "llm.call requires 'model'");

        var prompt = input["prompt"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "llm.call requires 'prompt'");

        var request = new LLMRequest
        {
            Provider = input["provider"]?.GetValue<string>(),
            Model = model,
            Prompt = prompt,
        };

        // ── Telemetry: record request attributes ──
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", request.Provider ?? "openai");
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

        // Structured output
        var structuredOutput = input["structured_output"] as JsonObject;
        if (structuredOutput != null)
        {
            request.StructuredOutputSchema = structuredOutput["schema_inline"] ?? structuredOutput["schema_ref"];
            if (structuredOutput.TryGetPropertyValue("strict", out var s) && s != null)
                request.StructuredOutputStrict = s.GetValue<bool>();
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
            if (response.Usage is JsonObject usage)
            {
                if (usage.TryGetPropertyValue("prompt_tokens", out var pt) && pt != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.input_tokens", pt.GetValue<int>());
                else if (usage.TryGetPropertyValue("input_tokens", out var it) && it != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.input_tokens", it.GetValue<int>());

                if (usage.TryGetPropertyValue("completion_tokens", out var ct2) && ct2 != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.output_tokens", ct2.GetValue<int>());
                else if (usage.TryGetPropertyValue("output_tokens", out var ot) && ot != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.output_tokens", ot.GetValue<int>());

                if (usage.TryGetPropertyValue("total_tokens", out var tt) && tt != null)
                    ctx.SetTelemetryAttribute("gen_ai.usage.total_tokens", tt.GetValue<int>());
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

            if (response.Json != null)
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
}
