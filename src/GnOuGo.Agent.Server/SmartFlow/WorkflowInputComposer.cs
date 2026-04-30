using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed record WorkflowInputSchema(
    string? AgentName,
    IReadOnlyList<WorkflowInputFieldSchema> Fields,
    string? ErrorMessage = null)
{
    private static readonly HashSet<string> PromptInputNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "task",
        "prompt",
        "query",
        "request",
        "input",
        "message"
    };

    public bool IsPromptOnly => Fields.Count is 0 || IsPromptOnlyShape(Fields);

    private static bool IsPromptOnlyShape(IReadOnlyList<WorkflowInputFieldSchema> fields)
    {
        var requiredFields = fields.Where(static field => field.Required).ToArray();
        return requiredFields.Length is 1
               && PromptInputNames.Contains(requiredFields[0].Name)
               && WorkflowInputComposer.IsStringType(requiredFields[0].Type)
               && fields.All(static field => field.Required || field.DefaultValue is not null);
    }
}

public sealed record WorkflowInputFieldSchema(
    string Name,
    string Type,
    bool Required,
    string? Description = null,
    JsonNode? DefaultValue = null,
    IReadOnlyList<WorkflowInputFieldSchema>? Properties = null,
    WorkflowInputFieldSchema? Items = null)
{
    public IReadOnlyList<WorkflowInputFieldSchema> PropertyList => Properties ?? Array.Empty<WorkflowInputFieldSchema>();
}

