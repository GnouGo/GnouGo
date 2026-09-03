using System.Text.Json.Nodes;

namespace GnOuGo.Mcp.Core;

/// <summary>
/// Stable, domain-neutral artifact metadata advertised by GnOuGo MCP tools.
/// JSON pointers address tool input or structured-output instance fields, not
/// locations inside the corresponding JSON Schema document.
/// </summary>
public static class McpArtifactContractMetadata
{
    public const string MetaPropertyName = "gnougo";
    public const string ArtifactsPropertyName = "artifacts";
    public const int CurrentVersion = 1;
    public const string WorkspaceDirectoryKind = "workspace.directory";
    public const string RevisionComparisonFilesKind = "revision.comparison.files";
    public const string SessionHandleKind = "session.handle";
    public const string MaterializeMode = "materialize";

    public const string WorkspaceDirectoryProducerProjectRootRelativeJson =
        """{"artifacts":{"version":1,"produces":[{"kind":"workspace.directory","pointer":"/projectRootRelative","mode":"materialize"}]}}""";

    public const string WorkspaceDirectoryConsumerProjectRootJson =
        """{"artifacts":{"version":1,"consumes":[{"kind":"workspace.directory","pointer":"/projectRoot","required":true}]}}""";
}

public sealed record McpProducedArtifact(string Kind, string Pointer, string Mode);

public sealed record McpConsumedArtifact(string Kind, string Pointer, bool Required);

public sealed record McpArtifactContract(
    int Version,
    IReadOnlyList<McpProducedArtifact> Produces,
    IReadOnlyList<McpConsumedArtifact> Consumes);

public sealed record McpArtifactContractValidationResult(
    McpArtifactContract? Contract,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Contract != null && Errors.Count == 0;
    public bool IsDeclared => Contract != null || Errors.Count > 0;
}

public static class McpArtifactContractParser
{
    public static McpArtifactContractValidationResult ParseAndValidate(
        JsonNode? toolMeta,
        JsonNode? inputSchema,
        JsonNode? outputSchema)
    {
        if (toolMeta is not JsonObject meta
            || !meta.TryGetPropertyValue(McpArtifactContractMetadata.MetaPropertyName, out var gnougoNode))
            return new McpArtifactContractValidationResult(null, Array.Empty<string>());

        if (gnougoNode is not JsonObject gnougo)
            return Invalid("gnougo metadata must be an object.");
        if (!gnougo.TryGetPropertyValue(McpArtifactContractMetadata.ArtifactsPropertyName, out var artifactsNode))
            return new McpArtifactContractValidationResult(null, Array.Empty<string>());
        if (artifactsNode is not JsonObject artifacts)
            return Invalid("gnougo.artifacts metadata must be an object.");

        var errors = new List<string>();
        var version = ReadRequiredInt32(artifacts, "version", errors);
        if (version != McpArtifactContractMetadata.CurrentVersion)
        {
            errors.Add(
                $"Artifact contract version must be {McpArtifactContractMetadata.CurrentVersion}; received {version?.ToString() ?? "null"}.");
        }

        var produces = ParseProduces(artifacts["produces"], outputSchema, errors);
        var consumes = ParseConsumes(artifacts["consumes"], inputSchema, errors);
        if (produces.Count == 0 && consumes.Count == 0)
            errors.Add("Artifact contract must declare at least one produced or consumed artifact.");

        var contract = version.HasValue
            ? new McpArtifactContract(version.Value, produces, consumes)
            : null;
        return new McpArtifactContractValidationResult(contract, errors);
    }

    private static McpArtifactContractValidationResult Invalid(string error)
        => new(null, new[] { error });

    private static IReadOnlyList<McpProducedArtifact> ParseProduces(
        JsonNode? node,
        JsonNode? outputSchema,
        List<string> errors)
    {
        if (node == null)
            return Array.Empty<McpProducedArtifact>();
        if (node is not JsonArray array)
        {
            errors.Add("artifacts.produces must be an array.");
            return Array.Empty<McpProducedArtifact>();
        }

        var result = new List<McpProducedArtifact>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                errors.Add($"artifacts.produces[{index}] must be an object.");
                continue;
            }

            var prefix = $"artifacts.produces[{index}]";
            var kind = ReadRequiredString(item, "kind", prefix, errors);
            var pointer = ReadRequiredPointer(item, prefix, errors);
            var mode = ReadRequiredString(item, "mode", prefix, errors);
            if (kind == null || pointer == null || mode == null)
                continue;
            if (!string.Equals(mode, McpArtifactContractMetadata.MaterializeMode, StringComparison.Ordinal))
            {
                errors.Add($"{prefix}.mode '{mode}' is unsupported; use '{McpArtifactContractMetadata.MaterializeMode}'.");
                continue;
            }
            if (!seen.Add(kind + "\u001f" + pointer))
            {
                errors.Add($"{prefix} duplicates produced artifact '{kind}' at '{pointer}'.");
                continue;
            }

