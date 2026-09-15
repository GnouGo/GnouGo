using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using static GnOuGo.Flow.Planning.Capabilities.CapabilityCatalogBuilder;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;
using static GnOuGo.Flow.Planning.Capabilities.PreparationTelemetry;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityDiscovery
{

    internal static PhysicalCapabilityCatalog BuildPhysicalCapabilityCatalog(
        IReadOnlyList<McpServerDiscovery> discovery)
    {
        var pending = new List<(string Server, string Kind, string Method, string Card, IReadOnlyList<string> SelectorIntentValues)>();
        foreach (var server in discovery.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            foreach (var prompt in server.Prompts.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                var arguments = prompt.Arguments is { Count: > 0 }
                    ? string.Join(",", prompt.Arguments.Take(24).Select(static argument =>
                        argument.Required ? $"{argument.Name}:string(required)" : $"{argument.Name}:string"))
                    : "none";
                var description = LimitPhysicalCapabilityDescription(prompt.Description);
                pending.Add((server.Name, "prompt", prompt.Name,
                    $"server={server.Name} kind=prompt method={prompt.Name} description={description} arguments=[{arguments}]",
                    Array.Empty<string>()));
            }

            foreach (var tool in server.Tools.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                if (IsManagementOnlyMcpTool(tool))
                    continue;
                var description = LimitPhysicalCapabilityDescription(tool.Description);
                var arguments = BuildPhysicalSchemaSummary(tool.InputSchema);
                var outputs = BuildPhysicalSchemaSummary(
                    McpToolContractEnricher.GetAuthoritativeOutputSchema(tool));
                var outputContractSummary = FormatOutputContractSummary(tool);
                var artifactContract = GetValidatedMcpArtifactContract(tool, server.Name);
                var artifactSummary = FormatArtifactContractSummary(artifactContract);
                var compositionContract = GetValidatedMcpCompositionContract(tool, server.Name);
                var compositionSummary = FormatCompositionContractSummary(compositionContract);
                var selectorIntentValues = ExtractSelectorVariants(tool.InputSchema)
                    .SelectMany(static variant => variant.Bindings)
                    .Select(static binding => binding.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text
                        : null)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(CapabilitySelectorMaxValues)
                    .ToArray();
                pending.Add((server.Name, "tool", tool.Name,
                    $"server={server.Name} kind=tool method={tool.Name} description={description} arguments=[{arguments}] outputs=[{outputs}]{outputContractSummary}{artifactSummary}{compositionSummary}",
                    selectorIntentValues));
            }
        }

        var ordered = pending
            .OrderBy(static item => item.Server, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.Method, StringComparer.Ordinal)
            .Select((item, index) => new PhysicalCapabilityEntry(
                $"physical_{index + 1:D6}",
                item.Server,
                item.Kind,
                item.Method,
                item.Card,
                item.SelectorIntentValues))
            .ToArray();
        var lines = ordered.Select(static entry => $"{entry.Id} {entry.Card}").ToArray();
        var totalCharacters = lines.Sum(static line => line.Length + Environment.NewLine.Length);
        return new PhysicalCapabilityCatalog(ordered, totalCharacters);
    }

    internal static List<McpServerDiscovery> ExpandSelectedCompositionWrappers(
        List<McpServerDiscovery> selected, IReadOnlyList<McpServerDiscovery> complete)
    {
        var selectedCapabilities = selected
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Kind: "tool", Method: tool.Name)))
            .ToArray();
        foreach (var server in complete)
        {
            foreach (var wrapper in server.Tools)
            {
                var composition = GetValidatedMcpCompositionContract(wrapper, server.Name);
                if (composition is not
                    {
                        Kind: McpCapabilityCompositionConventions.CompleteOperationKind,
                        Encapsulates.Count: > 0
                    })
                {
                    continue;
                }

                if (composition.Encapsulates.Any(encapsulated => selectedCapabilities.Any(selectedCapability =>
                        string.Equals(selectedCapability.Server, server.Name, StringComparison.Ordinal)
                        && string.Equals(selectedCapability.Kind, encapsulated.Kind, StringComparison.Ordinal)
                        && string.Equals(selectedCapability.Method, encapsulated.Method, StringComparison.Ordinal))))
                {
                    _ = AddToolToDiscovery(selected, server, wrapper);
                }
            }
        }

        return selected;
    }

    internal static string BuildPhysicalSchemaSummary(JsonNode? schema)
    {
        if (schema is not JsonObject root)
            return "unknown";
        if (root["properties"] is not JsonObject properties || properties.Count == 0)
            return ReadPhysicalSchemaType(root);

        var required = GetRequiredPropertyNames(root);
        var fields = properties
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Take(24)
            .Select(property => property.Value is JsonObject propertySchema
                ? $"/{EncodeJsonPointerToken(property.Key)}:{ReadPhysicalSchemaType(propertySchema)}{(required.Contains(property.Key) ? "(required)" : string.Empty)}{FormatPhysicalSelectorValues(propertySchema, "/" + EncodeJsonPointerToken(property.Key))}"
                : $"/{EncodeJsonPointerToken(property.Key)}:unknown")
            .ToArray();
        return string.Join(",", fields);
    }

    internal static string FormatPhysicalSelectorValues(JsonObject schema, string path)
    {
        var values = ReadDocumentedScalarValues(schema, path);
        return values.Count == 0
            ? string.Empty
            : $"{{allowed={string.Join('|', values.Select(CanonicalScalar))}}}";
    }

    internal static string ReadPhysicalSchemaType(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type))
            return type;
        if (schema["type"] is JsonArray types)
        {
            var values = types.OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var candidate) ? candidate : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join("|", values!);
        }
        if (schema["properties"] is JsonObject)
            return "object";
        if (schema["items"] != null)
            return "array";
        return "constrained";
    }

    internal static string LimitPhysicalCapabilityDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description)
            ? "No description supplied."
            : string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= PhysicalCapabilityDescriptionMaxCharacters
            ? normalized
            : normalized[..PhysicalCapabilityDescriptionMaxCharacters];
    }

    internal static List<McpServerDiscovery> FilterDiscoveryToPhysicalEntries(
        IReadOnlyList<McpServerDiscovery> discovery, IReadOnlyList<PhysicalCapabilityEntry> selected)
    {
        var selectedKeys = selected.Select(static entry => (entry.Server, entry.Kind, entry.Method))
            .ToHashSet();
        return discovery.Select(server => new McpServerDiscovery
        {
            Name = server.Name,
            Description = server.Description,
            CallTimeoutSeconds = server.CallTimeoutSeconds,
            Discovered = server.Discovered,
            Tools = server.Tools.Where(tool => selectedKeys.Contains((server.Name, "tool", tool.Name))).ToArray(),
            Prompts = server.Prompts.Where(prompt => selectedKeys.Contains((server.Name, "prompt", prompt.Name))).ToArray()
        })
            .Where(static server => server.Tools.Count > 0 || server.Prompts.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Connects to each configured MCP server and lists its tools/prompts.
    /// Returns null when no servers are configured.
    /// </summary>
    internal static async Task<List<McpServerDiscovery>?> DiscoverMcpServersAsync(
        IMcpClientFactory? factory, Microsoft.Extensions.Caching.Memory.IMemoryCache? cache, ILogger logger, StepExecutionContext ctx, IReadOnlyList<McpServerMetadata>? candidateServers, ITelemetrySpan? parentSpan, CancellationToken ct, bool refresh = false)
    {
        if (factory?.ServerMetadata == null || factory.ServerMetadata.Count == 0)
            return null;

        var serverMetadata = candidateServers ?? factory.ServerMetadata;
        if (serverMetadata.Count == 0)
            return new List<McpServerDiscovery>();

        var serverCount = serverMetadata.Count;

        using var discoverySpan = parentSpan == null
            ? ctx.BeginTelemetrySpan("workflow.plan.mcp_discovery", "mcp_discovery", new[]
            {
                new KeyValuePair<string, object?>("mcp.servers_total", serverCount)
            })
            : ctx.BeginTelemetrySpan(parentSpan, "workflow.plan.mcp_discovery", "mcp_discovery", new[]
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
            var cachedTools = refresh ? null : McpCacheHelper.GetCachedTools(cache, server.Name);
            var cachedPrompts = refresh ? null : McpCacheHelper.GetCachedPrompts(cache, server.Name);

            if (cachedTools != null && cachedPrompts != null)
            {
                var enrichedCachedTools = McpToolContractEnricher.EnrichTools(cachedTools);
                logger.LogDebug("workflow.plan: serving MCP server '{ServerName}' discovery from cache", server.Name);
                results.Add(new McpServerDiscovery
                {
                    Name = server.Name,
                    Description = server.Description,
                    CallTimeoutSeconds = server.CallTimeoutSeconds,
                    Tools = enrichedCachedTools,
                    Prompts = cachedPrompts,
                    Discovered = true
                });

                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': {enrichedCachedTools.Count} tool(s), {cachedPrompts.Count} prompt(s) (cached)"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                continue;
            }

            Exception? lastDiscoveryError = null;
            var discovered = false;
            for (var attempt = 1; attempt <= McpDiscoveryMaxAttempts; attempt++)
            {
                try
                {
                    var discoveryTimeout = GetMcpDiscoveryTimeout(server);
                    using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    discoveryCts.CancelAfter(discoveryTimeout);

                    await using var session = await factory.GetClientAsync(server.Name, discoveryCts.Token);

                    var tools = McpToolContractEnricher.EnrichTools(cachedTools ?? await session.ListToolsAsync(discoveryCts.Token));
                    McpCacheHelper.CacheTools(cache, server.Name, tools, ctx.Engine.McpCacheSlidingExpiration);

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
                        McpCacheHelper.CachePrompts(cache, server.Name, prompts, ctx.Engine.McpCacheSlidingExpiration);
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
                    discovered = true;

                    ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                            $"MCP '{server.Name}': {tools.Count} tool(s), {prompts.Count} prompt(s) (attempt {attempt}/{McpDiscoveryMaxAttempts})"),
                        new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                    });
                    break;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    lastDiscoveryError = ex;
                    logger.LogWarning(ex,
                        "workflow.plan: MCP server '{ServerName}' discovery attempt {Attempt}/{MaxAttempts} timed out after {TimeoutSeconds}s",
                        server.Name,
                        attempt,
                        McpDiscoveryMaxAttempts,
                        GetMcpDiscoveryTimeout(server).TotalSeconds);
                }
                catch (Exception ex)
                {
                    lastDiscoveryError = ex;
                    logger.LogWarning(ex,
                        "workflow.plan: MCP server '{ServerName}' discovery attempt {Attempt}/{MaxAttempts} failed",
                        server.Name,
                        attempt,
                        McpDiscoveryMaxAttempts);
                }

                if (attempt >= McpDiscoveryMaxAttempts)
                    break;

                var retryDelay = GetMcpDiscoveryRetryDelay(attempt);
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        $"MCP '{server.Name}': discovery attempt {attempt}/{McpDiscoveryMaxAttempts} failed; retrying in {retryDelay.TotalMilliseconds:0}ms"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });
                await Task.Delay(retryDelay, ct);
            }

            if (!discovered)
            {
                logger.LogError(lastDiscoveryError,
                    "workflow.plan: failed to discover MCP server '{ServerName}' after {MaxAttempts} attempts",
                    server.Name,
                    McpDiscoveryMaxAttempts);
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
                        $"MCP '{server.Name}': discovery failed after {McpDiscoveryMaxAttempts} attempts — {lastDiscoveryError?.Message ?? "unknown error"}"),
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

    internal static TimeSpan GetMcpDiscoveryTimeout(McpServerMetadata server)
    {
        var seconds = server.DiscoveryTimeoutSeconds ?? DefaultMcpDiscoveryTimeoutSeconds;
        seconds = Math.Clamp(seconds, MinMcpDiscoveryTimeoutSeconds, MaxMcpDiscoveryTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    internal static TimeSpan GetMcpDiscoveryRetryDelay(int failedAttempt)
    {
        var multiplier = 1 << Math.Clamp(failedAttempt - 1, 0, 10);
        return TimeSpan.FromMilliseconds(McpDiscoveryRetryBaseDelayMilliseconds * multiplier);
    }

    internal static bool IsManagementOnlyMcpTool(McpToolInfo tool)
        => string.Equals(
            tool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>(),
            "management_only",
            StringComparison.OrdinalIgnoreCase);

    internal static List<string> GetRequiredPropertyNames(JsonNode? schema)
    {
        if (schema is not JsonObject obj || obj["required"] is not JsonArray required)
            return new List<string>();

        return required
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