public static class WorkflowInputComposer
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static WorkflowInputSchema FromWorkflow(string? agentName, WorkflowDef workflow)
    {
        var fields = workflow.Inputs?
            .Select(static pair => FromInputDef(pair.Key, pair.Value))
            .ToArray()
            ?? Array.Empty<WorkflowInputFieldSchema>();

        return new WorkflowInputSchema(agentName, fields);
    }

    public static bool TryBuildInputs(
        WorkflowInputSchema schema,
        IReadOnlyDictionary<string, string> values,
        out JsonObject inputs,
        out IReadOnlyList<string> errors)
    {
        inputs = new JsonObject();
        var errorList = new List<string>();

        foreach (var field in schema.Fields)
        {
            if (TryBuildField(field, field.Name, values, errorList, out var value, out var hasValue))
            {
                if (hasValue)
                    inputs[field.Name] = value;
            }
        }

        errors = errorList;
        return errorList.Count == 0;
    }

    public static string BuildPromptSummary(WorkflowInputSchema schema, JsonObject inputs)
    {
        if (schema.IsPromptOnly)
        {
            var promptField = schema.Fields.FirstOrDefault(static field => field.Required && IsStringType(field.Type))
                              ?? schema.Fields.FirstOrDefault();
            var promptName = promptField?.Name ?? "prompt";
            return inputs[promptName]?.GetValue<string>()?.Trim() ?? string.Empty;
        }

        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(schema.AgentName)
            ? "Workflow input"
            : $"Workflow input for {schema.AgentName}";
        sb.AppendLine(title);
        sb.AppendLine();

        foreach (var field in schema.Fields)
        {
            if (!inputs.ContainsKey(field.Name))
                continue;

            var displayValue = IsSensitive(field.Name)
                ? "••••••••"
                : FormatNodeForDisplay(inputs[field.Name]);
            sb.AppendLine($"- {field.Name}: {displayValue}");
        }

        return sb.ToString().Trim();
    }

    public static string FormatNodeForEditor(JsonNode? node, string type)
    {
        if (node is null)
            return string.Empty;

        if (IsStringType(type) && node is JsonValue stringValue && stringValue.TryGetValue(out string? text))
            return text ?? string.Empty;

        if (IsBooleanType(type) && node is JsonValue boolValue && boolValue.TryGetValue(out bool boolean))
            return boolean ? "true" : "false";

        if (IsNumberType(type))
            return node.ToJsonString();

        return node.ToJsonString(IndentedJsonOptions);
    }

    public static bool IsStringType(string? type)
        => string.Equals(type, "string", StringComparison.OrdinalIgnoreCase);

    public static bool IsBooleanType(string? type)
        => string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "bool", StringComparison.OrdinalIgnoreCase);

    public static bool IsNumberType(string? type)
        => string.Equals(type, "number", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "int", StringComparison.OrdinalIgnoreCase);

    public static bool IsComplexType(string? type)
        => string.Equals(type, "array", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "object", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "dictionary", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "map", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "any", StringComparison.OrdinalIgnoreCase);

    public static bool IsSensitive(string name)
        => name.Contains("key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("password", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static WorkflowInputFieldSchema FromInputDef(string name, InputDef def)
    {
        var properties = def.Properties?
            .Select(pair => FromInputDef(pair.Key, pair.Value))
            .ToArray();

        return new WorkflowInputFieldSchema(
            name,
            def.Type,
            def.Required,
            def.Description,
            ConvertDefaultToNode(def.Default),
            properties,
            def.Items is null ? null : FromInputDef("item", def.Items));
    }

    private static bool TryBuildField(
        WorkflowInputFieldSchema field,
        string path,
        IReadOnlyDictionary<string, string> values,
        List<string> errors,
        out JsonNode? value,
        out bool hasValue)
    {
        value = null;
        hasValue = false;

        if (IsObjectWithEditableProperties(field))
        {
            var obj = new JsonObject();
            foreach (var property in field.PropertyList)
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!TryBuildField(property, propertyPath, values, errors, out var propertyValue, out var propertyHasValue))
                    continue;

                if (propertyHasValue)
                    obj[property.Name] = propertyValue;
            }

            if (obj.Count > 0)
            {
                value = obj;
                hasValue = true;
                return true;
            }

            if (field.Required)
                errors.Add($"Input '{field.Name}' is required.");

            return true;
        }

        values.TryGetValue(path, out var raw);
        raw ??= string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (field.Required)
                errors.Add($"Input '{field.Name}' is required.");
            return true;
        }

        if (IsStringType(field.Type))
        {
            value = JsonValue.Create(raw);
            hasValue = true;
            return true;
        }

        if (IsBooleanType(field.Type))
        {
            if (TryParseBoolean(raw, out var boolean))
            {
                value = JsonValue.Create(boolean);
                hasValue = true;
            }
            else
            {
                errors.Add($"Input '{field.Name}' must be a boolean value.");
            }

            return true;
        }

        if (IsNumberType(field.Type))
        {
            if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                value = JsonValue.Create(number);
                hasValue = true;
            }
            else
            {
                errors.Add($"Input '{field.Name}' must be a number.");
            }

            return true;
        }

        if (TryParseStructuredValue(raw, out var parsed, out var parseError))
        {
            value = parsed;
            hasValue = true;
        }
        else if (string.Equals(field.Type, "any", StringComparison.OrdinalIgnoreCase))
        {
            value = JsonValue.Create(raw);
            hasValue = true;
        }
        else
        {
            errors.Add($"Input '{field.Name}' must be valid JSON or YAML. {parseError}");
        }

        return true;
    }

    private static bool IsObjectWithEditableProperties(WorkflowInputFieldSchema field)
        => string.Equals(field.Type, "object", StringComparison.OrdinalIgnoreCase)
           && field.PropertyList.Count > 0;

    private static bool TryParseBoolean(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "1":
                value = true;
                return true;
            case "false":
            case "no":
            case "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseStructuredValue(string raw, out JsonNode? node, out string? error)
    {
        node = null;
        error = null;

        try
        {
            node = JsonNode.Parse(raw);
            return true;
        }
        catch (JsonException jsonEx)
        {
            try
            {
                node = ParseYamlToJsonNode(raw);
                return true;
            }
            catch (Exception yamlEx) when (yamlEx is YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                error = $"JSON: {jsonEx.Message} YAML: {yamlEx.Message}";
                return false;
            }
        }
    }

    private static JsonNode? ParseYamlToJsonNode(string raw)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(raw));

        if (stream.Documents.Count == 0)
            return null;

        return ConvertYamlNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertYamlNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ConvertYamlScalar(scalar.Value),
            YamlSequenceNode sequence => ConvertYamlSequence(sequence),
            YamlMappingNode mapping => ConvertYamlMapping(mapping),
            _ => JsonValue.Create(node.ToString())
        };
    }

    private static JsonNode? ConvertYamlScalar(string? value)
    {
        if (value is null || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) || value is "~")
            return null;

        if (bool.TryParse(value, out var boolean))
            return JsonValue.Create(boolean);

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);

        return JsonValue.Create(value);
    }

    private static JsonArray ConvertYamlSequence(YamlSequenceNode sequence)
    {
        var array = new JsonArray();
        foreach (var child in sequence.Children)
            array.Add(ConvertYamlNode(child));
        return array;
    }

    private static JsonObject ConvertYamlMapping(YamlMappingNode mapping)
    {
        var obj = new JsonObject();
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = keyNode is YamlScalarNode scalarKey
                ? scalarKey.Value
                : keyNode.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                obj[key] = ConvertYamlNode(valueNode);
        }

        return obj;
    }

    private static JsonNode? ConvertDefaultToNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            short s16 => JsonValue.Create(s16),
            byte b8 => JsonValue.Create(b8),
            uint ui => JsonValue.Create(ui),
            ulong ul => JsonValue.Create(ul),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            IDictionary<string, object?> dict => ConvertDictionaryDefault(dict),
            System.Collections.IDictionary dict => ConvertDictionaryDefault(dict),
            System.Collections.IEnumerable enumerable when value is not string => ConvertEnumerableDefault(enumerable),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonObject ConvertDictionaryDefault(IDictionary<string, object?> dict)
    {
        var obj = new JsonObject();
        foreach (var (key, item) in dict)
            obj[key] = ConvertDefaultToNode(item);
        return obj;
    }

    private static JsonObject ConvertDictionaryDefault(System.Collections.IDictionary dict)
    {
        var obj = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                obj[key] = ConvertDefaultToNode(entry.Value);
        }

        return obj;
    }

    private static JsonArray ConvertEnumerableDefault(System.Collections.IEnumerable enumerable)
    {
        var array = new JsonArray();
        foreach (var item in enumerable)
            array.Add(ConvertDefaultToNode(item));
        return array;
    }

    private static string FormatNodeForDisplay(JsonNode? node)
    {
        if (node is null)
            return "null";

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? text))
                return text ?? string.Empty;
            if (value.TryGetValue(out bool boolean))
                return boolean ? "true" : "false";
            return node.ToJsonString();
        }

        return $"`{node.ToJsonString(IndentedJsonOptions)}`";
    }
}



