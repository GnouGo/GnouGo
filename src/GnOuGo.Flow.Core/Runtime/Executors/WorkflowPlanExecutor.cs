using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Generates a workflow dynamically via LLM under policy constraints.
/// Builds a comprehensive prompt from DslReference (common) + each executor's DslSnippet (step-specific).
/// </summary>
public sealed class WorkflowPlanExecutor : IStepExecutor
{
    public string StepType => "workflow.plan";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "workflow.plan input, generator, policy or validation sections are malformed or missing required fields."),
        new(ErrorCodes.TemplatePlan, false, "The planning LLM is unavailable or the generated workflow could not be made valid after the configured reprompts."),
        new(ErrorCodes.TemplatePolicy, false, "The generated workflow violates allowed step types, denied step types, or max step limits.")
    };

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "No LLM client configured");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan input must be object");

        var generator = input["generator"] as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires 'generator'");

        var policy = input["policy"] as JsonObject;
        var limits = input["limits"] as JsonObject;
        var validate = input["validate"] as JsonObject;
        var onInvalid = input["on_invalid"] as JsonObject;

        var requestedModel = generator["model"]?.GetValue<string>();
        var requestedProvider = generator["provider"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        model ??= "gpt-4";
        var instruction = generator["instruction"]?.GetValue<string>() ?? "";
        var generatorContext = generator["context"]?.GetValue<string>() ?? "";

        // Reasoning effort: workflow planning is heavy reasoning, default to "high" (max).
        // Authors can override via `generator.reasoning: auto|minimal|low|medium|high|max`.
        var planReasoning = generator["reasoning"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(planReasoning))
            planReasoning = "high";

        // Determine allowed step types for filtering DSL snippets
        HashSet<string>? allowedTypes = null;
        var constraintsSb = new StringBuilder();
        if (policy != null)
        {
            var allowed = policy["allowed_step_types"] as JsonArray;
            if (allowed != null)
            {
                allowedTypes = allowed.Select(a => a?.GetValue<string>() ?? "").ToHashSet();
                constraintsSb.AppendLine($"Allowed step types: {string.Join(", ", allowedTypes)}");
            }
            var denied = policy["denied_step_types"] as JsonArray;
            if (denied != null)
                constraintsSb.AppendLine($"Denied step types: {string.Join(", ", denied.Select(a => a?.GetValue<string>()))}");
            var allowRemote = policy["allow_remote_workflow_refs"]?.GetValue<bool>() ?? false;
            if (!allowRemote)
                constraintsSb.AppendLine("Remote workflow references (kind: url) are NOT allowed.");
        }
        if (limits != null)
        {
            var maxSteps = limits["max_steps_total"]?.GetValue<int>();
            if (maxSteps.HasValue)
                constraintsSb.AppendLine($"Maximum total steps: {maxSteps.Value}");
        }

        // Collect DSL snippets from all registered executors (filtered by policy)
        var snippets = ctx.Engine.Registry.GetDslSnippets(allowedTypes);
        var stepTypesDoc = string.Join("\n", snippets);
        var stepExceptionsDoc = BuildStepExceptionsDoc(ctx.Engine.Registry, allowedTypes);

        // ── MCP discovery + optional pre-filter ─────────────────────────
        var discovered = await DiscoverMcpServersAsync(ctx.Engine.McpClientFactory, ctx.Engine.McpCache, ctx.Engine.Logger, ctx, ct);

        if (discovered != null && discovered.Count > 0)
        {
            // Check if pre-filtering is requested via generator.prefilter (default: true)
            var prefilterNode = generator["prefilter"];
            bool shouldPrefilter = prefilterNode == null
                || prefilterNode is JsonObject
                || (prefilterNode is JsonValue jv && (!jv.TryGetValue<bool>(out var bv) || bv));

            if (shouldPrefilter)
            {
                var prefilterModel = model;
                var prefilterProvider = provider;
                if (prefilterNode is JsonObject pfObj)
                {
                    prefilterModel = pfObj["model"]?.GetValue<string>() ?? model;
                    prefilterProvider = pfObj["provider"]?.GetValue<string>() ?? provider;
                }

                discovered = await PrefilterMcpServersAsync(
                    llmClient, discovered, instruction, generatorContext,
                    prefilterModel, prefilterProvider, planReasoning, ctx, ct);
            }
        }

        var mcpServersDoc = discovered != null && discovered.Count > 0
            ? FormatMcpServersDoc(discovered)
            : null;

        // Build the base prompt with full DSL reference
        var basePrompt = new StringBuilder();
        basePrompt.AppendLine("You are a GnOuGo.Flow YAML workflow generator. Return ONLY valid YAML, no explanation or markdown fences.");
        basePrompt.AppendLine();
        basePrompt.AppendLine("[DSL REFERENCE]");
        basePrompt.AppendLine(DslReference.CommonReference);
        basePrompt.AppendLine();
        basePrompt.AppendLine("[AVAILABLE STEP TYPES]");
        basePrompt.AppendLine(stepTypesDoc);
        basePrompt.AppendLine();
        basePrompt.AppendLine("[REQUIRED ROOT YAML SHAPE]");
        basePrompt.AppendLine("The generated YAML MUST include all required root keys exactly once: version, name, workflows.");
        basePrompt.AppendLine("Root key requirements:");
        basePrompt.AppendLine("- version: non-empty string");
        basePrompt.AppendLine("- name: non-empty string");
        basePrompt.AppendLine("- workflows: non-empty object");
        basePrompt.AppendLine("Each workflow entry under workflows MUST define a steps array.");
        basePrompt.AppendLine("If any required key is missing or has the wrong shape, the output is invalid.");
        basePrompt.AppendLine("Minimal valid skeleton:");
        basePrompt.AppendLine("version: \"1.0\"");
        basePrompt.AppendLine("name: \"generated-workflow\"");
        basePrompt.AppendLine("workflows:");
        basePrompt.AppendLine("  main:");
        basePrompt.AppendLine("    steps: []");

        if (mcpServersDoc != null)
        {
            basePrompt.AppendLine();
            basePrompt.AppendLine("[AVAILABLE MCP SERVERS]");
            basePrompt.AppendLine(mcpServersDoc);

            basePrompt.AppendLine();
            basePrompt.AppendLine("[MCP OUTPUT ACCESS]");
            basePrompt.AppendLine("mcp.call single-tool output shape: `{ status: \"ok\"|\"error\", response: <tool-specific JSON> }`");
            basePrompt.AppendLine("Access status via `data.steps.<id>.status` and the full tool result via `data.steps.<id>.response`.");
            basePrompt.AppendLine("The `response` value is opaque, tool-specific JSON. Do NOT assume field names inside `response` unless the tool description explicitly documents them.");
            basePrompt.AppendLine("When passing the tool result to a subsequent step, prefer `data.steps.<id>.response` (the whole object) or `json(data.steps.<id>.response)` to serialize it.");
            basePrompt.AppendLine("For batch/auto-discover output: `{ status, results: [{ method, status, response }] }` — access via `data.steps.<id>.results`.");
            basePrompt.AppendLine("For LLM-assisted output: `{ status, selection_mode: \"llm\", text, tool_calls, results, json? }` — structured content is in `data.steps.<id>.json` when `structured_output` is used, or `data.steps.<id>.text` for free-form text.");
        }

        basePrompt.AppendLine();
        basePrompt.AppendLine("[ERROR HANDLING AND RETRIES]");
        basePrompt.AppendLine("Use `retry` only for transient errors that are explicitly marked retryable by the runtime.");
        basePrompt.AppendLine("Retries run before `on_error` is evaluated.");
        basePrompt.AppendLine("`on_error` is evaluated only after retries are exhausted, or immediately for non-retryable errors.");
        basePrompt.AppendLine("In the current runtime, `on_error` actions are `continue` or `stop`.");
        basePrompt.AppendLine("Inside `on_error.cases[].if`, the error context exposes `error.code`, `error.message`, `error.retryable`, `step.id`, and `step.type`.");
        basePrompt.AppendLine("Prefer `retry` for timeout/network/connectivity failures that may succeed later. Prefer `action: stop` for validation, policy, schema, or syntax problems that will not improve on retry.");
        basePrompt.AppendLine();
        basePrompt.AppendLine("Retry + fallback example for a transient LLM error:");
        basePrompt.AppendLine("```yaml");
        basePrompt.AppendLine("- id: summarize");
        basePrompt.AppendLine("  type: llm.call");
        basePrompt.AppendLine("  input:");
        basePrompt.AppendLine("    model: gpt-4o-mini");
        basePrompt.AppendLine("    prompt: \"Summarize: ${json(data.inputs)}\"");
        basePrompt.AppendLine("  retry:");
        basePrompt.AppendLine("    max: 3");
        basePrompt.AppendLine("    backoff_ms: 1000");
        basePrompt.AppendLine("    backoff_mult: 2");
        basePrompt.AppendLine("    jitter_ms: 100");
        basePrompt.AppendLine("  on_error:");
        basePrompt.AppendLine("    cases:");
        basePrompt.AppendLine("      - if: \"${error.code == \\\"LLM_TIMEOUT\\\" || error.code == \\\"LLM_NETWORK\\\"}\"");
        basePrompt.AppendLine("        action: continue");
        basePrompt.AppendLine("        set_output:");
        basePrompt.AppendLine("          text: \"Temporary LLM issue after retries\"");
        basePrompt.AppendLine("      - action: stop");
        basePrompt.AppendLine("```");
        basePrompt.AppendLine();
        basePrompt.AppendLine("Non-retryable validation example:");
        basePrompt.AppendLine("```yaml");
        basePrompt.AppendLine("on_error:");
        basePrompt.AppendLine("  cases:");
        basePrompt.AppendLine("    - if: \"${error.code == \\\"INPUT_VALIDATION\\\"}\"");
        basePrompt.AppendLine("      action: stop");
        basePrompt.AppendLine("    - if: \"${error.retryable}\"");
        basePrompt.AppendLine("      action: continue");
        basePrompt.AppendLine("      set_output:");
        basePrompt.AppendLine("        status: \"degraded\"");
        basePrompt.AppendLine("    - action: stop");
        basePrompt.AppendLine("```");
        basePrompt.AppendLine();
        basePrompt.AppendLine("[STEP EXCEPTIONS BY TYPE]");
        basePrompt.AppendLine(stepExceptionsDoc);

        if (constraintsSb.Length > 0)
        {
            basePrompt.AppendLine();
            basePrompt.AppendLine("[CONSTRAINTS]");
            basePrompt.Append(constraintsSb);
        }

        basePrompt.AppendLine();
        basePrompt.AppendLine("[TASK]");
        basePrompt.AppendLine($"Instruction: {instruction}");
        if (!string.IsNullOrWhiteSpace(generatorContext))
            basePrompt.AppendLine($"Context: {generatorContext}");

        var maxAttempts = onInvalid?["max_attempts"]?.GetValue<int>() ?? 3;
        var failAction = onInvalid?["action"]?.GetValue<string>() ?? "fail";
        string? lastError = null;

        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "openai");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);

        // ── Thinking: signal planning start ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Planning workflow with {model}…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var prompt = new StringBuilder(basePrompt.ToString());

            // On retry, inject the previous error so the LLM can self-correct
            if (lastError != null)
            {
                // ── Thinking: signal retry ──
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Plan attempt {attempt + 1}/{maxAttempts} — fixing: {(lastError.Length > 100 ? lastError[..100] + "…" : lastError)}"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                prompt.AppendLine();
                prompt.AppendLine("[PREVIOUS ERROR]");
                prompt.AppendLine(lastError);
                prompt.AppendLine("Fix the issues above and generate a corrected YAML.");
            }

            var promptText = prompt.ToString();
            if (ctx.Limits.LogStepContent)
            {
                ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", promptText),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
                });
            }

            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = promptText,
                Reasoning = planReasoning,
            }, ct);

            ctx.SetTelemetryAttribute("gen_ai.response.model", model);
            ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", "stop");
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

            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
                });
            }

            try
            {
                // Strip markdown fences if the LLM wrapped the YAML
                var yaml = StripMarkdownFences(response.Text);

                // Parse + validate minimal required shape before policy/limits/compile checks.
                var generatedDoc = ParseAndValidateGeneratedWorkflow(yaml);

                // Policy enforcement
                if (policy != null)
                    EnforcePolicy(generatedDoc, policy);

                // Limits enforcement
                if (limits != null)
                    EnforceLimits(generatedDoc, limits);

                // Compile to validate
                if (validate?["compile"]?.GetValue<bool>() ?? true)
                {
                    var compiler = new Compilation.WorkflowCompiler();
                    compiler.Compile(generatedDoc);
                }

                // Return the generated workflow as JSON
                var workflowInfo = new JsonObject
                {
                    ["version"] = generatedDoc.Version,
                    ["name"] = generatedDoc.Name
                };
                var wfNames = new JsonArray();
                foreach (var wfName in generatedDoc.Workflows.Keys)
                    wfNames.Add((JsonNode)JsonValue.Create(wfName)!);
                workflowInfo["workflows"] = wfNames;

                return new JsonObject
                {
                    ["workflow"] = workflowInfo,
                    ["yaml"] = yaml,
                    ["meta"] = new JsonObject { ["model"] = model, ["attempt"] = attempt + 1 },
                    ["diagnostics"] = new JsonArray()
                };
            }
            catch (Exception ex) when (attempt < maxAttempts - 1 && failAction == "reprompt")
            {
                // Capture the error for injection into the next prompt
                ctx.Engine.Logger.LogWarning(ex, "workflow.plan: attempt {Attempt}/{MaxAttempts} failed, reprompting", attempt + 1, maxAttempts);
                lastError = BuildStructuredPlanError(ex, attempt + 1);
            }
        }

        ctx.Engine.Logger.LogError("workflow.plan: failed to generate valid workflow after {MaxAttempts} attempts", maxAttempts);
        throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan,
            $"Failed to generate valid workflow after {maxAttempts} attempts");
    }

    // ── MCP discovery data ──────────────────────────────────────────────

    /// <summary>
    /// Holds the result of discovering tools and prompts from one MCP server.
    /// </summary>
    private sealed class McpServerDiscovery
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();
        public IReadOnlyList<McpPromptInfo> Prompts { get; init; } = Array.Empty<McpPromptInfo>();
        /// <summary>True when the server was reachable and listing succeeded.</summary>
        public bool Discovered { get; init; }
    }

    /// <summary>
    /// Connects to each configured MCP server and lists its tools/prompts.
    /// Returns null when no servers are configured.
    /// </summary>
    private static async Task<List<McpServerDiscovery>?> DiscoverMcpServersAsync(
        IMcpClientFactory? factory, Microsoft.Extensions.Caching.Memory.IMemoryCache? cache, ILogger logger, StepExecutionContext ctx, CancellationToken ct)
    {
        if (factory?.ServerMetadata == null || factory.ServerMetadata.Count == 0)
            return null;

        var serverCount = factory.ServerMetadata.Count;

        // ── Thinking: MCP discovery start ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Discovering {serverCount} MCP server(s)…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var results = new List<McpServerDiscovery>();

        foreach (var server in factory.ServerMetadata)
        {
            // ── Try cache first: skip session entirely when both tools & prompts are cached ──
            var cachedTools = McpCacheHelper.GetCachedTools(cache, server.Name);
            var cachedPrompts = McpCacheHelper.GetCachedPrompts(cache, server.Name);

            if (cachedTools != null && cachedPrompts != null)
            {
                logger.LogDebug("workflow.plan: serving MCP server '{ServerName}' discovery from cache", server.Name);
                results.Add(new McpServerDiscovery
                {
                    Name = server.Name,
                    Description = server.Description,
                    Tools = cachedTools,
                    Prompts = cachedPrompts,
                    Discovered = true
                });

                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': {cachedTools.Count} tool(s), {cachedPrompts.Count} prompt(s) (cached)"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                continue;
            }

            try
            {
                using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                discoveryCts.CancelAfter(TimeSpan.FromSeconds(10));

                await using var session = await factory.GetClientAsync(server.Name, discoveryCts.Token);

                var tools = cachedTools ?? await session.ListToolsAsync(discoveryCts.Token);
                McpCacheHelper.CacheTools(cache, server.Name, tools);

                IReadOnlyList<McpPromptInfo> prompts;
                if (cachedPrompts != null)
                {
                    prompts = cachedPrompts;
                }
                else
                {
                    try { prompts = await session.ListPromptsAsync(discoveryCts.Token); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "workflow.plan: prompts/list not supported on MCP server '{ServerName}'", server.Name);
                        prompts = Array.Empty<McpPromptInfo>();
                    }
                    McpCacheHelper.CachePrompts(cache, server.Name, prompts);
                }

                results.Add(new McpServerDiscovery
                {
                    Name = server.Name,
                    Description = server.Description,
                    Tools = tools,
                    Prompts = prompts,
                    Discovered = true
                });

                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': {tools.Count} tool(s), {prompts.Count} prompt(s)"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "workflow.plan: failed to discover MCP server '{ServerName}'", server.Name);
                results.Add(new McpServerDiscovery
                {
                    Name = server.Name,
                    Description = server.Description,
                    Discovered = false
                });

                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': discovery failed — {ex.Message}"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });
            }
        }

        // ── Thinking: MCP discovery summary ──
        var discoveredCount = results.Count(r => r.Discovered);
        var totalTools = results.Sum(r => r.Tools.Count);
        var totalPrompts = results.Sum(r => r.Prompts.Count);
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                $"MCP discovery complete: {discoveredCount}/{serverCount} server(s), {totalTools} tool(s), {totalPrompts} prompt(s)"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });

        return results;
    }

    /// <summary>
    /// Calls an LLM to select only the MCP servers and tools relevant to the task instruction.
    /// Returns a filtered copy of <paramref name="allServers"/>.
    /// On failure, returns the original list unchanged.
    /// </summary>
    private static async Task<List<McpServerDiscovery>> PrefilterMcpServersAsync(
        ILLMClient llmClient,
        List<McpServerDiscovery> allServers,
        string instruction,
        string context,
        string model,
        string? provider,
        string? planReasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        // Build a compact catalog for the pre-filter prompt
        var catalogSb = new StringBuilder();
        foreach (var srv in allServers)
        {
            catalogSb.Append($"server: {srv.Name}");
            if (!string.IsNullOrWhiteSpace(srv.Description))
                catalogSb.Append($" — {srv.Description}");
            catalogSb.AppendLine();

            if (!srv.Discovered)
            {
                catalogSb.AppendLine("  (discovery unavailable)");
                continue;
            }

            foreach (var t in srv.Tools)
            {
                catalogSb.Append($"  tool: {t.Name}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    catalogSb.Append($" — {t.Description}");
                catalogSb.AppendLine();
            }
            foreach (var p in srv.Prompts)
            {
                catalogSb.Append($"  prompt: {p.Name}");
                if (!string.IsNullOrWhiteSpace(p.Description))
                    catalogSb.Append($" — {p.Description}");
                catalogSb.AppendLine();
            }
        }

        var prefilterPrompt = $$"""
            You are a tool-selection assistant. Given a task description and a catalog of MCP servers with their tools/prompts, return ONLY a JSON object listing the servers and tools/prompts that are relevant for the task.

            Return format (strict JSON, no explanation):
            {
              "servers": [
                {
                  "name": "server-name",
                  "tools": ["tool1", "tool2"],
                  "prompts": ["prompt1"]
                }
              ]
            }

            Rules:
            - Include only servers that have at least one relevant tool or prompt.
            - Include only the specific tools/prompts needed for the task, not all of them.
            - If a server has "(discovery unavailable)", include it if its description seems relevant (with empty tools/prompts arrays).
            - If no server is relevant, return {"servers": []}.

            [CATALOG]
            {{catalogSb}}

            [TASK]
            Instruction: {{instruction}}
            {{(string.IsNullOrWhiteSpace(context) ? "" : $"Context: {context}")}}
            """;

        try
        {
            // ── Thinking: prefilter start ──
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"Pre-filtering MCP servers with {model} ({allServers.Count} server(s), {allServers.Sum(s => s.Tools.Count)} tool(s))…"),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
            });

            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.start", new[]
            {
                new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count),
                new KeyValuePair<string, object?>("mcp.tools_total", allServers.Sum(s => s.Tools.Count)),
                new KeyValuePair<string, object?>("gen_ai.request.model", model)
            });

            // ── GenAI: log prefilter prompt ──
            if (ctx.Limits.LogStepContent)
            {
                ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", prefilterPrompt),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "prefilter")
                });
            }

            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prefilterPrompt,
                Temperature = 0.0,
                Reasoning = planReasoning,
            }, ct);

            // ── GenAI: log prefilter completion + usage ──
            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "prefilter")
                });
            }

            if (response.Usage is JsonObject prefilterUsage)
            {
                var attrs = new List<KeyValuePair<string, object?>>
                {
                    new("gnougo-flow.plan.phase", "prefilter"),
                    new("gen_ai.request.model", model)
                };
                if (prefilterUsage.TryGetPropertyValue("prompt_tokens", out var pt) && pt != null)
                    attrs.Add(new("gen_ai.usage.input_tokens", pt.GetValue<int>()));
                else if (prefilterUsage.TryGetPropertyValue("input_tokens", out var it) && it != null)
                    attrs.Add(new("gen_ai.usage.input_tokens", it.GetValue<int>()));
                if (prefilterUsage.TryGetPropertyValue("completion_tokens", out var ct2) && ct2 != null)
                    attrs.Add(new("gen_ai.usage.output_tokens", ct2.GetValue<int>()));
                else if (prefilterUsage.TryGetPropertyValue("output_tokens", out var ot) && ot != null)
                    attrs.Add(new("gen_ai.usage.output_tokens", ot.GetValue<int>()));
                if (prefilterUsage.TryGetPropertyValue("total_tokens", out var tt) && tt != null)
                    attrs.Add(new("gen_ai.usage.total_tokens", tt.GetValue<int>()));

                ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.usage", attrs.ToArray());
            }

            var jsonText = StripMarkdownFences(response.Text).Trim();
            var filterResult = JsonNode.Parse(jsonText) as JsonObject;
            var serversArr = filterResult?["servers"] as JsonArray;

            if (serversArr == null || serversArr.Count == 0)
            {
                ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.result", new[]
                {
                    new KeyValuePair<string, object?>("mcp.servers_selected", 0),
                    new KeyValuePair<string, object?>("mcp.tools_selected", 0)
                });

                // ── Thinking: prefilter result (none selected) ──
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        "MCP pre-filter: no servers selected as relevant for this task."),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                return new List<McpServerDiscovery>();
            }

            // Build lookup: server name → set of selected tools/prompts
            var selectionMap = new Dictionary<string, (HashSet<string> tools, HashSet<string> prompts)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in serversArr)
            {
                if (entry is not JsonObject entryObj) continue;
                var name = entryObj["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var selectedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (entryObj["tools"] is JsonArray toolsArr)
                    foreach (var t in toolsArr)
                    {
                        var tn = t?.GetValue<string>();
                        if (!string.IsNullOrEmpty(tn)) selectedTools.Add(tn);
                    }

                var selectedPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (entryObj["prompts"] is JsonArray promptsArr)
                    foreach (var p in promptsArr)
                    {
                        var pn = p?.GetValue<string>();
                        if (!string.IsNullOrEmpty(pn)) selectedPrompts.Add(pn);
                    }

                selectionMap[name] = (selectedTools, selectedPrompts);
            }

            // Filter the full discovery list
            var filtered = new List<McpServerDiscovery>();
            foreach (var srv in allServers)
            {
                if (!selectionMap.TryGetValue(srv.Name, out var selection))
                    continue;

                // Filter tools and prompts to only selected ones
                var filteredTools = selection.tools.Count > 0
                    ? srv.Tools.Where(t => selection.tools.Contains(t.Name)).ToList()
                    : (IReadOnlyList<McpToolInfo>)Array.Empty<McpToolInfo>();

                var filteredPrompts = selection.prompts.Count > 0
                    ? srv.Prompts.Where(p => selection.prompts.Contains(p.Name)).ToList()
                    : (IReadOnlyList<McpPromptInfo>)Array.Empty<McpPromptInfo>();

                // If the LLM selected a server but listed no specific tools/prompts,
                // keep all tools/prompts from that server (it means "the whole server is relevant")
                if (selection.tools.Count == 0 && selection.prompts.Count == 0)
                {
                    filteredTools = srv.Tools;
                    filteredPrompts = srv.Prompts;
                }

                filtered.Add(new McpServerDiscovery
                {
                    Name = srv.Name,
                    Description = srv.Description,
                    Tools = filteredTools,
                    Prompts = filteredPrompts,
                    Discovered = srv.Discovered
                });
            }

            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.result", new[]
            {
                new KeyValuePair<string, object?>("mcp.servers_selected", filtered.Count),
                new KeyValuePair<string, object?>("mcp.tools_selected", filtered.Sum(s => s.Tools.Count))
            });

            // ── Thinking: prefilter result summary ──
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"MCP pre-filter: {filtered.Count} server(s), {filtered.Sum(s => s.Tools.Count)} tool(s) selected for planning."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });

            return filtered;
        }
        catch (Exception ex)
        {
            // On any failure, fall back to the full unfiltered list
            ctx.Engine.Logger.LogWarning(ex, "workflow.plan: MCP prefilter failed, falling back to full server list");
            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.fallback", Array.Empty<KeyValuePair<string, object?>>());

            // ── Thinking: prefilter fallback ──
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"MCP pre-filter failed ({ex.Message}), using all {allServers.Count} server(s)."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });

            return allServers;
        }
    }

    /// <summary>
    /// Formats MCP server discovery results into the prompt text for [AVAILABLE MCP SERVERS].
    /// </summary>
    private static string FormatMcpServersDoc(List<McpServerDiscovery> servers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Use the exact server name in mcp.call input.server and in mcp.list input.servers.");

        bool anyToolsDiscovered = false;

        foreach (var server in servers)
        {
            sb.Append("- ");
            sb.Append(server.Name);
            if (!string.IsNullOrWhiteSpace(server.Description))
            {
                sb.Append(": ");
                sb.Append(server.Description);
            }
            sb.AppendLine();

            if (!server.Discovered)
            {
                sb.AppendLine("  (tool discovery unavailable)");
                continue;
            }

            if (server.Tools.Count > 0)
            {
                anyToolsDiscovered = true;
                sb.AppendLine($"  Tools ({server.Tools.Count}):");
                foreach (var t in server.Tools)
                {
                    sb.Append($"    - {t.Name}");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        sb.Append($": {t.Description}");
                    sb.AppendLine();
                    if (t.InputSchema != null)
                    {
                        var schemaStr = t.InputSchema.ToJsonString();
                        if (schemaStr.Length > 500)
                            schemaStr = schemaStr[..500] + "…";
                        sb.AppendLine($"      input_schema: {schemaStr}");
                    }
                }
            }

            if (server.Prompts.Count > 0)
            {
                anyToolsDiscovered = true;
                sb.AppendLine($"  Prompts ({server.Prompts.Count}):");
                foreach (var p in server.Prompts)
                {
                    sb.Append($"    - {p.Name}");
                    if (!string.IsNullOrWhiteSpace(p.Description))
                        sb.Append($": {p.Description}");
                    if (p.Arguments is { Count: > 0 })
                    {
                        var args = string.Join(", ", p.Arguments.Select(a =>
                            a.Required ? $"{a.Name} (required)" : a.Name));
                        sb.Append($" [{args}]");
                    }
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine();
        if (anyToolsDiscovered)
        {
            sb.AppendLine("Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly with explicit `method` and `request` — no `mcp.list` step needed.");
            sb.AppendLine("Fallback pattern: if a server lists `(tool discovery unavailable)`, use `mcp.list` first to inspect capabilities, then `mcp.call`.");
        }
        else
        {
            sb.AppendLine("Required MCP planning pattern: discover candidate servers from these descriptions -> choose one server -> use mcp.list to inspect capabilities -> either (a) choose the exact tool/prompt and call mcp.call with explicit method/methods, or (b) pass tools/prompts from mcp.list into mcp.call and use prompt/model/temperature for LLM-assisted selection.");
        }
        sb.AppendLine("Choose servers from these static descriptions only; do not assume any global initialize/probing step across all servers.");
        sb.AppendLine("Do NOT generate mcp.call with only input.server as the default plan. That runtime auto-discovery mode is only for explicit call-everything scenarios on the chosen server.");
        sb.AppendLine("If the exact tool or prompt name is unknown, use mcp.list first, then either add an intermediate explicit selection step or use mcp.call with prompt + model (+ optional temperature) and pass the discovered tools/prompts.");
        sb.AppendLine("When using LLM-assisted mcp.call, put the natural-language instruction in input.prompt and pass candidate capabilities through input.tools and/or input.prompts, typically from mcp.list outputs.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Strips markdown code fences (```yaml ... ``` or ``` ... ```) from LLM output.
    /// </summary>
    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            // Remove first line (```yaml or ```)
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            // Remove trailing ```
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    private static WorkflowDocument ParseAndValidateGeneratedWorkflow(string yaml)
    {
        var generatedDoc = Parsing.WorkflowParser.Parse(yaml);

        if (generatedDoc.Workflows.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Validation failed: required root key 'workflows' must be a non-empty object.");

        return generatedDoc;
    }

    private static string BuildStructuredPlanError(Exception ex, int attempt)
    {
        var message = ex.Message.Trim();
        var lower = message.ToLowerInvariant();

        var errorCode = "VALIDATION_ERROR";
        if (lower.Contains("missing required field 'workflows'"))
            errorCode = "MISSING_ROOT_KEY_WORKFLOWS";
        else if (lower.Contains("missing required field 'version'"))
            errorCode = "MISSING_ROOT_KEY_VERSION";
        else if (lower.Contains("missing required field 'name'"))
            errorCode = "MISSING_ROOT_KEY_NAME";
        else if (lower.Contains("yaml"))
            errorCode = "YAML_PARSE_ERROR";
        else if (lower.Contains("not allowed by policy") || lower.Contains("denied by policy"))
            errorCode = "POLICY_ERROR";
        else if (lower.Contains("exceeds limit"))
            errorCode = "LIMIT_ERROR";

        return $"attempt={attempt}; code={errorCode}; message={message}";
    }

    private static void EnforcePolicy(WorkflowDocument doc, JsonObject policy)
    {
        var allowed = policy["allowed_step_types"] as JsonArray;
        var denied = policy["denied_step_types"] as JsonArray;
        var allowedSet = allowed?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();
        var deniedSet = denied?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();

        foreach (var wf in doc.Workflows.Values)
        {
            foreach (var step in wf.Steps)
            {
                if (allowedSet != null && !allowedSet.Contains(step.Type))
                    throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                        $"Step type '{step.Type}' not allowed by policy");
                if (deniedSet != null && deniedSet.Contains(step.Type))
                    throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                        $"Step type '{step.Type}' denied by policy");
            }
        }

        var allowRemote = policy["allow_remote_workflow_refs"]?.GetValue<bool>() ?? false;
        if (!allowRemote)
        {
            foreach (var wf in doc.Workflows.Values)
            {
                foreach (var step in wf.Steps)
                {
                    if (step.Type == "workflow.call" && step.Input is JsonObject inputObj)
                    {
                        var refObj = inputObj["ref"] as JsonObject;
                        if (refObj?["kind"]?.GetValue<string>() == "url")
                            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                                "Remote workflow references not allowed by policy");
                    }
                }
            }
        }
    }

    private static void EnforceLimits(WorkflowDocument doc, JsonObject limits)
    {
        var maxSteps = limits["max_steps_total"]?.GetValue<int>();
        if (maxSteps.HasValue)
        {
            var totalSteps = doc.Workflows.Values.Sum(wf => CountSteps(wf.Steps));
            if (totalSteps > maxSteps.Value)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Total steps ({totalSteps}) exceeds limit ({maxSteps.Value})");
        }
    }

    private static int CountSteps(List<StepDef> steps)
    {
        var count = steps.Count;
        foreach (var step in steps)
        {
            if (step.Steps != null) count += CountSteps(step.Steps);
            if (step.Branches != null)
                count += step.Branches.Sum(b => CountSteps(b.Steps));
            if (step.Cases != null)
                count += step.Cases.Sum(c => CountSteps(c.Steps));
            if (step.Default != null) count += CountSteps(step.Default);
        }
        return count;
    }

    private static string BuildStepExceptionsDoc(StepExecutorRegistry registry, HashSet<string>? allowedTypes)
    {
        var catalogs = registry.GetStepExceptionCatalogs(allowedTypes)
            .OrderBy(c => c.StepType, StringComparer.Ordinal)
            .ToList();

        if (catalogs.Count == 0)
            return "No task-specific exception catalog is available.";

        var sb = new StringBuilder();
        sb.AppendLine("Common notes:");
        sb.AppendLine("- `INPUT_VALIDATION` usually means a required field is missing or has the wrong shape. It is usually non-retryable.");
        sb.AppendLine("- Only codes marked `retryable` should normally use `retry`.");

        var containerTypes = new[]
        {
            "sequence",
            "parallel",
            "loop.sequential",
            "loop.parallel",
            "switch",
            "workflow.call",
            "workflow.execute"
        };
        var visibleContainerTypes = containerTypes
            .Where(t => allowedTypes == null || allowedTypes.Contains(t))
            .ToList();
        if (visibleContainerTypes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Container child-error propagation:");
            sb.AppendLine("- These container steps can raise both their own documented errors and errors propagated from nested child steps.");
            foreach (var containerType in visibleContainerTypes)
            {
                var propagationNote = containerType switch
                {
                    "sequence" => "runs child steps sequentially, so any unhandled child failure can stop the container.",
                    "parallel" => "can fail because one branch throws an unhandled child error, in addition to its own parallel-limit checks.",
                    "loop.sequential" => "can fail because one iteration throws an unhandled child error, in addition to loop-limit checks.",
                    "loop.parallel" => "can fail because one parallel iteration throws an unhandled child error, in addition to loop-limit checks.",
                    "switch" => "can fail because the selected case/default branch throws an unhandled child error.",
                    "workflow.call" => "can fail because the called sub-workflow throws an error, in addition to workflow reference/fetch/policy errors.",
                    "workflow.execute" => "can fail because the generated workflow throws an error, in addition to planned-YAML/entrypoint validation errors.",
                    _ => "can propagate child-step errors."
                };
                sb.AppendLine($"- `{containerType}`: {propagationNote}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Step-specific exceptions:");
        foreach (var catalog in catalogs)
        {
            sb.AppendLine();
            sb.AppendLine($"- {catalog.StepType}");
            foreach (var exception in catalog.Exceptions
                         .OrderBy(e => e.Code, StringComparer.Ordinal)
                         .ThenBy(e => e.Retryable))
            {
                sb.Append("  - ");
                sb.Append(exception.Code);
                sb.Append(exception.Retryable ? " (retryable)" : " (non-retryable)");
                sb.Append(": ");
                sb.AppendLine(exception.Description);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
