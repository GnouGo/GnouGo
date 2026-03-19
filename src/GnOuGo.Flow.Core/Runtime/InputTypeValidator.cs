using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Validates a runtime JsonNode value against a rich <see cref="InputDef"/> schema.
/// Returns a list of human-readable error messages (empty = valid).
/// </summary>
public static class InputTypeValidator
{
    private const int MaxDepth = 16;

    /// <summary>
    /// Validates all declared inputs of a workflow against the provided runtime values.
    /// </summary>
    public static List<string> Validate(WorkflowDef? workflow, JsonObject inputs)
    {
        var errors = new List<string>();
        var definitions = workflow?.Inputs;
        if (definitions == null || definitions.Count == 0)
            return errors;

        foreach (var (name, def) in definitions)
        {
            var value = inputs.ContainsKey(name) ? inputs[name] : null;

            // Required check
            if (def.Required && value == null)
            {
                errors.Add($"Input '{name}' is required but was not provided.");
                continue;
            }

            // Skip validation for absent optional inputs
            if (value == null)
                continue;

            ValidateNode(value, def, name, errors, 0);
        }

        return errors;
    }

    /// <summary>
    /// Validates a single JsonNode against an InputDef schema.
    /// </summary>
    public static void ValidateNode(JsonNode? node, InputDef def, string path, List<string> errors, int depth)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"'{path}': validation exceeded maximum depth ({MaxDepth}).");
            return;
        }

        if (node == null)
            return; // null already handled by required check at call site

        switch (def.Type.ToLowerInvariant())
        {
            case "any":
                break; // anything is valid

            case "string":
                if (node is not JsonValue sv || !sv.TryGetValue(out string? _))
                    errors.Add($"'{path}': expected string, got {DescribeKind(node)}.");
                break;

            case "number":
                if (!IsNumber(node))
                    errors.Add($"'{path}': expected number, got {DescribeKind(node)}.");
                break;

            case "boolean":
                if (node is not JsonValue bv || !bv.TryGetValue(out bool _))
                    errors.Add($"'{path}': expected boolean, got {DescribeKind(node)}.");
                break;

            case "array":
                ValidateArray(node, def, path, errors, depth);
                break;

            case "object":
                ValidateObject(node, def, path, errors, depth);
                break;

            case "dictionary":
                ValidateDictionary(node, def, path, errors, depth);
                break;
        }
    }

    private static void ValidateArray(JsonNode node, InputDef def, string path, List<string> errors, int depth)
    {
        if (node is not JsonArray arr)
        {
            errors.Add($"'{path}': expected array, got {DescribeKind(node)}.");
            return;
        }

        if (def.Items == null)
            return; // no element schema — accept any array

        for (int i = 0; i < arr.Count; i++)
        {
            ValidateNode(arr[i], def.Items, $"{path}[{i}]", errors, depth + 1);
        }
    }

    private static void ValidateObject(JsonNode node, InputDef def, string path, List<string> errors, int depth)
    {
        if (node is not JsonObject obj)
        {
            errors.Add($"'{path}': expected object, got {DescribeKind(node)}.");
            return;
        }

        // Required properties
        if (def.RequiredProperties != null)
        {
            foreach (var reqProp in def.RequiredProperties)
            {
                if (!obj.ContainsKey(reqProp))
                    errors.Add($"'{path}': missing required property '{reqProp}'.");
            }
        }

        // Validate typed properties
        if (def.Properties != null)
        {
            foreach (var (propName, propDef) in def.Properties)
            {
                if (obj.ContainsKey(propName))
                {
                    ValidateNode(obj[propName], propDef, $"{path}.{propName}", errors, depth + 1);
                }
                else if (propDef.Required && def.RequiredProperties == null)
                {
                    // If no RequiredProperties list is provided, fall back to per-property Required flag
                    errors.Add($"'{path}': missing required property '{propName}'.");
                }
            }
        }

        // Validate additional (untyped) properties against AdditionalProperties schema
        if (def.AdditionalProperties != null)
        {
            var knownKeys = def.Properties?.Keys.ToHashSet() ?? new HashSet<string>();
            foreach (var (key, val) in obj)
            {
                if (!knownKeys.Contains(key))
                    ValidateNode(val, def.AdditionalProperties, $"{path}.{key}", errors, depth + 1);
            }
        }
    }

    private static void ValidateDictionary(JsonNode node, InputDef def, string path, List<string> errors, int depth)
    {
        if (node is not JsonObject obj)
        {
            errors.Add($"'{path}': expected dictionary (object), got {DescribeKind(node)}.");
            return;
        }

        if (def.AdditionalProperties == null)
            return; // no value schema — accept any object

        foreach (var (key, val) in obj)
        {
            ValidateNode(val, def.AdditionalProperties, $"{path}['{key}']", errors, depth + 1);
        }
    }

    // ── Helpers ──

    private static bool IsNumber(JsonNode node)
    {
        if (node is not JsonValue jv) return false;
        return jv.TryGetValue(out int _)
            || jv.TryGetValue(out long _)
            || jv.TryGetValue(out double _)
            || jv.TryGetValue(out float _)
            || jv.TryGetValue(out decimal _);
    }

    private static string DescribeKind(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "array",
        JsonObject => "object",
        JsonValue jv when jv.TryGetValue(out bool _) => "boolean",
        JsonValue jv when jv.TryGetValue(out string? _) => "string",
        JsonValue jv when IsNumber(jv) => "number",
        _ => node.GetType().Name
    };
}