            ValidateSchemaPointer(outputSchema, pointer, requireRequiredProperty: true, prefix, errors);
            result.Add(new McpProducedArtifact(kind, pointer, mode));
        }

        return result;
    }

    private static IReadOnlyList<McpConsumedArtifact> ParseConsumes(
        JsonNode? node,
        JsonNode? inputSchema,
        List<string> errors)
    {
        if (node == null)
            return Array.Empty<McpConsumedArtifact>();
        if (node is not JsonArray array)
        {
            errors.Add("artifacts.consumes must be an array.");
            return Array.Empty<McpConsumedArtifact>();
        }

        var result = new List<McpConsumedArtifact>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                errors.Add($"artifacts.consumes[{index}] must be an object.");
                continue;
            }

            var prefix = $"artifacts.consumes[{index}]";
            var kind = ReadRequiredString(item, "kind", prefix, errors);
            var pointer = ReadRequiredPointer(item, prefix, errors);
            var required = ReadRequiredBoolean(item, "required", prefix, errors);
            if (kind == null || pointer == null || !required.HasValue)
                continue;
            if (!seen.Add(kind + "\u001f" + pointer))
            {
                errors.Add($"{prefix} duplicates consumed artifact '{kind}' at '{pointer}'.");
                continue;
            }

            ValidateSchemaPointer(inputSchema, pointer, required.Value, prefix, errors);
            result.Add(new McpConsumedArtifact(kind, pointer, required.Value));
        }

        return result;
    }

    private static int? ReadRequiredInt32(JsonObject source, string propertyName, List<string> errors)
    {
        if (source[propertyName] is JsonValue value && value.TryGetValue<int>(out var parsed))
            return parsed;
        errors.Add($"artifacts.{propertyName} must be an integer.");
        return null;
    }

    private static string? ReadRequiredString(
        JsonObject source,
        string propertyName,
        string prefix,
        List<string> errors)
    {
        if (source[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var parsed)
            && !string.IsNullOrWhiteSpace(parsed))
        {
            return parsed.Trim();
        }

        errors.Add($"{prefix}.{propertyName} must be a non-empty string.");
        return null;
    }

    private static bool? ReadRequiredBoolean(
        JsonObject source,
        string propertyName,
        string prefix,
        List<string> errors)
    {
        if (source[propertyName] is JsonValue value && value.TryGetValue<bool>(out var parsed))
            return parsed;
        errors.Add($"{prefix}.{propertyName} must be a boolean.");
        return null;
    }

    private static string? ReadRequiredPointer(JsonObject source, string prefix, List<string> errors)
    {
        var pointer = ReadRequiredString(source, "pointer", prefix, errors);
        if (pointer == null)
            return null;
        if (pointer.Length <= 1 || pointer[0] != '/' || pointer.EndsWith("/", StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.pointer must be a non-root JSON pointer to an instance field.");
            return null;
        }

        try
        {
            _ = DecodePointer(pointer);
            return pointer;
        }
        catch (FormatException ex)
        {
            errors.Add($"{prefix}.pointer is invalid: {ex.Message}");
            return null;
        }
    }

    private static void ValidateSchemaPointer(
        JsonNode? schema,
        string pointer,
        bool requireRequiredProperty,
        string prefix,
        List<string> errors)
    {
        if (schema is not JsonObject root)
        {
            errors.Add($"{prefix}.pointer '{pointer}' cannot be validated because its schema is unavailable.");
            return;
        }

        var current = root;
        foreach (var segment in DecodePointer(pointer))
        {
            current = ResolveLocalReference(root, current);
            if (current["properties"] is not JsonObject properties
                || properties[segment] is not JsonObject propertySchema)
            {
                errors.Add($"{prefix}.pointer '{pointer}' does not resolve to a schema property.");
                return;
            }

            if (requireRequiredProperty && !IsRequired(current, segment))
            {
                errors.Add($"{prefix}.pointer '{pointer}' must reference a required schema property.");
                return;
            }

            current = propertySchema;
        }

        current = ResolveLocalReference(root, current);
        if (!AllowsString(current))
            errors.Add($"{prefix}.pointer '{pointer}' must resolve to a string-compatible schema.");
    }

    private static JsonObject ResolveLocalReference(JsonObject root, JsonObject schema)
    {
        if (schema["$ref"] is not JsonValue referenceValue
            || !referenceValue.TryGetValue<string>(out var reference)
            || string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return schema;
        }

        JsonNode? current = root;
        foreach (var segment in DecodePointer(reference[1..]))
        {
            current = current is JsonObject obj ? obj[segment] : null;
            if (current == null)
                return schema;
        }

        return current as JsonObject ?? schema;
    }

    private static bool IsRequired(JsonObject schema, string propertyName)
        => schema["required"] is JsonArray required
           && required.Any(item => item is JsonValue value
                                   && value.TryGetValue<string>(out var name)
                                   && string.Equals(name, propertyName, StringComparison.Ordinal));

    private static bool AllowsString(JsonObject schema)
    {
        if (schema["type"] is JsonValue value
            && value.TryGetValue<string>(out var type))
        {
            return string.Equals(type, "string", StringComparison.Ordinal);
        }

        if (schema["type"] is JsonArray types)
        {
            return types.Any(item => item is JsonValue typeValue
                                     && typeValue.TryGetValue<string>(out var type)
                                     && string.Equals(type, "string", StringComparison.Ordinal));
        }

        return schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any(AllowsString)
               || schema["oneOf"] is JsonArray oneOf && oneOf.OfType<JsonObject>().Any(AllowsString);
    }

    private static IReadOnlyList<string> DecodePointer(string pointer)
    {
        if (pointer.Length == 0)
            return Array.Empty<string>();
        if (pointer[0] != '/')
            throw new FormatException("JSON pointers must start with '/'.");

        return pointer[1..].Split('/', StringSplitOptions.None)
            .Select(DecodePointerToken)
            .ToArray();
    }

    private static string DecodePointerToken(string token)
    {
        var result = new System.Text.StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            if (token[index] != '~')
            {
                result.Append(token[index]);
                continue;
            }

            if (index + 1 >= token.Length || token[index + 1] is not ('0' or '1'))
                throw new FormatException("Only '~0' and '~1' escape sequences are allowed.");
            result.Append(token[++index] == '0' ? '~' : '/');
        }

        return result.ToString();
    }
}
