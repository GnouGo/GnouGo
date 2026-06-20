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
        sb.AppendLine("Object schemas: never duplicate the YAML key `required`. Input-level `required` is only a boolean. Required object property names must use `required_properties`, not a second `required` key.");
        AppendPromptSectionEnd(sb, "minimum_dsl_context");
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
        if (discovered == null || discovered.Count == 0 || !selectedTypes.Contains("mcp.call") || string.IsNullOrWhiteSpace(invalidYaml))
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
                var server = input["server"]?.GetValue<string>();
                var method = input["method"]?.GetValue<string>();
                return (Server: server, Method: method);
            })
            .Where(call => !string.IsNullOrWhiteSpace(call.Server))
            .Distinct()
            .ToList();

        if (calls.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var call in calls)
        {
            var server = discovered.FirstOrDefault(s => string.Equals(s.Name, call.Server, StringComparison.Ordinal));
            if (server == null)
                continue;

            sb.Append("- ");
            sb.Append(server.Name);
            if (!string.IsNullOrWhiteSpace(server.Description))
                sb.Append($": {server.Description}");
            sb.AppendLine();

            var tools = server.Tools
                .Where(tool => string.IsNullOrWhiteSpace(call.Method) || string.Equals(tool.Name, call.Method, StringComparison.Ordinal))
                .ToList();
            foreach (var tool in tools)
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
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static void AppendJsonBlock(StringBuilder sb, string indent, string label, JsonNode node)
    {
        sb.Append(indent);
        sb.Append(label);
        sb.Append("_json: ");
        sb.AppendLine(node.ToJsonString(PromptJsonOptions));
    }

    private static string BuildStructuredPlanError(Exception ex, int attempt)
    {
        var message = ex.Message.Trim();
        var lower = message.ToLowerInvariant();

        var errorCode = "VALIDATION_ERROR";
        if (lower.Contains("mcp_server_unknown"))
            errorCode = "MCP_SERVER_UNKNOWN";
        else if (lower.Contains("mcp_method_unknown"))
            errorCode = "MCP_METHOD_UNKNOWN";
        else if (lower.Contains("mcp_server_not_found") || lower.Contains("mcp server") && lower.Contains("not found"))
            errorCode = ErrorCodes.McpServerNotFound;
        else if (lower.Contains("missing required field 'workflows'"))
            errorCode = "MISSING_ROOT_KEY_WORKFLOWS";
        else if (lower.Contains("missing required field 'version'"))
            errorCode = "MISSING_ROOT_KEY_VERSION";
        else if (lower.Contains("missing required field 'name'"))
            errorCode = "MISSING_ROOT_KEY_NAME";
        else if (lower.Contains("skill_required") || lower.Contains("top-level 'skill'"))
            errorCode = "MISSING_ROOT_KEY_SKILL";
        else if (lower.Contains("step_type_unknown"))
            errorCode = "UNKNOWN_STEP_TYPE";
        else if (lower.Contains("missing_steps") || lower.Contains("missing_branches") || lower.Contains("missing_cases"))
            errorCode = "INVALID_CONTAINER_SHAPE";
        else if (lower.Contains("step_reference_not_available") || lower.Contains("step_reference_unknown") || lower.Contains("semantic_mapping_error"))
            errorCode = "SEMANTIC_MAPPING_ERROR";
        else if (lower.Contains("opaque_response_deep_access"))
            errorCode = "OPAQUE_RESPONSE_DEEP_ACCESS";
        else if (lower.Contains("step_output_property_unknown"))
            errorCode = "STEP_OUTPUT_PROPERTY_UNKNOWN";
        else if (lower.Contains("yaml"))
            errorCode = "YAML_PARSE_ERROR";
        else if (lower.Contains("not allowed by policy") || lower.Contains("denied by policy"))
            errorCode = "POLICY_ERROR";
        else if (lower.Contains("exceeds limit"))
            errorCode = "LIMIT_ERROR";

        return $"attempt={attempt}; code={errorCode}; message={message}";
    }

    private static string? TryExtractMissingMcpServerName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = Regex.Match(message, @"MCP server '([^']+)' not found", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
