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

    internal static bool IsCapabilityCandidateSelectionEnabled(JsonObject generator)
    {
        var prefilterNode = generator["prefilter"];
        return prefilterNode == null
               || prefilterNode is JsonObject
               || prefilterNode is JsonValue value
               && (!value.TryGetValue<bool>(out var enabled) || enabled);
    }

    internal static async Task<List<McpServerDiscovery>> SelectPhysicalCapabilityCandidatesAsync(
        ILLMClient llmClient, CapabilityInventory inventory, IReadOnlyList<McpServerDiscovery> completeDiscovery, string instruction, string generatorContext, string? provider, string model, string reasoning, StepExecutionContext ctx, TelemetrySpanScope inferenceSpan, CancellationToken ct)
    {
        var externalOperations = inventory.Operations
            .Where(static operation => string.Equals(operation.ExecutionKind, "external_effect", StringComparison.Ordinal))
            .ToArray();
        var constraints = inventory.Constraints.Where(static constraint => constraint.EnforcementKind == "exact_denial").ToArray();
        if (externalOperations.Length == 0 && constraints.Length == 0)
            return new List<McpServerDiscovery>();

        var catalog = BuildPhysicalCapabilityCatalog(completeDiscovery);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.full_server_count", completeDiscovery.Count);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.full_tool_count", completeDiscovery.Sum(static server => server.Tools.Count));
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.full_prompt_count", completeDiscovery.Sum(static server => server.Prompts.Count));
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.catalog_entry_count", catalog.Entries.Count);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.catalog_character_count", catalog.TotalCharacters);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.page_count", catalog.Pages.Count);

        if (catalog.Entries.Count == 0)
            return new List<McpServerDiscovery>();

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                $"Selecting relevant MCP capabilities from {catalog.Entries.Count} compact physical candidate(s)."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var operationSelections = externalOperations.ToDictionary(
            static operation => operation.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var constraintSelections = constraints.ToDictionary(
            static constraint => constraint.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var knownCatalogIds = catalog.Entries.Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        await RunPhysicalCandidateSelectionPassAsync(
            ctx,
            llmClient,
            inventory,
            catalog,
            instruction,
            generatorContext,
            provider,
            model,
            reasoning,
            repair: false,
            targetOperationIds: externalOperations.Select(static operation => operation.Id).ToHashSet(StringComparer.Ordinal),
            targetConstraintIds: constraints.Select(static constraint => constraint.Id).ToHashSet(StringComparer.Ordinal),
            knownCatalogIds,
            operationSelections,
            constraintSelections,
            inferenceSpan,
            ct);

        var missingRequiredOperationIds = externalOperations
            .Where(static operation => operation.Required)
            .Where(operation => operationSelections[operation.Id].Count == 0)
            .Select(static operation => operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        var missingRequiredExactDenialIds = constraints
            .Where(static constraint => constraint.Required)
            .Where(static constraint => string.Equals(
                constraint.EnforcementKind,
                "exact_denial",
                StringComparison.Ordinal))
            .Where(constraint => constraintSelections[constraint.Id].Count == 0)
            .Select(static constraint => constraint.Id)
            .ToHashSet(StringComparer.Ordinal);
        var repairAttempted = missingRequiredOperationIds.Count > 0
                              || missingRequiredExactDenialIds.Count > 0;
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.repair_attempted", repairAttempted);

        if (repairAttempted)
        {
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    "Physical capability selection omitted a required inventory item; considering the compact full catalog once more."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });
            await RunPhysicalCandidateSelectionPassAsync(
                ctx,
                llmClient,
                inventory,
                catalog,
                instruction,
                generatorContext,
                provider,
                model,
                reasoning,
                repair: true,
                missingRequiredOperationIds,
                missingRequiredExactDenialIds,
                knownCatalogIds,
                operationSelections,
                constraintSelections,
                inferenceSpan,
                ct);
        }

        var selection = new PhysicalCandidateSelection(
            operationSelections.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal)
                    .Take(PhysicalCapabilityMaxCandidatesPerInventoryItem).ToArray(),
                StringComparer.Ordinal),
            constraintSelections.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal)
                    .Take(PhysicalCapabilityMaxCandidatesPerInventoryItem).ToArray(),
                StringComparer.Ordinal),
            repairAttempted);
        var selectedIds = selection.OperationCandidates.Values
            .Concat(selection.ConstraintCandidates.Values)
            .SelectMany(static ids => ids)
            .ToHashSet(StringComparer.Ordinal);
        var selectedPhysicalEntries = catalog.Entries.Where(entry => selectedIds.Contains(entry.Id)).ToArray();
        var selectedDiscovery = FilterDiscoveryToPhysicalEntries(completeDiscovery, selectedPhysicalEntries);
        selectedDiscovery = ExpandSelectedOperationalArtifactPrerequisites(
            selectedDiscovery,
            completeDiscovery,
            instruction);
        selectedDiscovery = ExpandSelectedCompositionWrappers(selectedDiscovery, completeDiscovery);

        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.selected_server_count", selectedDiscovery.Count);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.selected_tool_count", selectedDiscovery.Sum(static server => server.Tools.Count));
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.selected_prompt_count", selectedDiscovery.Sum(static server => server.Prompts.Count));
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.unresolved_required_operation_count",
            missingRequiredOperationIds.Count(id => operationSelections[id].Count == 0));
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_candidates.unresolved_required_exact_denial_count",
            missingRequiredExactDenialIds.Count(id => constraintSelections[id].Count == 0));

        ctx.AddTelemetryEvent("gnougo-flow.plan.capability_candidates.result", new[]
        {
            new KeyValuePair<string, object?>("mcp.servers_total", completeDiscovery.Count),
            new KeyValuePair<string, object?>("mcp.tools_total", completeDiscovery.Sum(static server => server.Tools.Count)),
            new KeyValuePair<string, object?>("mcp.servers_selected", selectedDiscovery.Count),
            new KeyValuePair<string, object?>("mcp.tools_selected", selectedDiscovery.Sum(static server => server.Tools.Count)),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_candidates.repair_attempted", repairAttempted)
        });
        return selectedDiscovery;
    }

    internal static async Task RunPhysicalCandidateSelectionPassAsync(
        StepExecutionContext ctx, ILLMClient llmClient, CapabilityInventory inventory, PhysicalCapabilityCatalog catalog, string instruction, string generatorContext, string? provider, string model, string reasoning, bool repair, IReadOnlySet<string> targetOperationIds, IReadOnlySet<string> targetConstraintIds, IReadOnlySet<string> knownCatalogIds, Dictionary<string, HashSet<string>> operationSelections, Dictionary<string, HashSet<string>> constraintSelections, TelemetrySpanScope inferenceSpan, CancellationToken ct)
    {
        if (targetOperationIds.Count == 0 && targetConstraintIds.Count == 0)
            return;

        for (var pageIndex = 0; pageIndex < catalog.Pages.Count; pageIndex++)
        {
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildPhysicalCapabilitySelectionPrompt(
                    inventory,
                    catalog.Pages[pageIndex],
                    pageIndex + 1,
                    catalog.Pages.Count,
                    instruction,
                    generatorContext,
                    repair,
                    targetOperationIds,
                    targetConstraintIds),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildPhysicalCapabilitySelectionSchema(),
                StructuredOutputStrict = true
            }, "workflow.plan.capability_candidates", ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);

            try
            {
                var parsed = ParseStructuredObject(response, repair
                    ? "physical capability candidate repair"
                    : "physical capability candidate selection");
                MergePhysicalCandidateSelections(
                    parsed["operation_candidates"] as JsonArray,
                    "operation_id",
                    targetOperationIds,
                    knownCatalogIds,
                    operationSelections);
                MergePhysicalCandidateSelections(
                    parsed["constraint_candidates"] as JsonArray,
                    "constraint_id",
                    targetConstraintIds,
                    knownCatalogIds,
                    constraintSelections);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                inferenceSpan.AddEvent("gnougo-flow.plan.capability_candidates.invalid_page", new[]
                {
                    new KeyValuePair<string, object?>("page", pageIndex + 1),
                    new KeyValuePair<string, object?>("repair", repair),
                    new KeyValuePair<string, object?>("error", SanitizeCapabilityInferenceDiagnostic(ex.Message, 1_000))
                });
            }
        }
    }

    internal static void MergePhysicalCandidateSelections(
        JsonArray? items, string inventoryIdProperty, IReadOnlySet<string> targetInventoryIds, IReadOnlySet<string> knownCatalogIds, Dictionary<string, HashSet<string>> destination)
    {
        if (items == null)
            return;

        foreach (var item in items.OfType<JsonObject>())
        {
            var inventoryId = item[inventoryIdProperty]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(inventoryId)
                || !targetInventoryIds.Contains(inventoryId)
                || !destination.TryGetValue(inventoryId, out var selected)
                || item["catalog_ids"] is not JsonArray ids)
            {
                continue;
            }

            foreach (var idNode in ids.OfType<JsonValue>())
            {
                if (selected.Count >= PhysicalCapabilityMaxCandidatesPerInventoryItem)
                    break;
                if (idNode.TryGetValue<string>(out var id) && knownCatalogIds.Contains(id))
                    selected.Add(id);
            }
        }
    }

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
        var pages = new List<string>();
        var page = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length + Environment.NewLine.Length > PhysicalCapabilityPageMaxCharacters)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    "One compact physical capability entry exceeds the safe selector page limit.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_candidate_selection",
                        ["reason"] = "physical_entry_too_large",
                        ["maximum_page_characters"] = PhysicalCapabilityPageMaxCharacters,
                        ["entry_characters"] = line.Length
                    });
            }

            if (page.Length > 0 && page.Length + line.Length + Environment.NewLine.Length > PhysicalCapabilityPageMaxCharacters)
            {
                pages.Add(page.ToString());
                page.Clear();
            }
            page.AppendLine(line);
        }
        if (page.Length > 0)
            pages.Add(page.ToString());

        if (pages.Count > PhysicalCapabilityMaxPages)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "The compact physical capability catalog exceeds the bounded selector limit.",
                details: new JsonObject
                {
                    ["phase"] = "capability_candidate_selection",
                    ["reason"] = "physical_catalog_too_large",
                    ["maximum_pages"] = PhysicalCapabilityMaxPages,
                    ["page_count"] = pages.Count,
                    ["total_characters"] = totalCharacters,
                    ["full_server_count"] = discovery.Count,
                    ["full_tool_count"] = discovery.Sum(static server => server.Tools.Count),
                    ["full_prompt_count"] = discovery.Sum(static server => server.Prompts.Count)
                });
        }

        return new PhysicalCapabilityCatalog(ordered, pages, totalCharacters);
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

    internal static string BuildPhysicalCapabilitySelectionPrompt(
        CapabilityInventory inventory, string catalogPage, int pageNumber, int pageCount, string instruction, string context, bool repair, IReadOnlySet<string> targetOperationIds, IReadOnlySet<string> targetConstraintIds) => $$"""
        You are a domain-neutral physical capability candidate selector. Return only the requested structured JSON.

        This is {{(repair ? "the single bounded repair pass" : "the initial selection pass")}}, page {{pageNumber}} of {{pageCount}}. The catalog contains exactly one compact row per physical MCP tool or prompt. It keeps bounded selector literals inline for intent matching but does not expand them into separate rows; authoritative variants are expanded and validated only after physical candidates are selected.

        For each target external operation, select zero or more plausible physical catalog IDs from this page. Select complementary prerequisite, lifecycle, and cleanup capabilities when their descriptions or artifact contracts make them relevant. When a plausible consumer declares a required artifact kind, also select a producer of that exact declared kind; the runtime will validate its pointers and dependency closure later. For each target constraint, select exact physical capabilities only when they may be unconditionally prohibited by that constraint. Do not select tools for local processing or human interaction. Do not invent IDs. An empty list is valid when this page contains no plausible candidate. Keep at most {{PhysicalCapabilityMaxCandidatesPerInventoryItem}} candidates per inventory item across the catalog.

        <target_operation_ids>
        {{new JsonArray(targetOperationIds.Order(StringComparer.Ordinal).Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()).ToJsonString()}}
        </target_operation_ids>
        <target_constraint_ids>
        {{new JsonArray(targetConstraintIds.Order(StringComparer.Ordinal).Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()).ToJsonString()}}
        </target_constraint_ids>
        <runtime_inventory>
        {{BuildCapabilityInventoryJson(inventory)}}
        </runtime_inventory>
        <compact_physical_catalog_page>
        {{catalogPage}}
        </compact_physical_catalog_page>

        {{BuildUserTaskBlock(instruction, context)}}
        """;

    internal static JsonObject BuildPhysicalCapabilitySelectionSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["operation_candidates"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["operation_id"] = new JsonObject { ["type"] = "string" },
                        ["catalog_ids"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        }
                    },
                    ["required"] = new JsonArray("operation_id", "catalog_ids"),
                    ["additionalProperties"] = false
                }
            },
            ["constraint_candidates"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["constraint_id"] = new JsonObject { ["type"] = "string" },
                        ["catalog_ids"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        }
                    },
                    ["required"] = new JsonArray("constraint_id", "catalog_ids"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("operation_candidates", "constraint_candidates"),
        ["additionalProperties"] = false
    };

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
