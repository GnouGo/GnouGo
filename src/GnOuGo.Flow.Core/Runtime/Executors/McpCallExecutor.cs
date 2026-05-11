using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Calls one or more MCP server tools or prompts.
///
/// Input (single call):
///   - server (string, required)  : MCP server name
///   - method (string)            : single tool/prompt name
///   - kind (string, optional)    : "tool" (default) or "prompt"
///   - request (object, optional) : arguments to pass
///   - request_template (string, optional) : Mustache template for arguments
///   - timeout_ms (number, optional)
///
/// Input (batch call):
///   - server (string, required)  : MCP server name
///   - methods (array of string)  : multiple tool/prompt names
///   - kind (string, optional)    : "tool" (default) or "prompt"
///   - request (object, optional) : shared arguments for every call
///   - timeout_ms (number, optional)
///
/// Input (auto-discover — neither method nor methods):
///   - server (string, required)  : MCP server name
///   - kind (string, optional)    : "tool" (default) or "prompt"
///   - request (object, optional) : shared arguments for every discovered tool/prompt
///   - timeout_ms (number, optional)
///   → Automatically lists all tools (or prompts) from the server and calls each one.
///
/// Output (single — method):
///   kind=tool:   { status, response }
///   kind=prompt: { status, description, messages, text }
///
/// Output (batch/auto — methods or auto-discover):
///   { status: "ok"|"error", results: [ { method, status, response|description|messages|text } ] }
/// </summary>
public sealed class McpCallExecutor : IStepExecutor
{
    public string StepType => "mcp.call";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object is malformed, `server` is missing, method selection is invalid, or prompt-mode `model`/`prompt` fields are missing."),
        new(ErrorCodes.TemplateSyntax, false, "`request_template` rendering failed before the MCP call was sent."),
        new(ErrorCodes.JsonParse, false, "`request_template` rendered invalid JSON arguments."),
        new(ErrorCodes.McpConnectionError, false, "No MCP client factory is configured for this runtime."),
        new(ErrorCodes.McpTimeout, true, "The MCP call timed out. This is retryable."),
        new(ErrorCodes.McpCallError, false, "A tool call failed, or LLM-assisted selection chose no valid MCP tool."),
        new(ErrorCodes.McpPromptError, false, "A prompt call failed, or LLM-assisted selection chose no valid MCP prompt."),
        new(ErrorCodes.LlmNetwork, false, "LLM-assisted MCP selection was requested but no LLM client is configured.")
    };

    public string DslSnippet => """
        ### mcp.call — Call MCP tool(s) or prompt(s)
        Use a configured MCP server name for `input.server`. When workflow.plan provides an `[AVAILABLE MCP SERVERS]` section, pick one of those exact names.

        Direct MCP call pattern (preferred when tool names are known from `[AVAILABLE MCP SERVERS]`):
        When tool names and input schemas are listed in the planner's `[AVAILABLE MCP SERVERS]` section, use `mcp.call` directly with explicit `method` and `request` — no `mcp.list` step needed.
        Fallback: discover candidate servers -> choose one server -> use `mcp.list` -> choose the exact tool/prompt -> build `request` -> use `mcp.call` with explicit `method`/`methods`.

        LLM-assisted MCP call pattern:
        use `mcp.list` first -> pass `tools` and/or `prompts` from that step into `mcp.call` -> provide a natural-language `prompt` + `model` (+ optional `temperature`) -> let the model choose and call the right MCP capability.

        Direct mode keeps the generic `request` object contract for both tools and prompts. Even when `kind: prompt`, `request` contains the named prompt arguments expected by the MCP server; it is not a free-form text alias.
        In LLM-assisted mode, top-level `prompt`/`model`/`temperature` are used for the selection model, while the selected MCP tool/prompt still receives named arguments.
        Optional `structured_output` works like `llm.call`, but is applied after the MCP capability has been executed so the final answer can be returned as strict JSON.
        For generated plans, do NOT use `mcp.call` with only `server` as the default next step after `mcp.list` unless calling everything is the explicit goal.

        Output access patterns:
        - Single tool: `data.steps.<id>.status` ("ok"|"error") and `data.steps.<id>.response` (opaque tool-specific JSON).
        - Single prompt: `data.steps.<id>.status`, `data.steps.<id>.text`, `data.steps.<id>.messages`.
        - Batch/auto: `data.steps.<id>.results` (array of `{ method, status, response|text }`).
        - LLM-assisted: `data.steps.<id>.text`, `data.steps.<id>.json` (when structured_output is used).
        - `response` is tool-specific. Do NOT guess field names inside `response` unless documented by the tool.
        - To pass the entire tool result to a subsequent step, use `data.steps.<id>.response` or `json(data.steps.<id>.response)`.

        Single tool call:
        ```yaml
        - id: weather
          type: mcp.call
          input:
            server: my-mcp-server
            kind: tool
            method: get_weather
            request: { location: "Paris" }
            timeout_ms: 30000
        ```

        Single prompt call:
        ```yaml
        - id: summarize_prompt
          type: mcp.call
          input:
            server: my-mcp-server
            kind: prompt
            method: summarize_document
            request: { text: "Long document here" }
        ```

        LLM-assisted call using `mcp.list` output:
        ```yaml
        - id: discover
          type: mcp.list
          input:
            server: github
            include: ["tools", "prompts"]

        - id: choose_and_call
          type: mcp.call
          input:
            server: github
            model: gpt-4o-mini
            temperature: 0.2
            prompt: "Find the right GitHub capability and call it to summarize my repos"
            tools: "${data.steps.discover.tools}"
            prompts: "${data.steps.discover.prompts}"
            structured_output:
              schema_inline:
                type: object
                properties:
                  summary: { type: string }
                  links:
                    type: array
                    items:
                      type: object
                      properties:
                        title: { type: string }
                        url: { type: string }
                      required: [title, url]
                required: [summary, links]
              strict: true
        ```

        Auto-discover all available tools:
        ```yaml
        - id: discover_and_call
          type: mcp.call
          input:
            server: my-server
        ```
        Output (single): `{ status, response }` (tool) or `{ status, text, messages }` (prompt)
        Output (batch/auto): `{ status, results: [{ method, status, response|text }] }`
        Output (LLM-assisted): `{ status, selection_mode: "llm", text, tool_calls: [...], results: [...], json?: {...} }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var factory = ctx.Engine.McpClientFactory
            ?? throw new WorkflowRuntimeException(ErrorCodes.McpConnectionError,
                "No IMcpClientFactory configured");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.call input must be object");

        var serverName = input["server"] != null ? ExpressionEvaluator.GetString(input["server"]) : null;
        if (string.IsNullOrEmpty(serverName))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.call requires 'server'");

        // Parse kind: "tool" (default) or "prompt"
        var kind = "tool";
        if (input.TryGetPropertyValue("kind", out var kindNode) && kindNode != null)
        {
            kind = ExpressionEvaluator.GetString(kindNode).ToLowerInvariant();
            if (kind != "tool" && kind != "prompt")
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"mcp.call 'kind' must be 'tool' or 'prompt', got '{kind}'");
        }

        bool hasPromptSelection = input.TryGetPropertyValue("prompt", out var promptNode) && promptNode != null;

        // ── Telemetry: record request attributes ──
        ctx.SetTelemetryAttribute("gen_ai.operation.name", hasPromptSelection ? "chat" : (kind == "prompt" ? "prompt_get" : "tool_call"));
        ctx.SetTelemetryAttribute("mcp.server.name", serverName);
        ctx.SetTelemetryAttribute("mcp.kind", kind);

        // ── Thinking: signal MCP call ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Calling MCP server '{serverName}' ({kind})…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        // Determine mode: single (method), batch (methods), or auto-discover (neither)
        bool hasMethod = input.ContainsKey("method") && input["method"] != null;
        bool hasMethods = input.ContainsKey("methods");
        bool isAutoDiscover = !hasMethod && !hasMethods;

        string? singleMethod = null;
        List<string>? batchMethods = null;

        if (hasMethods)
        {
            if (input["methods"] is not JsonArray methodsArr || methodsArr.Count == 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "mcp.call 'methods' must be a non-empty array of strings");

            batchMethods = new List<string>();
            foreach (var item in methodsArr)
            {
                var name = item != null ? ExpressionEvaluator.GetString(item) : null;
                if (string.IsNullOrEmpty(name))
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        "mcp.call 'methods' contains an empty or null entry");
                batchMethods.Add(name);
            }
        }
        else if (hasMethod)
        {
            singleMethod = ExpressionEvaluator.GetString(input["method"]!);
            if (string.IsNullOrEmpty(singleMethod))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "mcp.call 'method' must be a non-empty string");
            ctx.SetTelemetryAttribute("mcp.method.name", singleMethod);
        }
        // else: isAutoDiscover — resolved after session is opened

        // Build the request arguments
        JsonNode? requestArgs = hasPromptSelection ? null : BuildRequestArgs(input, ctx);

        // Parse timeout. A server-level CallTimeoutSeconds acts as a recommended minimum,
        // so slow configured servers cannot be undercut by generated workflows with short timeouts.
        var timeoutMs = ResolveEffectiveTimeoutMs(input, serverName, factory);
        if (timeoutMs.HasValue)
            ctx.SetTelemetryAttribute("mcp.timeout_ms", timeoutMs.Value);

        try
        {
            using var timeoutCts = timeoutMs.HasValue
                ? new CancellationTokenSource(timeoutMs.Value)
                : new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var correlation = BuildCorrelationContext(ctx, serverName, kind, singleMethod, batchMethods);
            ctx.SetTelemetryAttribute("gnougo.correlation_id", correlation.CorrelationId);
            if (!string.IsNullOrWhiteSpace(correlation.TraceId))
                ctx.SetTelemetryAttribute("gnougo.trace_id", correlation.TraceId);

            using var correlationScope = ConfiguredMcpClientFactory.PushCorrelationContext(correlation);
            await using var session = await factory.GetClientAsync(serverName, linkedCts.Token);

            if (hasPromptSelection)
            {
                return await ExecuteLlmAssistedAsync(session, input, kind, singleMethod, batchMethods, ctx, linkedCts.Token);
            }

            // Auto-discover: list all tools or prompts from the server
            if (isAutoDiscover)
            {
                batchMethods = new List<string>();
                var cache = ctx.Engine.McpCache;
                if (kind == "prompt")
                {
                    var prompts = McpCacheHelper.GetCachedPrompts(cache, serverName)
                        ?? await TryListPromptsAsync(session, serverName, ctx, linkedCts.Token);
                    McpCacheHelper.CachePrompts(cache, serverName, prompts);
                    foreach (var p in prompts)
                        batchMethods.Add(p.Name);
                }
                else
                {
                    var tools = McpCacheHelper.GetCachedTools(cache, serverName)
                        ?? (IReadOnlyList<McpToolInfo>) await session.ListToolsAsync(linkedCts.Token);
                    McpCacheHelper.CacheTools(cache, serverName, tools);
                    foreach (var t in tools)
                        batchMethods.Add(t.Name);
                }

                ctx.SetTelemetryAttribute("mcp.auto_discover", true);
                ctx.SetTelemetryAttribute("mcp.methods_count", batchMethods.Count);

                if (batchMethods.Count == 0)
                {
                    ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", "stop");
                    return new JsonObject
                    {
                        ["status"] = "ok",
                        ["results"] = new JsonArray()
                    };
                }
            }

            bool isBatch = batchMethods != null;

            if (isBatch)
            {
                ctx.SetTelemetryAttribute("mcp.methods_count", batchMethods!.Count);

                // ── Batch mode: call each method, collect results ──
                var resultsArr = new JsonArray();
                bool hasError = false;

                foreach (var methodName in batchMethods!)
                {
                    var itemCorrelation = correlation with { MethodName = methodName };
                    var itemResult = await CallSingleAsync(session, kind, methodName, requestArgs?.DeepClone(), itemCorrelation, ctx, linkedCts.Token);
                    var itemObj = (JsonObject)itemResult!;
                    itemObj["method"] = methodName;
                    if (itemObj["status"]?.GetValue<string>() == "error")
                        hasError = true;
                    resultsArr.Add(itemObj);
                }

                ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", hasError ? "error" : "stop");
                return new JsonObject
                {
                    ["status"] = hasError ? "error" : "ok",
                    ["results"] = resultsArr
                };
            }
            else
            {
                // ── Single mode (backward compatible) ──
                var singleCorrelation = correlation with { MethodName = singleMethod };
                var singleResult = await CallSingleAsync(session, kind, singleMethod!, requestArgs, singleCorrelation, ctx, linkedCts.Token);
                var statusStr = (singleResult as JsonObject)?["status"]?.GetValue<string>();
                ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", statusStr == "error" ? "error" : "stop");
                return singleResult;
            }
        }
        catch (WorkflowRuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var target = batchMethods != null ? string.Join(", ", batchMethods) : singleMethod ?? (hasPromptSelection ? "(llm-selection)" : "(auto)");
            throw new WorkflowRuntimeException(ErrorCodes.McpTimeout,
                $"mcp.call to '{serverName}/{target}' timed out after {timeoutMs}ms", retryable: true);
        }
        catch (Exception ex)
        {
            var errorCode = kind == "prompt" ? ErrorCodes.McpPromptError : ErrorCodes.McpCallError;
            var target = batchMethods != null ? string.Join(", ", batchMethods) : singleMethod ?? (hasPromptSelection ? "(llm-selection)" : "(auto)");
            var diagnostics = ConfiguredMcpClientFactory.FormatMcpFailureDiagnostics(serverName, ex);
            throw new WorkflowRuntimeException(errorCode,
                $"mcp.call ({kind}) to '{serverName}/{target}' failed: {diagnostics}", retryable: false, inner: ex);
        }
    }

    private static int? ResolveEffectiveTimeoutMs(JsonObject input, string serverName, IMcpClientFactory factory)
    {
        int? requestedTimeoutMs = null;
        if (input.TryGetPropertyValue("timeout_ms", out var timeoutNode) && timeoutNode != null)
            requestedTimeoutMs = (int)ExpressionEvaluator.GetNumber(timeoutNode);

        var configuredTimeoutMs = ResolveConfiguredCallTimeoutMs(factory, serverName);
        if (requestedTimeoutMs.HasValue && configuredTimeoutMs.HasValue)
            return Math.Max(requestedTimeoutMs.Value, configuredTimeoutMs.Value);

        return requestedTimeoutMs ?? configuredTimeoutMs;
    }

    private static int? ResolveConfiguredCallTimeoutMs(IMcpClientFactory factory, string serverName)
    {
        var metadata = factory.ServerMetadata?
            .FirstOrDefault(server => string.Equals(server.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (metadata?.CallTimeoutSeconds is not > 0)
            return null;

        return metadata.CallTimeoutSeconds.Value > int.MaxValue / 1000
            ? int.MaxValue
            : metadata.CallTimeoutSeconds.Value * 1000;
    }

    private static async Task<JsonNode?> ExecuteLlmAssistedAsync(
        IMcpSession session,
        JsonObject input,
        string defaultKind,
        string? singleMethod,
        List<string>? batchMethods,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.LlmNetwork,
                "mcp.call prompt mode requires an LLM client");

        var requestedProvider = input["provider"]?.GetValue<string>();
        var requestedModel = input["model"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "mcp.call prompt mode requires 'model' unless WorkflowEngine.LlmDefaults.Model is configured");
        }
        var prompt = input["prompt"] != null ? ExpressionEvaluator.GetString(input["prompt"]!) : null;
        if (string.IsNullOrWhiteSpace(prompt))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.call prompt mode requires a non-empty 'prompt'");

        double? temperature = null;
        if (input.TryGetPropertyValue("temperature", out var tempNode) && tempNode != null)
            temperature = ExpressionEvaluator.GetNumber(tempNode);

        var (structuredOutputSchema, structuredOutputStrict) = GetStructuredOutputConfig(input);

        var capabilities = await ResolveSelectableCapabilitiesAsync(session, session.ServerName, input, defaultKind, singleMethod, batchMethods, ctx, ct);
        if (capabilities.Count == 0)
        {
            ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "default");
            ctx.SetTelemetryAttribute("gen_ai.request.model", model);
            ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", "stop");
            return new JsonObject
            {
                ["status"] = "ok",
                ["selection_mode"] = "llm",
                ["tool_calls"] = new JsonArray(),
                ["results"] = new JsonArray(),
                ["text"] = "No MCP capabilities available for selection."
            };
        }

        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "default");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);
        if (temperature.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.request.temperature", temperature.Value);
        ctx.SetTelemetryAttribute("mcp.capabilities_count", capabilities.Count);

        if (ctx.Limits.LogStepContent)
        {
            ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", BuildLlmSelectionPrompt(prompt)),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.mcp.phase", "selection")
            });
        }

        var llmResponse = await llmClient.CallAsync(new LLMRequest
        {
            Provider = provider,
            Model = model,
            Prompt = BuildLlmSelectionPrompt(prompt),
            Temperature = temperature,
            Tools = BuildToolsList(capabilities, input)
        }, ct);

        var finishReason = llmResponse.ToolCalls is { Count: > 0 } ? "tool_calls" : "stop";
        ctx.SetTelemetryAttribute("gen_ai.response.model", model);
        ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", finishReason);
        ExtractUsageTelemetry(ctx, llmResponse.Usage as JsonObject, model, provider);

        if (ctx.Limits.LogStepContent && (!string.IsNullOrWhiteSpace(llmResponse.Text) || llmResponse.Json != null))
        {
            ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.completion", !string.IsNullOrWhiteSpace(llmResponse.Text) ? llmResponse.Text : llmResponse.Json?.ToJsonString()),
                new KeyValuePair<string, object?>("completion.role", "assistant"),
                new KeyValuePair<string, object?>("completion.finish_reason", finishReason),
                new KeyValuePair<string, object?>("gnougo-flow.mcp.phase", "selection")
            });
        }

        if (llmResponse.ToolCalls is not { Count: > 0 })
            throw new WorkflowRuntimeException(defaultKind == "prompt" ? ErrorCodes.McpPromptError : ErrorCodes.McpCallError,
                "mcp.call prompt mode did not select any MCP tool or prompt", retryable: false);

        var capabilityMap = capabilities.ToDictionary(c => c.InternalName, StringComparer.Ordinal);
        var toolCallsArr = new JsonArray();
        var resultsArr = new JsonArray();
        bool hasError = false;

        foreach (var toolCall in llmResponse.ToolCalls)
        {

            if (!capabilityMap.TryGetValue(toolCall.Name, out var capability))
                throw new WorkflowRuntimeException(defaultKind == "prompt" ? ErrorCodes.McpPromptError : ErrorCodes.McpCallError,
                    $"mcp.call prompt mode selected unknown MCP capability '{toolCall.Name}'", retryable: false);

            var correlation = BuildCorrelationContext(ctx, session.ServerName, capability.Kind, capability.MethodName, null);
            var itemResult = await CallSingleAsync(session, capability.Kind, capability.MethodName, toolCall.Arguments?.DeepClone(), correlation, ctx, ct);
            var itemObj = (JsonObject)itemResult!;
            itemObj["method"] = capability.MethodName;
            itemObj["kind"] = capability.Kind;
            if (!string.IsNullOrWhiteSpace(toolCall.Id))
                itemObj["call_id"] = toolCall.Id;
            if (itemObj["status"]?.GetValue<string>() == "error")
                hasError = true;
            resultsArr.Add(itemObj);

            var callObj = new JsonObject
            {
                ["name"] = capability.MethodName,
                ["kind"] = capability.Kind,
                ["arguments"] = toolCall.Arguments?.DeepClone()
            };
            if (!string.IsNullOrWhiteSpace(toolCall.Id))
                callObj["id"] = toolCall.Id;
            toolCallsArr.Add(callObj);
        }

        if (llmResponse.ToolCalls.Count == 1)
        {
            ctx.SetTelemetryAttribute("mcp.method.name", capabilityMap[llmResponse.ToolCalls[0].Name].MethodName);
            ctx.SetTelemetryAttribute("mcp.kind", capabilityMap[llmResponse.ToolCalls[0].Name].Kind);
        }
        ctx.SetTelemetryAttribute("mcp.methods_count", llmResponse.ToolCalls.Count);

        var response = new JsonObject
        {
            ["status"] = hasError ? "error" : "ok",
            ["selection_mode"] = "llm",
            ["text"] = llmResponse.Text,
            ["tool_calls"] = toolCallsArr,
            ["results"] = resultsArr
        };

        if (structuredOutputSchema != null)
        {
            var structuredResponse = await RunStructuredPostProcessAsync(
                llmClient,
                provider,
                model,
                temperature,
                prompt,
                toolCallsArr,
                resultsArr,
                structuredOutputSchema,
                structuredOutputStrict,
                ctx,
                ct);

            var structuredJson = structuredResponse.Json;
            if (structuredJson == null && !string.IsNullOrWhiteSpace(structuredResponse.Text))
            {
                try { structuredJson = JsonNode.Parse(structuredResponse.Text); }
                catch (JsonException ex)
                {
                    ctx.Engine.Logger.LogDebug(ex, "mcp.call structured post-process response was not valid JSON for model '{Model}'.", model);
                }
            }

            if (structuredJson == null)
                throw new WorkflowRuntimeException(ErrorCodes.LlmSchema,
                    "mcp.call structured_output expected valid JSON but the LLM returned an incompatible response", retryable: false);

            response["selection_text"] = llmResponse.Text;
            response["json"] = structuredJson.DeepClone();
            if (!string.IsNullOrWhiteSpace(structuredResponse.Text))
                response["text"] = structuredResponse.Text;
        }

        return response;
    }

    private static async Task<LLMResponse> RunStructuredPostProcessAsync(
        ILLMClient llmClient,
        string? provider,
        string model,
        double? temperature,
        string originalPrompt,
        JsonArray toolCalls,
        JsonArray results,
        JsonNode structuredOutputSchema,
        bool? structuredOutputStrict,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = BuildStructuredPostProcessPrompt(originalPrompt, toolCalls, results);

        if (ctx.Limits.LogStepContent)
        {
            ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.mcp.phase", "finalize")
            });
        }

        var response = await llmClient.CallAsync(new LLMRequest
        {
            Provider = provider,
            Model = model,
            Prompt = prompt,
            Temperature = temperature,
            StructuredOutputSchema = structuredOutputSchema,
            StructuredOutputStrict = structuredOutputStrict
        }, ct);

        ExtractUsageTelemetry(ctx, response.Usage as JsonObject, model, provider);

        if (ctx.Limits.LogStepContent && (!string.IsNullOrWhiteSpace(response.Text) || response.Json != null))
        {
            ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.completion", !string.IsNullOrWhiteSpace(response.Text) ? response.Text : response.Json?.ToJsonString()),
                new KeyValuePair<string, object?>("completion.role", "assistant"),
                new KeyValuePair<string, object?>("completion.finish_reason", response.Json != null ? "stop" : "error"),
                new KeyValuePair<string, object?>("gnougo-flow.mcp.phase", "finalize")
            });
        }

        return response;
    }

    private static string BuildLlmSelectionPrompt(string userPrompt) =>
        $"""
