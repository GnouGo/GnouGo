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
        double? temperature,
        string? planReasoning,
        StepExecutionContext ctx,
        ITelemetrySpan? parentSpan,
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

            <catalog>
            {{catalogSb}}
            </catalog>

            {{BuildUserTaskBlock(instruction, context)}}
            """;

        using var prefilterSpan = parentSpan == null
            ? ctx.BeginTelemetrySpan("workflow.plan.mcp_capability_prefilter", "mcp_capability_prefilter", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count),
                new KeyValuePair<string, object?>("mcp.tools_total", allServers.Sum(s => s.Tools.Count))
            })
            : ctx.BeginTelemetrySpan(parentSpan, "workflow.plan.mcp_capability_prefilter", "mcp_capability_prefilter", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("mcp.servers_total", allServers.Count),
            new KeyValuePair<string, object?>("mcp.tools_total", allServers.Sum(s => s.Tools.Count))
        });

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
                prefilterSpan.AddEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", prefilterPrompt),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_capability_prefilter")
                });
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
                          "tools": { "type": "array", "items": { "type": "string" } },
                          "prompts": { "type": "array", "items": { "type": "string" } }
                        },
                        "required": ["name", "tools", "prompts"],
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

            // ── GenAI: log prefilter completion + usage ──
            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                prefilterSpan.AddEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "mcp_capability_prefilter")
                });
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "prefilter")
                });
            }

            AddUsageAttributes(prefilterSpan, response.Usage, model, provider);
            AddPrefilterUsageEvent(ctx, response.Usage, model, provider, "prefilter", "gnougo-flow.plan.prefilter.usage");

            var filterResult = response.Json as JsonObject;
            if (filterResult == null)
            {
                var jsonText = StripMarkdownFences(response.Text).Trim();
                filterResult = JsonNode.Parse(jsonText) as JsonObject;
            }
            var serversArr = filterResult?["servers"] as JsonArray;

            if (serversArr == null || serversArr.Count == 0)
            {
                ctx.AddTelemetryEvent("gnougo-flow.plan.prefilter.result", new[]
                {
                    new KeyValuePair<string, object?>("mcp.servers_selected", 0),
                    new KeyValuePair<string, object?>("mcp.tools_selected", 0)
                });
                prefilterSpan.SetAttribute("mcp.servers_selected", 0);
                prefilterSpan.SetAttribute("mcp.tools_selected", 0);

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
                    CallTimeoutSeconds = srv.CallTimeoutSeconds,
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
            prefilterSpan.SetAttribute("mcp.servers_selected", filtered.Count);
            prefilterSpan.SetAttribute("mcp.tools_selected", filtered.Sum(s => s.Tools.Count));

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
            prefilterSpan.Fail(ex);
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
    /// Formats MCP server discovery results into the prompt text for <available_mcp_servers>.
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

            if (server.CallTimeoutSeconds is > 0)
                sb.AppendLine($"  Recommended mcp.call timeout: timeout_ms: {server.CallTimeoutSeconds.Value * 1000}");

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
                    AppendMcpCapabilityCard(sb, "      ", server, t);
                    if (t.InputSchema != null)
                        AppendJsonBlock(sb, "      ", "input_schema", t.InputSchema);
                    if (t.OutputSchema != null)
                        AppendJsonBlock(sb, "      ", "output_schema", t.OutputSchema);
                    if (t.ExampleResponse != null)
                        AppendJsonBlock(sb, "      ", "example_response", t.ExampleResponse);
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
            sb.AppendLine("Required MCP planning pattern: discover candidate servers from these descriptions -> choose one server -> use mcp.list to inspect capabilities -> either (a) choose the exact tool/prompt and call mcp.call with explicit method/methods, or (b) pass tools/prompts from mcp.list into mcp.call and use prompt/model for LLM-assisted selection; include temperature only for an explicit sampling override.");
        }
        sb.AppendLine("Choose servers from these static descriptions only; do not assume any global initialize/probing step across all servers.");
        sb.AppendLine("Do NOT generate mcp.call with only input.server as the default plan. That runtime auto-discovery mode is only for explicit call-everything scenarios on the chosen server.");
        sb.AppendLine("If the exact tool or prompt name is unknown, use mcp.list first, then either add an intermediate explicit selection step or use mcp.call with prompt + model (+ optional temperature) and pass the discovered tools/prompts.");
        sb.AppendLine("When using LLM-assisted mcp.call, put the natural-language instruction in input.prompt and pass candidate capabilities through input.tools and/or input.prompts, typically from mcp.list outputs.");
        sb.AppendLine("If a server lists a recommended mcp.call timeout, include at least that value as `input.timeout_ms` for generated calls to that server.");
        sb.AppendLine("When building `mcp.call.input.request`, preserve JSON schema scalar types exactly: numbers/integers/booleans must be unquoted YAML scalars, while strings may be quoted.");
        sb.AppendLine("Prefer adapting each `capability_card_yaml` example when it matches the task; the JSON schemas remain authoritative for exact validation.");
        sb.AppendLine("If a string field must contain JSON text, prefer a YAML literal block (`|`) so nested quotes remain valid YAML.");
        sb.AppendLine("For `mcp.call` single-tool outputs, access `data.steps.<id>.response.<field>` only when that field is documented in `output_schema` or `example_response` above. Otherwise the response is opaque: use `json(data.steps.<id>.response)` or normalize it with `llm.call` + `structured_output`.");
        return sb.ToString().TrimEnd();
    }

    private static void AppendMcpCapabilityCard(StringBuilder sb, string indent, McpServerDiscovery server, McpToolInfo tool)
    {
        var properties = GetJsonSchemaProperties(tool.InputSchema);
        var requiredNames = GetRequiredPropertyNames(tool.InputSchema);
        var propertyNames = properties.Keys
            .Concat(requiredNames)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var enumValuesByName = propertyNames
            .Select(name => new { Name = name, Values = GetEnumValues(GetSchemaProperty(properties, name)) })
            .Where(item => item.Values.Count > 0)
            .ToList();
        var numericRequiredNames = requiredNames
            .Where(name => IsNumericJsonSchema(GetSchemaProperty(properties, name)))
            .ToList();

        sb.Append(indent).AppendLine("capability_card_yaml:");
        sb.Append(indent).Append("  server: ").AppendLine(FormatYamlScalar(server.Name));
        sb.Append(indent).Append("  tool: ").AppendLine(FormatYamlScalar(tool.Name));
        sb.Append(indent).AppendLine("  kind: tool");
        if (!string.IsNullOrWhiteSpace(tool.Description))
            sb.Append(indent).Append("  purpose: ").AppendLine(FormatYamlScalar(tool.Description));

        sb.Append(indent).AppendLine("  request_contract:");
        if (requiredNames.Count > 0)
        {
            sb.Append(indent).AppendLine("    required_arguments:");
            foreach (var name in requiredNames)
            {
                sb.Append(indent)
                    .Append("      ")
                    .Append(name)
                    .Append(": ")
                    .AppendLine(DescribeJsonSchemaType(GetSchemaProperty(properties, name)));
            }
        }
        else
        {
            sb.Append(indent).AppendLine("    required_arguments: {}");
        }

        var optionalNames = propertyNames
            .Where(name => !requiredNames.Contains(name, StringComparer.Ordinal))
            .ToList();
        if (optionalNames.Count > 0)
        {
            sb.Append(indent).AppendLine("    optional_arguments:");
            foreach (var name in optionalNames)
            {
                sb.Append(indent)
                    .Append("      ")
                    .Append(name)
                    .Append(": ")
                    .AppendLine(DescribeJsonSchemaType(GetSchemaProperty(properties, name)));
            }
        }

        if (enumValuesByName.Count > 0)
        {
            sb.Append(indent).AppendLine("    valid_values:");
            foreach (var item in enumValuesByName)
            {
                sb.Append(indent).Append("      ").Append(item.Name).AppendLine(":");
                foreach (var value in item.Values)
                    sb.Append(indent).Append("        - ").AppendLine(FormatYamlScalar(value));
            }
        }

        sb.Append(indent).AppendLine("  examples:");
        var methodVariants = enumValuesByName
            .FirstOrDefault(item => string.Equals(item.Name, "method", StringComparison.OrdinalIgnoreCase))
            ?.Values
            .Take(4)
            .ToList();
        if (methodVariants is { Count: > 0 })
        {
            foreach (var methodVariant in methodVariants)
                AppendMcpCapabilityExample(sb, indent, server, tool, properties, requiredNames, "method", methodVariant);
        }
        else
        {
            AppendMcpCapabilityExample(sb, indent, server, tool, properties, requiredNames, null, null);
        }

        sb.Append(indent).AppendLine("  rules:");
        if (properties.ContainsKey("method"))
        {
            sb.Append(indent).Append("    - ").AppendLine(FormatYamlScalar(
                $"Use input.method: {tool.Name} for the MCP tool name; use input.request.method only as this tool's argument."));
        }
        if (requiredNames.Count > 0)
        {
            sb.Append(indent).Append("    - ").AppendLine(FormatYamlScalar(
                "Always provide required request arguments: " + string.Join(", ", requiredNames) + "."));
        }
        if (numericRequiredNames.Count > 0)
        {
            sb.Append(indent).Append("    - ").AppendLine(FormatYamlScalar(
                string.Join(", ", numericRequiredNames) + " must resolve to numbers, not strings."));
        }
        foreach (var item in enumValuesByName)
        {
            sb.Append(indent).Append("    - ").AppendLine(FormatYamlScalar(
                $"request.{item.Name} must be one of: {string.Join(", ", item.Values)}."));
        }
    }

    private static void AppendMcpCapabilityExample(
        StringBuilder sb,
        string indent,
        McpServerDiscovery server,
        McpToolInfo tool,
        IReadOnlyDictionary<string, JsonNode?> properties,
        IReadOnlyList<string> requiredNames,
        string? variantArgumentName,
        string? variantArgumentValue)
    {
        var variantSuffix = variantArgumentValue == null ? "" : "_" + SanitizeExampleName(variantArgumentValue);
        sb.Append(indent).Append("    - name: ").AppendLine(FormatYamlScalar("call_" + SanitizeExampleName(tool.Name) + variantSuffix));
        sb.Append(indent).AppendLine("      call:");
        sb.Append(indent).AppendLine("        type: mcp.call");
        sb.Append(indent).AppendLine("        input:");
        sb.Append(indent).Append("          server: ").AppendLine(FormatYamlScalar(server.Name));
        sb.Append(indent).AppendLine("          kind: tool");
        sb.Append(indent).Append("          method: ").AppendLine(FormatYamlScalar(tool.Name));
        if (requiredNames.Count == 0)
        {
            sb.Append(indent).AppendLine("          request: {}");
            return;
        }

        sb.Append(indent).AppendLine("          request:");
        foreach (var name in requiredNames)
        {
            var schema = GetSchemaProperty(properties, name);
            var value = string.Equals(name, variantArgumentName, StringComparison.Ordinal)
                ? FormatYamlScalar(variantArgumentValue ?? "")
                : BuildExampleRequestValue(name, schema);
            sb.Append(indent).Append("            ").Append(name).Append(": ").AppendLine(value);
        }
    }

    private const int MaxCapabilityCardTypeDepth = 3;
    private const int MaxCapabilityCardProperties = 8;

    private static IReadOnlyDictionary<string, JsonNode?> GetJsonSchemaProperties(JsonNode? schema)
    {
        if (schema is not JsonObject obj || obj["properties"] is not JsonObject properties)
            return new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        return properties.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static List<string> GetRequiredPropertyNames(JsonNode? schema)
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

    private static JsonNode? GetSchemaProperty(IReadOnlyDictionary<string, JsonNode?> properties, string name)
        => properties.TryGetValue(name, out var property) ? property : null;

    private static string DescribeJsonSchemaType(JsonNode? schema)
        => DescribeJsonSchemaType(schema, 0);

    private static string DescribeJsonSchemaType(JsonNode? schema, int depth)
    {
        if (schema is not JsonObject obj)
            return "any";

        var enumValues = GetEnumValues(obj);
        if (enumValues.Count > 0)
        {
            var enumPreview = string.Join("|", enumValues.Take(6));
            if (enumValues.Count > 6)
                enumPreview += "|...";
            return $"string enum<{enumPreview}>";
        }

        if (TryDescribeSchemaUnion(obj, "anyOf", depth, out var union)
            || TryDescribeSchemaUnion(obj, "oneOf", depth, out union))
            return union;

        var type = GetJsonSchemaTypeName(obj);
        var typeParts = type?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        if (typeParts.Contains("object", StringComparer.OrdinalIgnoreCase)
            || obj["properties"] is JsonObject)
            return DescribeObjectJsonSchemaType(obj, depth);

        if (typeParts.Contains("array", StringComparer.OrdinalIgnoreCase))
            return DescribeArrayJsonSchemaType(obj, depth);

        if (string.Equals(type, "string", StringComparison.OrdinalIgnoreCase)
            && obj["format"] is JsonValue formatValue
            && formatValue.TryGetValue<string>(out var format)
            && !string.IsNullOrWhiteSpace(format))
            return $"string({format})";

        return string.IsNullOrWhiteSpace(type) ? "any" : type;
    }

    private static bool TryDescribeSchemaUnion(JsonObject obj, string keyword, int depth, out string description)
    {
        description = "";
        if (obj[keyword] is not JsonArray variants || variants.Count == 0)
            return false;

        var parts = variants
            .Take(4)
            .Select(item => DescribeJsonSchemaType(item, depth + 1))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (parts.Count == 0)
            return false;

        if (variants.Count > parts.Count)
            parts.Add("...");
        description = string.Join("|", parts);
        return true;
    }

    private static string DescribeObjectJsonSchemaType(JsonObject obj, int depth)
    {
        var properties = GetJsonSchemaProperties(obj);
        if (properties.Count == 0)
        {
            if (obj["additionalProperties"] is JsonObject additionalSchema)
                return $"object<string, {DescribeJsonSchemaType(additionalSchema, depth + 1)}>";
            return "object";
        }

        if (depth >= MaxCapabilityCardTypeDepth)
            return "object {...}";

        var required = GetRequiredPropertyNames(obj);
        var parts = properties
            .Take(MaxCapabilityCardProperties)
            .Select(kv =>
            {
                var optional = required.Contains(kv.Key, StringComparer.Ordinal) ? "" : "?";
                return $"{kv.Key}{optional}: {DescribeJsonSchemaType(kv.Value, depth + 1)}";
            })
            .ToList();

        if (properties.Count > MaxCapabilityCardProperties)
            parts.Add("...");

        return "object { " + string.Join(", ", parts) + " }";
    }

    private static string DescribeArrayJsonSchemaType(JsonObject obj, int depth)
    {
        if (obj["items"] is JsonObject items)
            return $"array<{DescribeJsonSchemaType(items, depth + 1)}>";

        if (obj["items"] is JsonArray tupleItems)
        {
            var itemTypes = tupleItems
                .Take(4)
                .Select(item => DescribeJsonSchemaType(item, depth + 1))
                .ToList();
            if (tupleItems.Count > itemTypes.Count)
                itemTypes.Add("...");
            return "array<" + string.Join("|", itemTypes) + ">";
        }

        return "array<any>";
    }

    private static string? GetJsonSchemaTypeName(JsonObject obj)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeText))
            return typeText;

        if (obj["type"] is JsonArray typeArray)
        {
            var values = typeArray
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(text => !string.IsNullOrWhiteSpace(text));
            return string.Join("|", values);
        }

        if (obj["enum"] is JsonArray)
            return "string";
        if (obj["const"] != null)
            return "string";

        return null;
    }

    private static List<string> GetEnumValues(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return new List<string>();

        if (obj["enum"] is JsonArray enumArray)
        {
            return enumArray
                .Select(GetJsonScalarText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        var constValue = GetJsonScalarText(obj["const"]);
        return string.IsNullOrWhiteSpace(constValue) ? new List<string>() : new List<string> { constValue };
    }

    private static bool IsNumericJsonSchema(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return false;

        var type = GetJsonSchemaTypeName(obj);
        return type != null
            && (type.Split('|').Contains("number", StringComparer.OrdinalIgnoreCase)
                || type.Split('|').Contains("integer", StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildExampleRequestValue(string name, JsonNode? schema)
    {
        if (schema is JsonObject obj)
        {
            if (obj["default"] != null)
                return FormatYamlValue(obj["default"]);
            if (obj["const"] != null)
                return FormatYamlValue(obj["const"]);

            var enumValues = GetEnumValues(obj);
            if (enumValues.Count > 0)
                return FormatYamlScalar(enumValues[0]);

            if (IsNumericJsonSchema(obj))
            {
                if (string.Equals(name, "page", StringComparison.OrdinalIgnoreCase))
                    return "1";
                if (string.Equals(name, "perPage", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "per_page", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "pageSize", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "limit", StringComparison.OrdinalIgnoreCase))
                    return "30";
            }
        }

        return FormatYamlScalar("${" + BuildInputReference(name) + "}");
    }

    private static string BuildInputReference(string name)
    {
        if (Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return "data.inputs." + name;

        return "data.inputs[" + QuoteDouble(name) + "]";
    }

    private static string? GetJsonScalarText(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;
        if (value.TryGetValue<bool>(out var boolValue))
            return boolValue ? "true" : "false";
        if (value.TryGetValue<int>(out var intValue))
            return intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var longValue))
            return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var doubleValue))
            return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value.ToJsonString();
    }

    private static string FormatYamlValue(JsonNode? node)
    {
        if (node == null)
            return "null";

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
                return FormatYamlScalar(text);
            if (value.TryGetValue<bool>(out var boolValue))
                return boolValue ? "true" : "false";
            if (value.TryGetValue<int>(out var intValue))
                return intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var longValue))
                return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var doubleValue))
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return FormatYamlScalar(node.ToJsonString(PromptJsonOptions));
    }

    private static string FormatYamlScalar(string value)
        => QuoteDouble(value);

    private static string QuoteDouble(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append(@"\r");
                    break;
                case '\n':
                    sb.Append(@"\n");
                    break;
                case '\t':
                    sb.Append(@"\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string SanitizeExampleName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }

        return sb.ToString().Trim('_') is { Length: > 0 } text ? text : "tool";
    }
}
