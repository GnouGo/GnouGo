using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    // ── MCP discovery data ──────────────────────────────────────────────

    /// <summary>
    /// Holds the result of discovering tools and prompts from one MCP server.
    /// </summary>
    private sealed class McpServerDiscovery
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public int? CallTimeoutSeconds { get; init; }
        public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();
        public IReadOnlyList<McpPromptInfo> Prompts { get; init; } = Array.Empty<McpPromptInfo>();
        /// <summary>True when the server was reachable and listing succeeded.</summary>
        public bool Discovered { get; init; }
    }

    private static HashSet<string> ExtractRequiredMcpServerNames(
        string instruction,
        string? context,
        IReadOnlyList<McpServerMetadata>? configuredServers)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configuredServers == null || configuredServers.Count == 0)
            return required;

        var configuredByName = configuredServers.ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase);
        var text = string.IsNullOrWhiteSpace(context)
            ? instruction
            : instruction + "\n" + context;

        foreach (Match match in Regex.Matches(text, @"(?im)^\s*server\s*:\s*[""']?([^""'\r\n#]+)"))
        {
            var candidate = match.Groups[1].Value.Trim().TrimEnd(',', ']');
            if (configuredByName.TryGetValue(candidate, out var server))
                required.Add(server.Name);
        }

        foreach (Match match in Regex.Matches(text, @"(?im)^\s*servers\s*:\s*\[([^\]\r\n#]+)\]"))
        {
            foreach (var raw in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = raw.Trim().Trim('"', '\'');
                if (configuredByName.TryGetValue(candidate, out var server))
                    required.Add(server.Name);
            }
        }

        return required;
    }

    private static IReadOnlyList<McpServerMetadata>? MergeRequiredMcpServerMetadata(
        IReadOnlyList<McpServerMetadata>? selected,
        IReadOnlyList<McpServerMetadata>? allServers,
        IReadOnlySet<string> requiredServerNames,
        StepExecutionContext ctx)
    {
        if (requiredServerNames.Count == 0 || allServers == null || allServers.Count == 0 || selected == null)
            return selected;

        var merged = selected.ToList();
        var seen = merged.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var server in allServers)
        {
            if (requiredServerNames.Contains(server.Name) && seen.Add(server.Name))
                merged.Add(server);
        }

        if (merged.Count != selected.Count)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.required_servers", new[]
            {
                new KeyValuePair<string, object?>("mcp.server.names", string.Join(",", requiredServerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))),
                new KeyValuePair<string, object?>("mcp.servers_selected", merged.Count)
            });
        }

        return merged;
    }

    private static List<McpServerDiscovery>? SelectDiscoveredServers(
        IReadOnlyList<McpServerDiscovery>? source,
        IReadOnlyList<McpServerMetadata>? selectedServers)
    {
        if (source == null)
            return null;
        if (selectedServers == null)
            return source.ToList();
        if (selectedServers.Count == 0)
            return new List<McpServerDiscovery>();

        var selectedNames = selectedServers.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source.Where(server => selectedNames.Contains(server.Name)).ToList();
    }

    private static List<McpServerDiscovery>? MergeRequiredMcpServerDiscovery(
        IReadOnlyList<McpServerDiscovery>? selected,
        IReadOnlyList<McpServerDiscovery>? allServers,
        IReadOnlySet<string> requiredServerNames,
        StepExecutionContext ctx)
    {
        if (selected == null || requiredServerNames.Count == 0)
            return selected?.ToList();

        var merged = selected.ToList();
        if (allServers == null || allServers.Count == 0)
            return merged;

        var seen = merged.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var server in allServers)
        {
            if (requiredServerNames.Contains(server.Name) && seen.Add(server.Name))
                merged.Add(server);
        }

        if (merged.Count != selected.Count)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.required_servers", new[]
            {
                new KeyValuePair<string, object?>("mcp.server.names", string.Join(",", requiredServerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))),
                new KeyValuePair<string, object?>("mcp.servers_selected", merged.Count)
            });
        }

        return merged;
    }

    /// <summary>
    /// Connects to each configured MCP server and lists its tools/prompts.
    /// Returns null when no servers are configured.
    /// </summary>
    private static async Task<List<McpServerDiscovery>?> DiscoverMcpServersAsync(
        IMcpClientFactory? factory,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache,
        ILogger logger,
        StepExecutionContext ctx,
        IReadOnlyList<McpServerMetadata>? candidateServers,
        CancellationToken ct)
    {
        if (factory?.ServerMetadata == null || factory.ServerMetadata.Count == 0)
            return null;

        var serverMetadata = candidateServers ?? factory.ServerMetadata;
        if (serverMetadata.Count == 0)
            return new List<McpServerDiscovery>();

        var serverCount = serverMetadata.Count;

        using var discoverySpan = ctx.BeginTelemetrySpan("workflow.plan.mcp_discovery", "mcp_discovery", new[]
        {
            new KeyValuePair<string, object?>("mcp.servers_total", serverCount)
        });

        // ── Thinking: MCP discovery start ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Discovering {serverCount} MCP server(s)…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var results = new List<McpServerDiscovery>();

        foreach (var server in serverMetadata)
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
                    CallTimeoutSeconds = server.CallTimeoutSeconds,
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
                var discoveryTimeout = GetMcpDiscoveryTimeout(server);
                using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                discoveryCts.CancelAfter(discoveryTimeout);

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
                    CallTimeoutSeconds = server.CallTimeoutSeconds,
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
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                var discoveryTimeout = GetMcpDiscoveryTimeout(server);
                logger.LogWarning(ex,
                    "workflow.plan: MCP server '{ServerName}' discovery timed out after {TimeoutSeconds}s",
                    server.Name,
                    discoveryTimeout.TotalSeconds);
                results.Add(new McpServerDiscovery
                {
                    Name = server.Name,
                    Description = server.Description,
                    CallTimeoutSeconds = server.CallTimeoutSeconds,
                    Discovered = false
                });

                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': discovery timed out after {discoveryTimeout.TotalSeconds:0.#}s"),
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
                    CallTimeoutSeconds = server.CallTimeoutSeconds,
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
        discoverySpan.SetAttribute("mcp.servers_discovered", discoveredCount);
        discoverySpan.SetAttribute("mcp.tools_total", totalTools);
        discoverySpan.SetAttribute("mcp.prompts_total", totalPrompts);
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                $"MCP discovery complete: {discoveredCount}/{serverCount} server(s), {totalTools} tool(s), {totalPrompts} prompt(s)"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });

        return results;
    }

    private static TimeSpan GetMcpDiscoveryTimeout(McpServerMetadata server)
    {
        var seconds = server.DiscoveryTimeoutSeconds ?? DefaultMcpDiscoveryTimeoutSeconds;
        seconds = Math.Clamp(seconds, MinMcpDiscoveryTimeoutSeconds, MaxMcpDiscoveryTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Calls an LLM with a strict structured-output schema to select relevant MCP servers
    /// from static server descriptions before opening MCP sessions for tool discovery.
    /// Returns null when no factory/metadata is available, and returns the full metadata list on failure.
    /// </summary>
    private static async Task<IReadOnlyList<McpServerMetadata>?> PrefilterMcpServerMetadataAsync(
        ILLMClient llmClient,
        IMcpClientFactory? factory,
        string instruction,
        string context,
        string model,
        string? provider,
        double? temperature,
        string? planReasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        if (factory?.ServerMetadata == null || factory.ServerMetadata.Count == 0)
            return null;

        var allServers = factory.ServerMetadata;
        using var prefilterSpan = ctx.BeginTelemetrySpan("workflow.plan.mcp_server_prefilter", "mcp_server_prefilter", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count)
        });
        var catalogSb = new StringBuilder();
        foreach (var server in allServers)
            catalogSb.AppendLine($"- {server.Name}: {(string.IsNullOrWhiteSpace(server.Description) ? "(no description)" : server.Description)}");

        var prefilterPrompt = $$"""
            You are an MCP server-selection assistant. Given a task description and a catalog of MCP server descriptions, select only the servers likely to contain relevant capabilities.

            Return ONLY a JSON object matching this shape:
            {
              "servers": [
                { "name": "server-name", "reason": "short relevance reason" }
              ]
            }

            Rules:
            - Use only exact server names from the catalog.
            - Base the decision only on server descriptions, not on imagined tools.
            - Include every plausibly relevant server; exclude clearly unrelated servers.
            - If no server is relevant, return {"servers": []}.

            <server_catalog>
            {{catalogSb}}
            </server_catalog>

            {{BuildUserTaskBlock(instruction, context)}}
            """;

        try
        {
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"Pre-filtering MCP server descriptions with {model} ({allServers.Count} server(s))…"),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
            });

            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.servers.start", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count)
            });

            if (ctx.Limits.LogStepContent)
            {
                prefilterSpan.AddEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", prefilterPrompt),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter")
                });
                ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", prefilterPrompt),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter")
                });
            }

            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prefilterPrompt,
                Temperature = temperature,
                StructuredOutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "servers": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "reason": { "type": "string" }
                        },
                        "required": ["name", "reason"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["servers"],
                  "additionalProperties": false
                }
                """),
                StructuredOutputStrict = true,
                Reasoning = planReasoning,
            }, ct);

            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                prefilterSpan.AddEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter")
                });
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter")
                });
            }

            AddUsageAttributes(prefilterSpan, response.Usage, model, provider);
            AddPrefilterUsageEvent(ctx, response.Usage, model, provider, "mcp_server_prefilter", "gnougo-flow.plan.prefilter.servers.usage");

            var payload = response.Json as JsonObject;
            if (payload == null && !string.IsNullOrWhiteSpace(response.Text))
                payload = JsonNode.Parse(StripMarkdownFences(response.Text).Trim()) as JsonObject;

            var serversArr = payload?["servers"] as JsonArray
                ?? throw new InvalidOperationException("MCP server prefilter response did not contain a servers array.");

            var byName = allServers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            var selected = new List<McpServerMetadata>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in serversArr)
            {
                var name = entry is JsonObject obj ? obj["name"]?.GetValue<string>() : entry?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || !byName.TryGetValue(name, out var metadata) || !seen.Add(metadata.Name))
                    continue;
                selected.Add(metadata);
            }

            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.servers.result", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count),
                new KeyValuePair<string, object?>("mcp.servers_selected", selected.Count),
                new KeyValuePair<string, object?>("mcp.server.names", string.Join(",", selected.Select(s => s.Name)))
            });
            prefilterSpan.SetAttribute("mcp.servers_selected", selected.Count);
            prefilterSpan.SetAttribute("mcp.server.names", string.Join(",", selected.Select(s => s.Name)));

            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"MCP server pre-filter: {selected.Count}/{allServers.Count} server(s) selected before discovery."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });

            return selected;
        }
        catch (Exception ex)
        {
            prefilterSpan.Fail(ex);
            ctx.Engine.Logger.LogWarning(ex, "workflow.plan: MCP server prefilter failed, falling back to full server list");
            ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.servers.fallback", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name),
                new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count)
            });

            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"MCP server pre-filter failed ({ex.Message}), discovering all {allServers.Count} server(s)."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });

            return allServers;
        }
    }
}
