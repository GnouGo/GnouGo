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
        sb.AppendLine("If a string field must contain JSON text, prefer a YAML literal block (`|`) so nested quotes remain valid YAML.");
        sb.AppendLine("For `mcp.call` single-tool outputs, access `data.steps.<id>.response.<field>` only when that field is documented in `output_schema` or `example_response` above. Otherwise the response is opaque: use `json(data.steps.<id>.response)` or normalize it with `llm.call` + `structured_output`.");
        return sb.ToString().TrimEnd();
    }
}