You are selecting and parameterizing MCP capabilities for the user's request.

Important rules:
- Preserve every explicit user constraint in the tool-call arguments.
- Choose the smallest set of MCP calls needed to satisfy the request.
- When a tool already exposes a parameter that matches the user's request, set that parameter explicitly instead of relying on a default value.

User request:
{userPrompt}
""";

    private static string BuildStructuredPostProcessPrompt(string originalPrompt, JsonArray toolCalls, JsonArray results) =>
        $"""
You have already executed the MCP capabilities needed for the user's request.

Original user request:
{originalPrompt}

Executed MCP tool calls:
{toolCalls.ToJsonString()}

Executed MCP results:
{results.ToJsonString()}

Produce the final answer strictly from the executed MCP results.
- Do not invent facts or links that are not supported by the MCP results.
- Preserve explicit user constraints such as HTML-vs-text intent, strict JSON shape, and absolute URLs.
- Return only the final answer matching the required JSON schema.
""";

    private static (JsonNode? Schema, bool? Strict) GetStructuredOutputConfig(JsonObject input)
    {
        var structuredOutput = input["structured_output"] as JsonObject;
        if (structuredOutput == null)
            return (null, null);

        var schema = structuredOutput["schema_inline"] ?? structuredOutput["schema_ref"];
        bool? strict = null;
        if (structuredOutput.TryGetPropertyValue("strict", out var strictNode) && strictNode != null)
            strict = strictNode.GetValue<bool>();

        return (schema?.DeepClone(), strict);
    }

    private static async Task<List<McpSelectableCapability>> ResolveSelectableCapabilitiesAsync(
        IMcpSession session,
        string serverName,
        JsonObject input,
        string defaultKind,
        string? singleMethod,
        List<string>? batchMethods,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var allowed = batchMethods != null
            ? new HashSet<string>(batchMethods, StringComparer.Ordinal)
            : singleMethod != null
                ? new HashSet<string>(new[] { singleMethod }, StringComparer.Ordinal)
                : null;

        var capabilities = new List<McpSelectableCapability>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        if (input["tools"] is JsonArray toolNodes)
            AddToolCapabilitiesFromJson(toolNodes, allowed, capabilities, usedNames);
        if (input["prompts"] is JsonArray promptNodes)
            AddPromptCapabilitiesFromJson(promptNodes, allowed, capabilities, usedNames);

        if (capabilities.Count > 0)
            return capabilities;

        if (defaultKind == "prompt")
        {
            var cache = ctx.Engine.McpCache;
            var prompts = McpCacheHelper.GetCachedPrompts(cache, serverName)
                ?? await TryListPromptsAsync(session, serverName, ctx, ct);
            McpCacheHelper.CachePrompts(cache, serverName, prompts);
            foreach (var prompt in prompts)
            {
                if (allowed != null && !allowed.Contains(prompt.Name))
                    continue;
                capabilities.Add(CreatePromptCapability(prompt, usedNames));
            }
        }
        else
        {
            var cache = ctx.Engine.McpCache;
            var tools = McpCacheHelper.GetCachedTools(cache, serverName)
                ?? (IReadOnlyList<McpToolInfo>) await session.ListToolsAsync(ct);
            McpCacheHelper.CacheTools(cache, serverName, tools);
            foreach (var tool in tools)
            {
                if (allowed != null && !allowed.Contains(tool.Name))
                    continue;
                capabilities.Add(CreateToolCapability(tool, usedNames));
            }
        }

        return capabilities;
    }

    private static async Task<IReadOnlyList<McpPromptInfo>> TryListPromptsAsync(
        IMcpSession session,
        string serverName,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            return await session.ListPromptsAsync(ct);
        }
        catch (Exception ex) when (IsUnsupportedCapability(ex, "prompts/list"))
        {
            ctx.Engine.Logger.LogWarning(ex, "mcp.call: prompts/list not supported on '{ServerName}'", serverName);
            ctx.SetTelemetryAttribute("mcp.prompts_unsupported", true);
            ctx.AddTelemetryEvent("mcp.capability.unsupported", new[]
            {
                new KeyValuePair<string, object?>("mcp.server.name", serverName),
                new KeyValuePair<string, object?>("mcp.method", "prompts/list"),
                new KeyValuePair<string, object?>("mcp.reason", ex.Message)
            });
            return Array.Empty<McpPromptInfo>();
        }
    }

    private static bool IsUnsupportedCapability(Exception ex, string methodName)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains(methodName, StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("not implemented", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("method not found", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("no handler", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddToolCapabilitiesFromJson(JsonArray toolNodes, HashSet<string>? allowed, List<McpSelectableCapability> capabilities, HashSet<string> usedNames)
    {
        foreach (var node in toolNodes.OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || (allowed != null && !allowed.Contains(name)))
                continue;

            capabilities.Add(CreateToolCapability(new McpToolInfo
            {
                Name = name,
                Description = node["description"]?.GetValue<string>(),
                InputSchema = node["input_schema"]?.DeepClone() ?? node["inputSchema"]?.DeepClone()
            }, usedNames));
        }
    }

    private static void AddPromptCapabilitiesFromJson(JsonArray promptNodes, HashSet<string>? allowed, List<McpSelectableCapability> capabilities, HashSet<string> usedNames)
    {
        foreach (var node in promptNodes.OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || (allowed != null && !allowed.Contains(name)))
                continue;

            var prompt = new McpPromptInfo
            {
                Name = name,
                Description = node["description"]?.GetValue<string>()
            };

            if (node["arguments"] is JsonArray argsArray)
            {
                prompt.Arguments = new List<McpPromptArgument>();
                foreach (var argNode in argsArray.OfType<JsonObject>())
                {
                    prompt.Arguments.Add(new McpPromptArgument
                    {
                        Name = argNode["name"]?.GetValue<string>() ?? "",
                        Description = argNode["description"]?.GetValue<string>(),
                        Required = argNode["required"]?.GetValue<bool>() ?? false
                    });
                }
            }

            capabilities.Add(CreatePromptCapability(prompt, usedNames));
        }
    }

    private static McpSelectableCapability CreateToolCapability(McpToolInfo tool, HashSet<string> usedNames)
    {
        var internalName = BuildUniqueInternalName(tool.Name, "tool", usedNames);
        return new McpSelectableCapability
        {
            InternalName = internalName,
            MethodName = tool.Name,
            Kind = "tool",
            Tool = new LLMTool
            {
                Name = internalName,
                Description = tool.Description,
                InputSchema = tool.InputSchema?.DeepClone()
            }
        };
    }

    private static McpSelectableCapability CreatePromptCapability(McpPromptInfo prompt, HashSet<string> usedNames)
    {
        var internalName = BuildUniqueInternalName(prompt.Name, "prompt", usedNames);
        return new McpSelectableCapability
        {
            InternalName = internalName,
            MethodName = prompt.Name,
            Kind = "prompt",
            Tool = new LLMTool
            {
                Name = internalName,
                Description = prompt.Description,
                InputSchema = BuildPromptArgumentSchema(prompt.Arguments)
            }
        };
    }

    private static string BuildUniqueInternalName(string methodName, string kind, HashSet<string> usedNames)
    {
        if (usedNames.Add(methodName))
            return methodName;

        var prefixed = $"{kind}:{methodName}";
        if (usedNames.Add(prefixed))
            return prefixed;

        var index = 2;
        while (!usedNames.Add($"{prefixed}:{index}"))
            index++;
        return $"{prefixed}:{index}";
    }

    private static JsonNode? BuildPromptArgumentSchema(List<McpPromptArgument>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return new JsonObject { ["type"] = "object" };

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var arg in arguments)
        {
            if (string.IsNullOrWhiteSpace(arg.Name))
                continue;

            properties[arg.Name] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = arg.Description
            };
            if (arg.Required)
                required.Add(arg.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private sealed class McpSelectableCapability
    {
        public string InternalName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public string Kind { get; set; } = "tool";
        public LLMTool Tool { get; set; } = new();
    }

    /// <summary>Calls a single tool or prompt and returns the result JsonObject.</summary>
    private static async Task<JsonNode?> CallSingleAsync(
        IMcpSession session, string kind, string method, JsonNode? requestArgs,
        McpCorrelationContext correlation, StepExecutionContext ctx, CancellationToken ct)
    {
        if (kind == "prompt")
        {
            var promptResult = await session.GetPromptAsync(method, requestArgs, ct);

            // ── Telemetry: extract LLM metrics from prompt result ──
            ExtractUsageTelemetry(ctx, promptResult.Usage, promptResult.Model, provider: null);

            var messagesArr = new JsonArray();
            var textParts = new StringBuilder();
            foreach (var msg in promptResult.Messages)
            {
                messagesArr.Add(new JsonObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                });
                textParts.AppendLine($"[{msg.Role}] {msg.Content}");
            }

            return new JsonObject
            {
                ["status"] = "ok",
                ["description"] = promptResult.Description,
                ["messages"] = messagesArr,
                ["text"] = textParts.ToString().TrimEnd()
            };
        }
        else
        {
            var callResult = await session.CallToolAsync(method, requestArgs, ct);

            // ── Telemetry: extract LLM metrics from tool result ──
            ExtractUsageTelemetry(ctx, callResult.Usage, callResult.Model, provider: null);

            return new JsonObject
            {
                ["status"] = callResult.IsError ? "error" : "ok",
                ["response"] = callResult.Content?.DeepClone(),
                ["error"] = callResult.IsError ? BuildMcpErrorObject(callResult.Content, correlation) : null,
                ["correlation_id"] = correlation.CorrelationId,
                ["trace_id"] = correlation.TraceId
            };
        }
    }

    private static McpCorrelationContext BuildCorrelationContext(
        StepExecutionContext ctx,
        string serverName,
        string kind,
        string? singleMethod,
        List<string>? batchMethods)
    {
        var activity = Activity.Current;
        var method = singleMethod ?? (batchMethods is { Count: > 0 } ? string.Join(",", batchMethods) : null);
        var traceId = activity?.TraceId.ToString();
        var spanId = activity?.SpanId.ToString();
        return new McpCorrelationContext
        {
            CorrelationId = ctx.Limits.RunId ?? activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            RunId = ctx.Limits.RunId,
            TraceId = traceId,
            SpanId = spanId,
            TraceParent = activity != null ? $"00-{activity.TraceId}-{activity.SpanId}-{(activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}" : null,
            StepId = ctx.Step.Id,
            StepType = ctx.Step.Type,
            ServerName = serverName,
            MethodName = method,
            Kind = kind
        };
    }

    private static JsonObject BuildMcpErrorObject(JsonNode? content, McpCorrelationContext correlation)
    {
        return new JsonObject
        {
            ["message"] = ExtractErrorMessage(content),
            ["content"] = content?.DeepClone(),
            ["correlation_id"] = correlation.CorrelationId,
            ["run_id"] = correlation.RunId,
            ["trace_id"] = correlation.TraceId,
            ["span_id"] = correlation.SpanId,
            ["traceparent"] = correlation.TraceParent,
            ["server"] = correlation.ServerName,
            ["method"] = correlation.MethodName,
            ["kind"] = correlation.Kind
        };
    }

    private static string ExtractErrorMessage(JsonNode? content)
    {
        if (content is null)
            return "MCP tool returned an error without content.";

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return text;

        if (content is JsonObject obj)
        {
            foreach (var key in new[] { "error_message", "message", "error", "detail", "response" })
            {
                if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue nodeValue && nodeValue.TryGetValue<string>(out var message))
                    return message;
            }
        }

        return content.ToJsonString();
    }

    /// <summary>
    /// Extracts LLM usage telemetry (tokens, model) from an MCP result and writes
    /// them to the step span, following the same GenAI semantic conventions as llm.call.
    /// </summary>
    private static void ExtractUsageTelemetry(StepExecutionContext ctx, JsonObject? usage, string? model, string? provider)
    {
        if (!string.IsNullOrWhiteSpace(model) && !ctx.TelemetryAttributes.ContainsKey("gen_ai.request.model"))
            ctx.SetTelemetryAttribute("gen_ai.request.model", model);

        if (usage == null)
            return;

        var inputTokens = GetUsageValue(usage, "prompt_tokens", "input_tokens");
        var outputTokens = GetUsageValue(usage, "completion_tokens", "output_tokens");
        var totalTokens = GetUsageValue(usage, "total_tokens", null);

        if (inputTokens.HasValue)
            AddTelemetryLong(ctx, "gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue)
            AddTelemetryLong(ctx, "gen_ai.usage.output_tokens", outputTokens.Value);

        if (totalTokens.HasValue)
            AddTelemetryLong(ctx, "gen_ai.usage.total_tokens", totalTokens.Value);
        else if (inputTokens.HasValue || outputTokens.HasValue)
            ctx.SetTelemetryAttribute(
                "gen_ai.usage.total_tokens",
                (GetTelemetryLong(ctx, "gen_ai.usage.input_tokens") ?? 0) +
                (GetTelemetryLong(ctx, "gen_ai.usage.output_tokens") ?? 0));

        var effectiveModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : ctx.TelemetryAttributes.TryGetValue("gen_ai.request.model", out var currentModel) ? currentModel?.ToString() : null;
        var effectiveProvider = !string.IsNullOrWhiteSpace(provider)
            ? provider
            : ctx.TelemetryAttributes.TryGetValue("gen_ai.system", out var currentProvider) ? currentProvider?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(effectiveModel) && (inputTokens.HasValue || outputTokens.HasValue))
        {
            var estimatedCost = ModelMetadataCatalog.EstimateCost(
                effectiveModel,
                inputTokens ?? 0,
                outputTokens ?? 0,
                providerType: effectiveProvider);
            if (estimatedCost.HasValue)
                AddTelemetryDecimal(ctx, "gen_ai.usage.cost", estimatedCost.Value);
        }
    }

    private static long? GetUsageValue(JsonObject usage, string primaryKey, string? secondaryKey)
    {
        if (usage.TryGetPropertyValue(primaryKey, out var primary) && primary != null)
            return CoerceLong(primary);
        if (secondaryKey != null && usage.TryGetPropertyValue(secondaryKey, out var secondary) && secondary != null)
            return CoerceLong(secondary);
        return null;
    }

    private static void AddTelemetryLong(StepExecutionContext ctx, string key, long delta)
    {
        var current = GetTelemetryLong(ctx, key) ?? 0;
        ctx.SetTelemetryAttribute(key, current + delta);
    }

    private static long? GetTelemetryLong(StepExecutionContext ctx, string key)
    {
        if (!ctx.TelemetryAttributes.TryGetValue(key, out var value) || value == null)
            return null;

        return CoerceLong(value);
    }

    private static void AddTelemetryDecimal(StepExecutionContext ctx, string key, decimal delta)
    {
        var current = GetTelemetryDecimal(ctx, key) ?? 0m;
        ctx.SetTelemetryAttribute(key, (double)(current + delta));
    }

    private static decimal? GetTelemetryDecimal(StepExecutionContext ctx, string key)
    {
        if (!ctx.TelemetryAttributes.TryGetValue(key, out var value) || value == null)
            return null;

        return CoerceDecimal(value);
    }

    private static long? CoerceLong(object value)
    {
        if (value is JsonNode node)
        {
            if (node is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var parsedLong))
                return parsedLong;
            if (node is JsonValue jsonValueInt && jsonValueInt.TryGetValue<int>(out var parsedInt))
                return parsedInt;
            value = node.ToJsonString().Trim('"');
        }

        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => l,
            float f => (long)f,
            double d => (long)d,
            decimal m => (long)m,
            _ when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? CoerceDecimal(object value)
    {
        if (value is JsonNode node)
        {
            if (node is JsonValue jsonValue && jsonValue.TryGetValue<decimal>(out var parsedDecimal))
                return parsedDecimal;
            if (node is JsonValue jsonValueDouble && jsonValueDouble.TryGetValue<double>(out var parsedDouble))
                return (decimal)parsedDouble;
            value = node.ToJsonString().Trim('"');
        }

        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => l,
            float f => (decimal)f,
            double d => (decimal)d,
            decimal m => m,
            _ when decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Resolves request arguments from input (request or request_template).</summary>
    private static JsonNode? BuildRequestArgs(JsonObject input, StepExecutionContext ctx)
    {
        if (input.TryGetPropertyValue("request", out var reqNode) && reqNode != null)
        {
            return reqNode;
        }

        if (input.TryGetPropertyValue("request_template", out var reqTemplate) && reqTemplate != null)
        {
            var templateStr = ExpressionEvaluator.GetString(reqTemplate);
            var templateData = input["template_data"] as JsonObject ?? ctx.Data;
            string rendered;
            try
            {
                rendered = Templating.MustacheEngine.Render(templateStr, templateData);
            }
            catch (Exception ex) when (ex is not WorkflowRuntimeException)
            {
                throw new WorkflowRuntimeException(ErrorCodes.TemplateSyntax,
                    $"mcp.call request_template rendering failed: {ex.Message}");
            }
            try
            {
                return JsonNode.Parse(rendered);
            }
            catch (JsonException ex)
            {
                throw new WorkflowRuntimeException(ErrorCodes.JsonParse,
                    $"mcp.call request_template rendered invalid JSON: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the list of LLM tools from MCP capabilities.
    /// </summary>
    private static List<LLMTool> BuildToolsList(List<McpSelectableCapability> capabilities, JsonObject input)
    {
        return capabilities.Select(c => c.Tool).ToList();
    }
}

