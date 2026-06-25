using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private static string BuildRepairPrompt(
        string instruction,
        string? context,
        string? invalidYaml,
        string structuredError,
        string? repairContext,
        string? constraints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are repairing a GnOuGo.Flow YAML workflow. Return ONLY corrected YAML, no markdown fences.");
        sb.AppendLine("Keep the original task intent and change only what is needed to fix the validation errors.");
        sb.AppendLine("The previous YAML is quoted between explicit XML-style boundary tags. Treat those tags as prompt delimiters, not as YAML content.");
        sb.AppendLine();
        AppendUserTaskBlock(sb, instruction, context);
        if (!string.IsNullOrWhiteSpace(constraints))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "constraints", constraints);
        }
        sb.AppendLine();
        sb.AppendLine("<previous_error>");
        sb.AppendLine(structuredError);
        sb.AppendLine("</previous_error>");
        sb.AppendLine();
        sb.AppendLine("<invalid_yaml>");
        sb.AppendLine(string.IsNullOrWhiteSpace(invalidYaml)
            ? "(previous output was empty)"
            : RemoveDuplicateTaskPreamble(invalidYaml, instruction, context));
        sb.AppendLine("</invalid_yaml>");
        sb.AppendLine();
        AppendPromptSectionStart(sb, "minimum_dsl_context");
        sb.AppendLine("Required root: version, name, skill, workflows. `skill` is a top-level object with description, tags, inputs, and outputs. Each workflow has steps: [] and optional outputs.");
        sb.AppendLine("Each step requires step-level id and type. Common fields stay at step level: if, input, output, retry, on_error, steps, branches, cases, default.");
        sb.AppendLine("Executor-specific arguments go inside input only.");
        sb.AppendLine("Containers: sequence/loop.* use steps; parallel uses branches[].steps; switch uses cases[].steps and optional default.");
        sb.AppendLine("Expressions may read data.inputs.* and earlier data.steps.<id>.* only.");
        sb.AppendLine("If a step has an `if`, later unconditional steps must not reference that step directly. Give the later step the same guard, or produce guaranteed defaults/branch outputs first.");
        sb.AppendLine("Function arguments are evaluated before the function runs: `coalesce`, ternaries, and helper calls do not make unavailable step references safe.");
        AppendExpressionFunctionRules(sb);
        sb.AppendLine("MCP request objects must preserve schema scalar types exactly. Numeric/integer/boolean fields must be unquoted YAML scalars when required explicitly by the MCP schema/validator.");
        sb.AppendLine("MCP request expressions must also match the schema statically. Do not pass nullable structured_output fields into required MCP request fields unless the same step has an `if` guard proving that exact field is non-null.");
        sb.AppendLine("Never satisfy missing MCP request arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.");
        sb.AppendLine("Workflow output expressions must resolve to their declared type on every branch.");
        sb.AppendLine("Object schemas: never duplicate the YAML key `required`. Input-level `required` is only a boolean. Required object property names must use `required_properties`, not a second `required` key.");
        AppendPromptSectionEnd(sb, "minimum_dsl_context");
        sb.AppendLine();
        AppendStructuredOutputStrictSchemaRules(sb);
        if (structuredError.Contains("Duplicate key required", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            AppendPromptSectionStart(sb, "duplicate_required_key_fix");
            sb.AppendLine("The parser error `Duplicate key required` means the same YAML mapping contains two `required:` keys.");
            sb.AppendLine("Keep at most one input-level `required: true|false` boolean.");
            sb.AppendLine("Move required object property names to `required_properties:`.");
            sb.AppendLine("Invalid: type: object + required: true + properties: ... + required: [name]");
            sb.AppendLine("Valid: type: object + required: true + properties: ... + required_properties: [name]");
            AppendPromptSectionEnd(sb, "duplicate_required_key_fix");
        }
        if (!string.IsNullOrWhiteSpace(repairContext))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "relevant_repair_context", repairContext);
        }
        sb.AppendLine();
        sb.AppendLine("Fix the issues above and generate a corrected YAML.");
        return sb.ToString();
    }

    private static string BuildUserTaskBlock(string instruction, string? context)
    {
        var sb = new StringBuilder();
        AppendUserTaskBlock(sb, instruction, context);
        return sb.ToString().TrimEnd();
    }

    private static void AppendUserTaskBlock(StringBuilder sb, string instruction, string? context)
    {
        AppendPromptSectionStart(sb, "task");
        sb.AppendLine("<user_prompt>");
        sb.AppendLine(instruction);
        sb.AppendLine("</user_prompt>");

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine("<user_context>");
            sb.AppendLine(context);
            sb.AppendLine("</user_context>");
        }

        AppendPromptSectionEnd(sb, "task");
    }

    private static void AppendPromptSection(StringBuilder sb, string tagName, string? content)
    {
        AppendPromptSectionStart(sb, tagName);

        if (!string.IsNullOrEmpty(content))
            sb.AppendLine(content.TrimEnd());

        AppendPromptSectionEnd(sb, tagName);
    }

    private static void AppendPromptSectionStart(StringBuilder sb, string tagName)
        => sb.AppendLine($"<{tagName}>");

    private static void AppendPromptSectionEnd(StringBuilder sb, string tagName)
        => sb.AppendLine($"</{tagName}>");

    private static void AppendStructuredOutputStrictSchemaRules(StringBuilder sb)
    {
        AppendPromptSection(sb, "structured_output_strict_schema_rules", """
        When generating any `llm.call.input.structured_output` or `mcp.call.input.structured_output`, use strict JSON Schema only:
        - Never use `type: any`; JSON Schema has no `any` type. Choose a real type, use `anyOf` with explicit variants, or avoid structured_output if no stable contract exists.
        - The root schema must be an object: `type: object`.
        - Every object schema, including nested objects and array item objects, must declare `type: object`, a `properties` object, `required` listing EVERY key from `properties`, and `additionalProperties: false`.
        - Optional fields must still appear in `required`; model optionality with `anyOf: [{ type: string }, { type: "null" }]` or another explicit nullable type.
        - Every array schema must declare `items`.
        - Do not generate bare object schemas such as `type: object` without `properties`, `required`, and `additionalProperties: false`.
        - Do not use `required_properties` inside JSON Schema structured_output; that keyword is only for GnOuGo workflow input/output schemas.

        Valid strict structured_output example:
        structured_output:
          strict: true
          schema_inline:
            type: object
            properties:
              issues:
                type: array
                items:
                  type: object
                  properties:
                    number: { type: integer }
                    title: { type: string }
                    severity:
                      anyOf:
                        - type: string
                        - type: "null"
                  required: [number, title, severity]
                  additionalProperties: false
              summary: { type: string }
            required: [issues, summary]
            additionalProperties: false
        """);
    }

    private static string RemoveDuplicateTaskPreamble(string invalidYaml, string instruction, string? context)
    {
        if (string.IsNullOrWhiteSpace(invalidYaml))
            return invalidYaml;

        var trimmed = invalidYaml.Trim();
        var rootMatch = RootYamlKeyRegex().Match(trimmed);

        if (!rootMatch.Success || rootMatch.Index == 0)
            return trimmed;

        var preamble = trimmed[..rootMatch.Index].Trim();
        if (!IsDuplicateTaskText(preamble, instruction, context))
            return trimmed;

        return trimmed[rootMatch.Index..].TrimStart();
    }

    private static string RemoveMarkdownFenceLines(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("```", StringComparison.Ordinal))
            return value;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return string.Join("\n", lines.Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)));
    }

    private static bool IsDuplicateTaskText(string candidate, string instruction, string? context)
    {
        var normalizedCandidate = NormalizePromptText(candidate);
        if (normalizedCandidate.Length < 80)
            return false;

        var normalizedTask = NormalizePromptText(string.IsNullOrWhiteSpace(context)
            ? instruction
            : instruction + "\n" + context);

        if (normalizedTask.Contains(normalizedCandidate, StringComparison.Ordinal))
            return true;

        var candidateWords = PromptWordRegex()
            .Matches(normalizedCandidate)
            .Select(match => match.Value)
            .ToArray();

        if (candidateWords.Length < 20)
            return false;

        var taskWords = PromptWordRegex()
            .Matches(normalizedTask)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (taskWords.Count == 0)
            return false;

        var matched = candidateWords.Count(taskWords.Contains);
        return matched / (double)candidateWords.Length >= 0.85;
    }

    private static string NormalizePromptText(string value)
    {
        var normalized = WhitespaceRegex().Replace(value, " ").Trim().ToLowerInvariant();
        return normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex PromptWordRegex();

    [GeneratedRegex(@"(?m)^(version|name|skill|workflows|inputs|outputs)\s*:")]
    private static partial Regex RootYamlKeyRegex();

    private static string BuildMinimalRepairContext(
        StepExecutorRegistry registry,
        HashSet<string>? allowedTypes,
        string? invalidYaml,
        Exception exception,
        IReadOnlyList<McpServerDiscovery>? discovered)
    {
        var selectedTypes = ExtractRepairStepTypes(registry, invalidYaml, exception.Message);
        if (selectedTypes.Count == 0)
            selectedTypes.UnionWith(ExtractKnownStepTypesFromYaml(registry, invalidYaml));

        if (allowedTypes != null)
            selectedTypes.IntersectWith(allowedTypes);

        var availableTypes = allowedTypes ?? registry.RegisteredTypes.ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Available step type names:");
        sb.AppendLine(string.Join(", ", availableTypes.OrderBy(t => t, StringComparer.Ordinal)));

        var expressionTypeDoc = BuildExpressionTypeMismatchRepairContext(exception.Message);
        if (!string.IsNullOrWhiteSpace(expressionTypeDoc))
        {
            sb.AppendLine();
            sb.AppendLine("Expression type mismatch repair guidance:");
            sb.AppendLine(expressionTypeDoc);
        }

        if (selectedTypes.Count > 0)
        {
            var snippets = registry.GetDslSnippets(selectedTypes).ToList();
            if (snippets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("DSL snippets for failed/referenced step types:");
                sb.AppendLine(RemoveMarkdownFenceLines(string.Join("\n", snippets)));
            }
        }

        var mcpDoc = BuildMinimalMcpRepairContext(invalidYaml, selectedTypes, discovered);
        if (!string.IsNullOrWhiteSpace(mcpDoc))
        {
            sb.AppendLine();
            sb.AppendLine("MCP docs for failed/referenced calls:");
            sb.AppendLine(mcpDoc);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? BuildExpressionTypeMismatchRepairContext(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return null;

        var lower = errorMessage.ToLowerInvariant();
        if (!lower.Contains(ErrorCodes.ExprTypeMismatch.ToLowerInvariant(), StringComparison.Ordinal)
            && !lower.Contains("mcp_request_expr_type_mismatch", StringComparison.Ordinal)
            && !(lower.Contains("resolves to", StringComparison.Ordinal) && lower.Contains("contract requires", StringComparison.Ordinal)))
            return null;

        var fields = ExtractJsonStringValues(errorMessage, "field")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var invalidExpressions = ExtractJsonStringValues(errorMessage, "invalid_path")
            .Where(static expression => expression.Contains("${", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        var sb = new StringBuilder();
        var mcpFields = fields
            .Where(static field => field.StartsWith("input.request", StringComparison.Ordinal))
            .ToArray();
        if (lower.Contains("mcp_request_expr_type_mismatch", StringComparison.Ordinal) || mcpFields.Length > 0)
        {
            sb.AppendLine("MCP request expressions must match the discovered MCP input_schema before runtime.");
            if (mcpFields.Length > 0)
                sb.AppendLine("Affected MCP request field(s): " + string.Join(", ", mcpFields.Select(static field => $"`{field}`")));
            sb.AppendLine("If a field is required as string/number/boolean, do not map a nullable source such as `string|null` directly into it.");
            sb.AppendLine("Prefer fixing the upstream `structured_output` schema so the field is non-null whenever the MCP call should run.");
            sb.AppendLine("Alternatively add an `if` guard on the same mcp.call step that proves the exact expression is non-null, for example `if: \"${data.steps.decide.json.commandName != null}\"`.");
            sb.AppendLine("If the decision can be false, skip the mcp.call or split the workflow into a guarded branch instead of passing null.");
        }
        else
        {
            sb.AppendLine("Workflow output expressions must match their declared output contract types exactly.");
            var outputFields = fields
                .Where(static field => field.StartsWith("outputs.", StringComparison.Ordinal))
                .ToArray();
            if (outputFields.Length > 0)
                sb.AppendLine("Affected output field(s): " + string.Join(", ", outputFields.Select(static field => $"`{field}`")));
        }

        if (lower.Contains("resolves to boolean", StringComparison.Ordinal) && lower.Contains("requires string", StringComparison.Ordinal))
        {
            sb.AppendLine("A comparison/predicate expression returns boolean, so it cannot satisfy a string output contract.");
            sb.AppendLine("Do not assign `${a == b}`, `${a != b}`, `${contains(...)}`, `${exists(...)}`, or other predicates to string outputs.");
            sb.AppendLine("For string outputs such as classification/status/level/severity, return a string-valued field or quoted string literal.");
            sb.AppendLine("If deriving the value from an MCP/LLM response, add a normalization step with `structured_output`, then map `data.steps.<normalizer>.json.<field>`.");
            sb.AppendLine("Invalid string output: `expr: \"${data.steps.classify.json.classification == 'bug'}\"`.");
            sb.AppendLine("Valid string output: `expr: \"${data.steps.classify.json.classification}\"`.");
        }
        else
        {
            sb.AppendLine("Replace incompatible expressions with values that resolve to the declared output type.");
            sb.AppendLine("Use predicate/comparison expressions only for boolean outputs or conditional fields such as `if` and `switch.when`.");
        }

        if (invalidExpressions.Length > 0)
            sb.AppendLine("Invalid expression(s) to replace: " + string.Join(", ", invalidExpressions.Select(static expression => $"`{expression}`")));

        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ExtractJsonStringValues(string text, string propertyName)
    {
        var values = new List<string>();
        foreach (Match match in Regex.Matches(
            text,
            $"\"{Regex.Escape(propertyName)}\":\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.CultureInvariant))
        {
            var raw = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var decoded = DecodeJsonStringFragment(raw);
            if (!string.IsNullOrWhiteSpace(decoded))
                values.Add(decoded);
        }

        return values;
    }

    private static string DecodeJsonStringFragment(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
            return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current != '\\' || i + 1 >= value.Length)
            {
                sb.Append(current);
                continue;
            }

            var escaped = value[++i];
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                    sb.Append(escaped);
                    break;
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u' when i + 4 < value.Length && TryParseHexQuad(value.AsSpan(i + 1, 4), out var codepoint):
                    sb.Append((char)codepoint);
                    i += 4;
                    break;
                default:
                    sb.Append(escaped);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool TryParseHexQuad(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var ch in text)
        {
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => -1
            };
            if (digit < 0)
                return false;
            value = value * 16 + digit;
        }

        return true;
    }

    private sealed record StepRepairInfo(string Type, IReadOnlyList<string> AncestorTypes);

    private static HashSet<string> ExtractRepairStepTypes(StepExecutorRegistry registry, string? invalidYaml, string errorMessage)
    {
        var knownSteps = BuildStepRepairLookup(invalidYaml);
        var selectedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stepId in ExtractErrorStepIds(errorMessage))
        {
            if (!knownSteps.TryGetValue(stepId, out var info))
                continue;

            if (registry.Has(info.Type))
                selectedTypes.Add(info.Type);
            foreach (var ancestorType in info.AncestorTypes)
            {
                if (registry.Has(ancestorType))
                    selectedTypes.Add(ancestorType);
            }
        }

        foreach (var stepType in ExtractQuotedStepTypes(errorMessage))
        {
            if (registry.Has(stepType))
                selectedTypes.Add(stepType);
        }

        return selectedTypes;
    }

    private static HashSet<string> ExtractKnownStepTypesFromYaml(StepExecutorRegistry registry, string? invalidYaml)
    {
        var selectedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in BuildStepRepairLookup(invalidYaml).Values)
        {
            if (registry.Has(info.Type))
                selectedTypes.Add(info.Type);
        }
        return selectedTypes;
    }

    private static Dictionary<string, StepRepairInfo> BuildStepRepairLookup(string? invalidYaml)
    {
        var lookup = new Dictionary<string, StepRepairInfo>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(invalidYaml))
            return lookup;

        try
        {
            var doc = Parsing.WorkflowParser.Parse(invalidYaml);
            foreach (var workflow in doc.Workflows.Values)
                AddStepRepairInfo(workflow.Steps, Array.Empty<string>(), lookup);
        }
        catch
        {
            return lookup;
        }

        return lookup;
    }

    private static void AddStepRepairInfo(
        IEnumerable<StepDef> steps,
        IReadOnlyList<string> ancestorTypes,
        Dictionary<string, StepRepairInfo> lookup)
    {
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Id))
                lookup[step.Id] = new StepRepairInfo(step.Type, ancestorTypes);

            var childAncestors = ancestorTypes.Concat(new[] { step.Type }).ToArray();
            if (step.Steps != null)
                AddStepRepairInfo(step.Steps, childAncestors, lookup);
            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                    AddStepRepairInfo(branch.Steps, childAncestors, lookup);
            }
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                    AddStepRepairInfo(@case.Steps, childAncestors, lookup);
            }
            if (step.Default != null)
                AddStepRepairInfo(step.Default, childAncestors, lookup);
        }
    }

    private static IEnumerable<string> ExtractErrorStepIds(string errorMessage)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(errorMessage, @"step '([^']+)'", RegexOptions.IgnoreCase))
            ids.Add(match.Groups[1].Value);
        foreach (Match match in Regex.Matches(errorMessage, @"""step"":""([^""]+)""", RegexOptions.IgnoreCase))
            ids.Add(match.Groups[1].Value);
        foreach (Match match in Regex.Matches(errorMessage, @"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)", RegexOptions.IgnoreCase))
            ids.Add(match.Groups[1].Value);
        return ids;
    }

    private static IEnumerable<string> ExtractQuotedStepTypes(string errorMessage)
    {
        foreach (Match match in Regex.Matches(errorMessage, @"Step type '([^']+)'", RegexOptions.IgnoreCase))
            yield return match.Groups[1].Value;
        foreach (Match match in Regex.Matches(errorMessage, @"type '([^']+)'", RegexOptions.IgnoreCase))
            yield return match.Groups[1].Value;
    }

    private static string? BuildMinimalMcpRepairContext(
        string? invalidYaml,
        HashSet<string> selectedTypes,
        IReadOnlyList<McpServerDiscovery>? discovered)
    {
        if (discovered == null || discovered.Count == 0 || string.IsNullOrWhiteSpace(invalidYaml))
            return null;

        WorkflowDocument doc;
        try
        {
            doc = Parsing.WorkflowParser.Parse(invalidYaml);
        }
        catch
        {
            return null;
        }

        var calls = doc.Workflows.Values
            .SelectMany(workflow => EnumerateSteps(workflow.Steps))
            .Where(step => step.Type == "mcp.call" && step.Input is JsonObject)
            .Select(step =>
            {
                var input = (JsonObject)step.Input!;
                var server = ReadMcpCallInputString(step, "server");
                var method = ReadMcpCallInputString(step, "method");
                var methods = new List<string>();
                if (!string.IsNullOrWhiteSpace(method) && !method.Contains("${", StringComparison.Ordinal))
                    methods.Add(method);
                if (input["methods"] is JsonArray methodArray)
                {
                    foreach (var item in methodArray)
                    {
                        var methodName = item is JsonValue methodValue
                            && methodValue.TryGetValue<string>(out var parsedMethodName)
                                ? parsedMethodName
                                : null;
                        if (!string.IsNullOrWhiteSpace(methodName) && !methodName.Contains("${", StringComparison.Ordinal))
                            methods.Add(methodName);
                    }
                }
                return (StepId: step.Id, Server: server, Methods: methods.Distinct(StringComparer.Ordinal).ToArray(), Request: input["request"]?.DeepClone());
            })
            .Where(call => !string.IsNullOrWhiteSpace(call.Server))
            .DistinctBy(call => (call.StepId, call.Server, Methods: string.Join('\u001f', call.Methods)))
            .ToList();

        if (calls.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("Use exact MCP server and method names. Tool arguments must be nested under `input.request`.");
        sb.AppendLine("Correct direct tool call shape:");
        sb.AppendLine("  type: mcp.call");
        sb.AppendLine("  input: { server: <exact-server>, kind: tool, method: <exact-tool>, request: { ... } }");
        sb.AppendLine("For every listed input_schema_json, copy all required request properties into input.request with the exact schema name and scalar type.");
        sb.AppendLine("If a required numeric/integer/boolean MCP property is missing, add an explicit YAML scalar value; do not use an expression string, cast, empty value, fake value, or data.env fallback.");
        sb.AppendLine("When repairing one MCP call, re-check every MCP call in the YAML so earlier schema fixes are preserved.");
        sb.AppendLine("Available MCP servers: " + string.Join(", ", discovered.Select(static server => server.Name).OrderBy(static name => name, StringComparer.Ordinal)));
        foreach (var call in calls)
        {
            var server = discovered.FirstOrDefault(s => string.Equals(s.Name, call.Server, StringComparison.Ordinal));
            if (server == null)
            {
                sb.AppendLine();
                sb.Append("- ");
                if (!string.IsNullOrWhiteSpace(call.StepId))
                    sb.Append($"Step `{call.StepId}` references ");
                sb.AppendLine($"unknown MCP server `{call.Server}`.");
                sb.AppendLine("  This server name is not configured/discovered. Use one exact server name from the available MCP servers list.");
                if (call.Request != null)
                    AppendJsonBlock(sb, "  ", "invalid_request", call.Request);

                var likelyServers = SelectLikelyMcpRepairServers(discovered, call.StepId, call.Server, call.Methods);
                if (likelyServers.Count > 0)
                {
                    sb.AppendLine("  Likely matching discovered server(s):");
                    foreach (var likelyServer in likelyServers)
                        AppendMcpServerRepairDetails(sb, likelyServer, call.Methods, includeUnknownMethods: false);
                }
                continue;
            }

            AppendMcpServerRepairDetails(sb, server, call.Methods, includeUnknownMethods: true, call.StepId, call.Request);
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static void AppendMcpServerRepairDetails(
        StringBuilder sb,
        McpServerDiscovery server,
        IReadOnlyList<string> methods,
        bool includeUnknownMethods,
        string? stepId = null,
        JsonNode? request = null)
    {
        if (stepId != null || request != null)
        {
            sb.AppendLine();
            sb.Append("- ");
            if (!string.IsNullOrWhiteSpace(stepId))
                sb.Append($"Step `{stepId}` references ");
            sb.Append(server.Name);
            if (!string.IsNullOrWhiteSpace(server.Description))
                sb.Append($": {server.Description}");
            sb.AppendLine();
        }
        else
        {
            sb.Append("  - ");
            sb.Append(server.Name);
            if (!string.IsNullOrWhiteSpace(server.Description))
                sb.Append($": {server.Description}");
            sb.AppendLine();
        }

        var availableToolNames = server.Tools.Select(static tool => tool.Name).ToArray();
        sb.AppendLine($"  Available tools on `{server.Name}`: {string.Join(", ", availableToolNames)}");

        var unknownMethods = methods
            .Where(method => !availableToolNames.Contains(method, StringComparer.Ordinal))
            .ToArray();
        if (includeUnknownMethods && unknownMethods.Length > 0)
            sb.AppendLine($"  Unknown requested method(s): {string.Join(", ", unknownMethods)}");

        if (request != null)
            AppendJsonBlock(sb, "  ", "invalid_request", request);

        var tools = unknownMethods.Length > 0 || methods.Count == 0
            ? server.Tools.ToList()
            : server.Tools.Where(tool => methods.Contains(tool.Name, StringComparer.Ordinal)).ToList();

        foreach (var tool in tools.Take(12))
        {
            sb.Append($"  - {tool.Name}");
            if (!string.IsNullOrWhiteSpace(tool.Description))
                sb.Append($": {tool.Description}");
            sb.AppendLine();
            if (tool.InputSchema != null)
                AppendJsonBlock(sb, "    ", "input_schema", tool.InputSchema);
            if (tool.OutputSchema != null)
                AppendJsonBlock(sb, "    ", "output_schema", tool.OutputSchema);
            if (tool.ExampleResponse != null)
                AppendJsonBlock(sb, "    ", "example_response", tool.ExampleResponse);
        }

        if (tools.Count > 12)
            sb.AppendLine($"  ... {tools.Count - 12} additional tool(s) omitted from repair context.");
    }

    private static IReadOnlyList<McpServerDiscovery> SelectLikelyMcpRepairServers(
        IReadOnlyList<McpServerDiscovery> discovered,
        string? stepId,
        string? requestedServer,
        IReadOnlyList<string> requestedMethods)
    {
        var queryTokens = TokenizeMcpRepairQuery(
            string.Join(' ', new[] { stepId, requestedServer }.Where(static text => !string.IsNullOrWhiteSpace(text)))
            + " "
            + string.Join(' ', requestedMethods));

        if (queryTokens.Count == 0)
            return discovered.Count <= 3 ? discovered : Array.Empty<McpServerDiscovery>();

        return discovered
            .Select(server => new
            {
                Server = server,
                Score = ScoreMcpServerRepairMatch(server, queryTokens)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Server.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Server)
            .ToArray();
    }

    private static int ScoreMcpServerRepairMatch(McpServerDiscovery server, IReadOnlySet<string> queryTokens)
    {
        var haystacks = new List<string?>
        {
            server.Name,
            server.Description
        };
        foreach (var tool in server.Tools)
        {
            haystacks.Add(tool.Name);
            haystacks.Add(tool.Description);
        }

        var score = 0;
        foreach (var token in queryTokens)
        {
            foreach (var haystack in haystacks)
            {
                if (string.IsNullOrWhiteSpace(haystack))
                    continue;
                if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                    score++;
            }
        }

        return score;
    }

    private static IReadOnlySet<string> TokenizeMcpRepairQuery(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(static match => match.Value)
            .Where(static token => token is not "mcp" and not "call" and not "server" and not "tool")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendJsonBlock(StringBuilder sb, string indent, string label, JsonNode node)
    {
        sb.Append(indent);
        sb.Append(label);
        sb.Append("_json: ");
        sb.AppendLine(node.ToJsonString(PromptJsonOptions));
    }

    private static string BuildStructuredPlanError(Exception ex, int attempt)
        => WorkflowPlanDiagnostics.BuildStructuredPlanError(ex, attempt);

    private static string? TryExtractMissingMcpServerName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = Regex.Match(message, @"MCP server '([^']+)' not found", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
