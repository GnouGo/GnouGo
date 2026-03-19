using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Lists tools, resources, and/or prompts from an MCP server.
///
/// Input:
///   - server (string, required) : MCP server name
///   - include (array of string, optional) : which capabilities to list — "tools", "resources", "prompts"
///       Defaults to ["tools"] if omitted.
///   - timeout_ms (number, optional) : timeout in milliseconds
///
/// Output:
///   {
///     status: "ok"|"error",
///     text: "...",              // merged human-readable description
///     tools: [ ... ],           // present if "tools" requested
///     resources: [ ... ],       // present if "resources" requested
///     prompts: [ ... ]          // present if "prompts" requested
///   }
/// </summary>
public sealed class McpListExecutor : IStepExecutor
{
    public string StepType => "mcp.list";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object is malformed, `server` is missing, or `include` contains unsupported values."),
        new(ErrorCodes.McpConnectionError, false, "No MCP client factory is configured for this runtime."),
        new(ErrorCodes.McpTimeout, true, "Listing MCP capabilities timed out. This is retryable."),
        new(ErrorCodes.McpListError, false, "The MCP server failed while listing tools/resources/prompts.")
    };

    public string DslSnippet => """
        ### mcp.list — List MCP server capabilities
        Use a configured MCP server name for `input.server`. When workflow.plan provides an `[AVAILABLE MCP SERVERS]` section, pick one of those exact names.
        Required MCP planning pattern: discover candidate servers from descriptions -> choose one server -> use `mcp.list` to inspect tools/prompts/resources -> select the exact tool/prompt -> build the request arguments -> use `mcp.call`.
        Prefer `mcp.list` first when the exact tool or prompt name is not known in advance.
        The output of `mcp.list` can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts` when you want an LLM-guided MCP selection step.
        Do not go directly from `mcp.list` to `mcp.call` with only `server` unless the explicit goal is to call every available capability on that server.
        ```yaml
        - id: discover
          type: mcp.list
          input:
            server: my-mcp-server               # required — MCP server name
            include: ["tools", "prompts"]        # optional — defaults to ["tools"]
            timeout_ms: 30000                    # optional

        - id: choose_and_call
          type: mcp.call
          input:
            server: my-mcp-server
            model: gpt-4o-mini
            prompt: "Choose the right MCP capability and call it"
            tools: "${data.steps.discover.tools}"
            prompts: "${data.steps.discover.prompts}"
        ```
        Output: `{ status, text, tools: [...], resources: [...], prompts: [...] }`
        """;

    private static readonly HashSet<string> ValidIncludes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools", "resources", "prompts"
    };

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var factory = ctx.Engine.McpClientFactory
            ?? throw new WorkflowRuntimeException(ErrorCodes.McpConnectionError,
                "No IMcpClientFactory configured");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.list input must be object");

        var serverName = input["server"] != null ? ExpressionEvaluator.GetString(input["server"]) : null;
        if (string.IsNullOrEmpty(serverName))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.list requires 'server'");

        // ── Telemetry: record request attributes ──
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "tool_list");
        ctx.SetTelemetryAttribute("mcp.server.name", serverName);

        // Parse include list (default: ["tools"])
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (input.TryGetPropertyValue("include", out var includeNode) && includeNode is JsonArray includeArr)
        {
            foreach (var item in includeArr)
            {
                var val = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(val))
                {
                    if (!ValidIncludes.Contains(val))
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                            $"mcp.list invalid include value '{val}'. Valid: tools, resources, prompts");
                    includes.Add(val);
                }
            }
        }
        if (includes.Count == 0)
            includes.Add("tools");
        ctx.SetTelemetryAttribute("mcp.include", string.Join(",", includes.OrderBy(x => x)));

        // Parse timeout
        int? timeoutMs = null;
        if (input.TryGetPropertyValue("timeout_ms", out var timeoutNode) && timeoutNode != null)
            timeoutMs = (int)ExpressionEvaluator.GetNumber(timeoutNode);

        // ── Phase 1: try to serve entirely from cache ──
        var cache = ctx.Engine.McpCache;
        bool wantTools = includes.Contains("tools");
        bool wantResources = includes.Contains("resources");
        bool wantPrompts = includes.Contains("prompts");

        var cachedTools = wantTools ? McpCacheHelper.GetCachedTools(cache, serverName) : null;
        var cachedResources = wantResources ? McpCacheHelper.GetCachedResources(cache, serverName) : null;
        var cachedPrompts = wantPrompts ? McpCacheHelper.GetCachedPrompts(cache, serverName) : null;

        bool allCached =
            (!wantTools || cachedTools != null) &&
            (!wantResources || cachedResources != null) &&
            (!wantPrompts || cachedPrompts != null);

        if (allCached)
        {
            ctx.Engine.Logger.LogDebug("mcp.list: serving '{ServerName}' entirely from cache", serverName);
            return BuildResult(serverName, includes, cachedTools, cachedResources, cachedPrompts, ctx);
        }

        // ── Phase 2: cache miss — open session and fetch missing data ──
        try
        {
            using var timeoutCts = timeoutMs.HasValue
                ? new CancellationTokenSource(timeoutMs.Value)
                : new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await using var session = await factory.GetClientAsync(serverName, linkedCts.Token);

            IReadOnlyList<McpToolInfo>? tools = cachedTools;
            IReadOnlyList<McpResourceInfo>? resources = cachedResources;
            IReadOnlyList<McpPromptInfo>? prompts = cachedPrompts;

            if (wantTools && tools == null)
            {
                tools = await session.ListToolsAsync(linkedCts.Token);
                McpCacheHelper.CacheTools(cache, serverName, tools);
            }
            if (wantResources && resources == null)
            {
                resources = await TryListResourcesAsync(session, serverName, ctx, linkedCts.Token);
                McpCacheHelper.CacheResources(cache, serverName, resources);
            }
            if (wantPrompts && prompts == null)
            {
                prompts = await TryListPromptsAsync(session, serverName, ctx, linkedCts.Token);
                McpCacheHelper.CachePrompts(cache, serverName, prompts);
            }

            return BuildResult(serverName, includes, tools, resources, prompts, ctx);
        }
        catch (WorkflowRuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowRuntimeException(ErrorCodes.McpTimeout,
                $"mcp.list on '{serverName}' timed out after {timeoutMs}ms", retryable: true);
        }
        catch (Exception ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.McpListError,
                $"mcp.list on '{serverName}' failed: {ex.Message}", retryable: false);
        }
    }

    /// <summary>Builds the mcp.list result JsonObject from fetched/cached data.</summary>
    private static JsonNode BuildResult(
        string serverName,
        HashSet<string> includes,
        IReadOnlyList<McpToolInfo>? tools,
        IReadOnlyList<McpResourceInfo>? resources,
        IReadOnlyList<McpPromptInfo>? prompts,
        StepExecutionContext ctx)
    {
        var result = new JsonObject { ["status"] = "ok" };
        var sb = new StringBuilder();
        sb.AppendLine($"MCP Server: {serverName}");

        if (includes.Contains("tools") && tools != null)
        {
            ctx.SetTelemetryAttribute("mcp.tools_count", tools.Count);
            var toolsArr = new JsonArray();
            sb.AppendLine();
            sb.AppendLine($"## Tools ({tools.Count})");
            foreach (var t in tools)
            {
                var obj = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description
                };
                if (t.InputSchema != null)
                    obj["input_schema"] = t.InputSchema.DeepClone();
                toolsArr.Add(obj);
                sb.AppendLine($"- **{t.Name}**: {t.Description ?? "(no description)"}");
            }
            result["tools"] = toolsArr;
        }

        if (includes.Contains("resources") && resources != null)
        {
            ctx.SetTelemetryAttribute("mcp.resources_count", resources.Count);
            var resourcesArr = new JsonArray();
            sb.AppendLine();
            sb.AppendLine($"## Resources ({resources.Count})");
            foreach (var r in resources)
            {
                var obj = new JsonObject
                {
                    ["uri"] = r.Uri,
                    ["name"] = r.Name,
                    ["description"] = r.Description
                };
                if (r.MimeType != null)
                    obj["mime_type"] = r.MimeType;
                resourcesArr.Add(obj);
                sb.AppendLine($"- **{r.Name}** ({r.Uri}): {r.Description ?? "(no description)"}");
            }
            result["resources"] = resourcesArr;
        }

        if (includes.Contains("prompts") && prompts != null)
        {
            ctx.SetTelemetryAttribute("mcp.prompts_count", prompts.Count);
            var promptsArr = new JsonArray();
            sb.AppendLine();
            sb.AppendLine($"## Prompts ({prompts.Count})");
            foreach (var p in prompts)
            {
                var obj = new JsonObject
                {
                    ["name"] = p.Name,
                    ["description"] = p.Description
                };
                if (p.Arguments != null && p.Arguments.Count > 0)
                {
                    var argsArr = new JsonArray();
                    foreach (var a in p.Arguments)
                    {
                        argsArr.Add(new JsonObject
                        {
                            ["name"] = a.Name,
                            ["description"] = a.Description,
                            ["required"] = a.Required
                        });
                    }
                    obj["arguments"] = argsArr;
                }
                promptsArr.Add(obj);

                var argsText = p.Arguments != null && p.Arguments.Count > 0
                    ? $" (args: {string.Join(", ", p.Arguments.Select(a => a.Name))})"
                    : "";
                sb.AppendLine($"- **{p.Name}**{argsText}: {p.Description ?? "(no description)"}");
            }
            result["prompts"] = promptsArr;
        }

        result["text"] = sb.ToString().TrimEnd();
        ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", "stop");
        return result;
    }

    private static async Task<IReadOnlyList<McpResourceInfo>> TryListResourcesAsync(
        IMcpSession session,
        string serverName,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            return await session.ListResourcesAsync(ct);
        }
        catch (Exception ex) when (IsUnsupportedCapability(ex, "resources/list"))
        {
            ctx.SetTelemetryAttribute("mcp.resources_unsupported", true);
            ctx.AddTelemetryEvent("mcp.capability.unsupported", new[]
            {
                new KeyValuePair<string, object?>("mcp.server.name", serverName),
                new KeyValuePair<string, object?>("mcp.method", "resources/list"),
                new KeyValuePair<string, object?>("mcp.reason", ex.Message)
            });
            return Array.Empty<McpResourceInfo>();
        }
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
            ctx.Engine.Logger.LogWarning(ex, "mcp.list: prompts/list not supported on '{ServerName}'", serverName);
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
        var current = ex;
        while (current != null)
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

            current = current.InnerException!;
        }

        return false;
    }
}
