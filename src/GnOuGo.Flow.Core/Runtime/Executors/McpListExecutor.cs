using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Lists tools, resources, and/or prompts from one or more MCP servers.
///
/// Input:
///   - servers (array of string, required) : MCP server names, or ["*"] for all configured servers
///   - include (array of string, optional) : which capabilities to list — "tools", "resources", "prompts"
///       Defaults to ["tools"] if omitted.
///   - timeout_ms (number, optional) : timeout in milliseconds
///
/// Output:
///   {
///     status: "ok"|"partial"|"error",
///     text: "...",               // merged human-readable description
///     servers: [ ... ],           // per-server details
///     tools: [ ... ],             // flattened tools with { server, name, ... }
///     resources: [ ... ],         // flattened resources with { server, uri, ... }
///     prompts: [ ... ]            // flattened prompts with { server, name, ... }
///   }
/// </summary>
public sealed class McpListExecutor : IStepExecutor
{
    public string StepType => "mcp.list";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object is malformed, `servers` is missing/invalid, or `include` contains unsupported values."),
        new(ErrorCodes.McpConnectionError, false, "No MCP client factory is configured for this runtime."),
        new(ErrorCodes.McpTimeout, true, "Listing MCP capabilities timed out. This is retryable."),
        new(ErrorCodes.McpListError, false, "One or more MCP servers failed while listing tools/resources/prompts.")
    };

    public string DslSnippet => """
        ### mcp.list — List MCP capabilities across servers
        Use configured MCP server names in `input.servers`. Use `servers: ["*"]` to inspect every configured MCP server.
        When workflow.plan provides an `[AVAILABLE MCP SERVERS]` section, pick those exact names.
        Required MCP planning pattern: discover candidate servers from descriptions -> use `mcp.list` to inspect tools/prompts/resources -> select the exact tool/prompt -> build the request arguments -> use `mcp.call`.
        Prefer `mcp.list` first when the exact tool or prompt name is not known in advance.
        The output of `mcp.list` can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts` when you want an LLM-guided MCP selection step.
        Every item in `tools`, `resources`, and `prompts` includes a `server` field so downstream steps know where it came from.
        Do not go directly from `mcp.list` to `mcp.call` with only `server` unless the explicit goal is to call every available capability on that server.
        ```yaml
        - id: discover
          type: mcp.list
          input:
            servers: [github, docs]              # required — MCP server names
            include: ["tools", "prompts"]      # optional — defaults to ["tools"]
            timeout_ms: 30000                    # optional

        - id: discover_all
          type: mcp.list
          input:
            servers: ["*"]
            include: ["tools"]

        - id: choose_and_call
          type: mcp.call
          input:
            server: my-mcp-server
            model: gpt-4o-mini
            prompt: "Choose the right MCP capability and call it"
            tools: "${data.steps.discover.tools}"
            prompts: "${data.steps.discover.prompts}"
        ```
        Output: `{ status, text, servers: [...], tools: [...], resources: [...], prompts: [...] }`
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

        var serverNames = ResolveServerNames(input, factory);

        // ── Telemetry: record request attributes ──
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "tool_list");
        ctx.SetTelemetryAttribute("mcp.server.count", serverNames.Count);
        if (serverNames.Count == 1)
            ctx.SetTelemetryAttribute("mcp.server.name", serverNames[0]);
        else
            ctx.SetTelemetryAttribute("mcp.server.names", string.Join(",", serverNames));

        var includes = ParseIncludes(input);
        ctx.SetTelemetryAttribute("mcp.include", string.Join(",", includes.OrderBy(x => x)));

        // Parse timeout
        int? timeoutMs = null;
        if (input.TryGetPropertyValue("timeout_ms", out var timeoutNode) && timeoutNode != null)
            timeoutMs = (int)ExpressionEvaluator.GetNumber(timeoutNode);

        try
        {
            using var timeoutCts = timeoutMs.HasValue
                ? new CancellationTokenSource(timeoutMs.Value)
                : new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            if (serverNames.Count == 1)
            {
                var serverResult = await FetchServerCapabilitiesAsync(factory, serverNames[0], includes, ctx, linkedCts.Token);
                return BuildAggregateResult(new[] { serverResult }, includes, ctx);
            }

            var results = new List<ServerCapabilitiesResult>(serverNames.Count);
            foreach (var serverName in serverNames)
            {
                try
                {
                    results.Add(await FetchServerCapabilitiesAsync(factory, serverName, includes, ctx, linkedCts.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ctx.Engine.Logger.LogWarning(ex, "mcp.list: failed to list capabilities for '{ServerName}'", serverName);
                    results.Add(new ServerCapabilitiesResult
                    {
                        ServerName = serverName,
                        Status = "error",
                        Error = ex.Message
                    });
                }
            }

            return BuildAggregateResult(results, includes, ctx);
        }
        catch (WorkflowRuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowRuntimeException(ErrorCodes.McpTimeout,
                $"mcp.list timed out after {timeoutMs}ms", retryable: true);
        }
        catch (Exception ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.McpListError,
                $"mcp.list failed: {ex.Message}", retryable: false);
        }
    }

    private static List<string> ResolveServerNames(JsonObject input, IMcpClientFactory factory)
    {
        if (input["servers"] is not JsonArray serversArray || serversArray.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "mcp.list requires 'servers' as a non-empty array of strings");

        var requested = new List<string>(serversArray.Count);
        bool containsWildcard = false;

        foreach (var item in serversArray)
        {
            var name = item != null ? ExpressionEvaluator.GetString(item) : null;
            if (string.IsNullOrWhiteSpace(name))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "mcp.list 'servers' contains an empty or null entry");

            if (string.Equals(name, "*", StringComparison.Ordinal))
                containsWildcard = true;

            requested.Add(name);
        }

        if (containsWildcard)
        {
            if (requested.Count > 1)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "mcp.list 'servers' cannot mix '*' with explicit server names");

            var allServers = factory.ServerMetadata
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (allServers.Count == 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "mcp.list 'servers: [\"*\"]' found no configured MCP servers");

            return allServers;
        }

        var deduped = new List<string>(requested.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            if (seen.Add(name))
                deduped.Add(name);
        }

        return deduped;
    }

    private static HashSet<string> ParseIncludes(JsonObject input)
    {
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (input.TryGetPropertyValue("include", out var includeNode) && includeNode is JsonArray includeArr)
        {
            foreach (var item in includeArr)
            {
                var val = item != null ? ExpressionEvaluator.GetString(item) : null;
                if (string.IsNullOrWhiteSpace(val))
                    continue;

                if (!ValidIncludes.Contains(val))
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        $"mcp.list invalid include value '{val}'. Valid: tools, resources, prompts");
                includes.Add(val);
            }
        }

        if (includes.Count == 0)
            includes.Add("tools");

        return includes;
    }

    private static async Task<ServerCapabilitiesResult> FetchServerCapabilitiesAsync(
        IMcpClientFactory factory,
        string serverName,
        HashSet<string> includes,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
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
            return new ServerCapabilitiesResult
            {
                ServerName = serverName,
                Tools = cachedTools,
                Resources = cachedResources,
                Prompts = cachedPrompts
            };
        }

        await using var session = await factory.GetClientAsync(serverName, ct);

        IReadOnlyList<McpToolInfo>? tools = cachedTools;
        IReadOnlyList<McpResourceInfo>? resources = cachedResources;
        IReadOnlyList<McpPromptInfo>? prompts = cachedPrompts;

        if (wantTools && tools == null)
        {
            tools = await session.ListToolsAsync(ct);
            McpCacheHelper.CacheTools(cache, serverName, tools);
        }
        if (wantResources && resources == null)
        {
            resources = await TryListResourcesAsync(session, serverName, ctx, ct);
            McpCacheHelper.CacheResources(cache, serverName, resources);
        }
        if (wantPrompts && prompts == null)
        {
            prompts = await TryListPromptsAsync(session, serverName, ctx, ct);
            McpCacheHelper.CachePrompts(cache, serverName, prompts);
        }

        return new ServerCapabilitiesResult
        {
            ServerName = serverName,
            Tools = tools,
            Resources = resources,
            Prompts = prompts
        };
    }

    /// <summary>Builds the aggregated mcp.list result JsonObject from fetched/cached data.</summary>
    private static JsonNode BuildAggregateResult(
        IEnumerable<ServerCapabilitiesResult> serverResults,
        HashSet<string> includes,
        StepExecutionContext ctx)
    {
        var results = serverResults.ToList();
        var successCount = results.Count(r => string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var errorCount = results.Count - successCount;

        var overallStatus = errorCount == 0
            ? "ok"
            : successCount == 0
                ? "error"
                : "partial";

        var result = new JsonObject
        {
            ["status"] = overallStatus
        };

        var serversArr = new JsonArray();
        var toolsArr = includes.Contains("tools") ? new JsonArray() : null;
        var resourcesArr = includes.Contains("resources") ? new JsonArray() : null;
        var promptsArr = includes.Contains("prompts") ? new JsonArray() : null;

        var sb = new StringBuilder();
        sb.AppendLine($"MCP Servers ({results.Count})");

        int totalTools = 0;
        int totalResources = 0;
        int totalPrompts = 0;

        foreach (var serverResult in results)
        {
            var serverObj = new JsonObject
            {
                ["name"] = serverResult.ServerName,
                ["status"] = serverResult.Status
            };

            sb.AppendLine();
            sb.AppendLine($"## Server: {serverResult.ServerName}");

            if (!string.Equals(serverResult.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(serverResult.Error))
                {
                    serverObj["error"] = serverResult.Error;
                    sb.AppendLine($"Status: error");
                    sb.AppendLine($"Error: {serverResult.Error}");
                }

                serversArr.Add((JsonNode)serverObj);
                continue;
            }

            if (includes.Contains("tools"))
            {
                var serverTools = new JsonArray();
                var tools = serverResult.Tools ?? Array.Empty<McpToolInfo>();
                totalTools += tools.Count;
                sb.AppendLine();
                sb.AppendLine($"### Tools ({tools.Count})");
                foreach (var t in tools)
                {
                    var serverTool = new JsonObject
                    {
                        ["server"] = serverResult.ServerName,
                        ["name"] = t.Name,
                        ["description"] = t.Description
                    };
                    if (t.InputSchema != null)
                        serverTool["input_schema"] = t.InputSchema.DeepClone();
                    if (t.OutputSchema != null)
                        serverTool["output_schema"] = t.OutputSchema.DeepClone();
                    if (t.ExampleResponse != null)
                        serverTool["example_response"] = t.ExampleResponse.DeepClone();

                    serverTools.Add(serverTool.DeepClone());
                    toolsArr?.Add((JsonNode)serverTool);
                    sb.AppendLine($"- **{t.Name}**: {t.Description ?? "(no description)"}");
                }

                serverObj["tools"] = serverTools;
            }

            if (includes.Contains("resources"))
            {
                var serverResources = new JsonArray();
                var resources = serverResult.Resources ?? Array.Empty<McpResourceInfo>();
                totalResources += resources.Count;
                sb.AppendLine();
                sb.AppendLine($"### Resources ({resources.Count})");
                foreach (var r in resources)
                {
                    var serverResource = new JsonObject
                    {
                        ["server"] = serverResult.ServerName,
                        ["uri"] = r.Uri,
                        ["name"] = r.Name,
                        ["description"] = r.Description
                    };
                    if (r.MimeType != null)
                        serverResource["mime_type"] = r.MimeType;

                    serverResources.Add(serverResource.DeepClone());
                    resourcesArr?.Add((JsonNode)serverResource);
                    sb.AppendLine($"- **{r.Name}** ({r.Uri}): {r.Description ?? "(no description)"}");
                }

                serverObj["resources"] = serverResources;
            }

            if (includes.Contains("prompts"))
            {
                var serverPrompts = new JsonArray();
                var prompts = serverResult.Prompts ?? Array.Empty<McpPromptInfo>();
                totalPrompts += prompts.Count;
                sb.AppendLine();
                sb.AppendLine($"### Prompts ({prompts.Count})");
                foreach (var p in prompts)
                {
                    var serverPrompt = new JsonObject
                    {
                        ["server"] = serverResult.ServerName,
                        ["name"] = p.Name,
                        ["description"] = p.Description
                    };
                    if (p.Arguments != null && p.Arguments.Count > 0)
                    {
                        var argsArr = new JsonArray();
                        foreach (var a in p.Arguments)
                        {
                            argsArr.Add((JsonNode)new JsonObject
                            {
                                ["name"] = a.Name,
                                ["description"] = a.Description,
                                ["required"] = a.Required
                            });
                        }
                        serverPrompt["arguments"] = argsArr;
                    }

                    serverPrompts.Add(serverPrompt.DeepClone());
                    promptsArr?.Add((JsonNode)serverPrompt);

                    var argsText = p.Arguments != null && p.Arguments.Count > 0
                        ? $" (args: {string.Join(", ", p.Arguments.Select(a => a.Name))})"
                        : "";
                    sb.AppendLine($"- **{p.Name}**{argsText}: {p.Description ?? "(no description)"}");
                }

                serverObj["prompts"] = serverPrompts;
            }

            serversArr.Add((JsonNode)serverObj);
        }

        result["servers"] = serversArr;
        if (toolsArr != null)
            result["tools"] = toolsArr;
        if (resourcesArr != null)
            result["resources"] = resourcesArr;
        if (promptsArr != null)
            result["prompts"] = promptsArr;

        result["text"] = sb.ToString().TrimEnd();
        ctx.SetTelemetryAttribute("mcp.tools_count", totalTools);
        ctx.SetTelemetryAttribute("mcp.resources_count", totalResources);
        ctx.SetTelemetryAttribute("mcp.prompts_count", totalPrompts);
        ctx.SetTelemetryAttribute("mcp.failed_servers_count", errorCount);
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

    private sealed class ServerCapabilitiesResult
    {
        public string ServerName { get; set; } = "";
        public string Status { get; set; } = "ok";
        public string? Error { get; set; }
        public IReadOnlyList<McpToolInfo>? Tools { get; set; }
        public IReadOnlyList<McpResourceInfo>? Resources { get; set; }
        public IReadOnlyList<McpPromptInfo>? Prompts { get; set; }
    }
}
