using System.Text.Json.Nodes;

namespace GnOuGo.Mcp.Core;

/// <summary>
/// Stable, domain-neutral composition metadata advertised by GnOuGo MCP tools.
/// A complete operation may encapsulate lower-level tools or prompts from the
/// same MCP server so planners do not schedule both the wrapper and its phases.
/// </summary>
public static class McpCapabilityCompositionMetadata
{
    public const string MetaPropertyName = "gnougo";
    public const string CompositionPropertyName = "composition";
    public const string CompleteOperationKind = "complete_operation";
    public const int CurrentVersion = 1;
}

public sealed record McpEncapsulatedCapability(string Kind, string Method);

public sealed record McpCapabilityComposition(
    int Version,
    string Kind,
    IReadOnlyList<McpEncapsulatedCapability> Encapsulates);

public sealed record McpCapabilityCompositionValidationResult(
    McpCapabilityComposition? Contract,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Contract != null && Errors.Count == 0;
    public bool IsDeclared => Contract != null || Errors.Count > 0;
}

public static class McpCapabilityCompositionParser
{
    private const int MaximumEncapsulatedCapabilities = 32;

    public static McpCapabilityCompositionValidationResult ParseAndValidate(JsonNode? toolMeta)
    {
        if (toolMeta is not JsonObject meta
            || !meta.TryGetPropertyValue(McpCapabilityCompositionMetadata.MetaPropertyName, out var gnougoNode))
        {
            return new McpCapabilityCompositionValidationResult(null, Array.Empty<string>());
        }

        if (gnougoNode is not JsonObject gnougo)
            return Invalid("gnougo metadata must be an object.");
        if (!gnougo.TryGetPropertyValue(McpCapabilityCompositionMetadata.CompositionPropertyName, out var compositionNode))
            return new McpCapabilityCompositionValidationResult(null, Array.Empty<string>());
        if (compositionNode is not JsonObject composition)
            return Invalid("gnougo.composition metadata must be an object.");

        var errors = new List<string>();
        var version = ReadRequiredInt32(composition, "version", errors);
        if (version != McpCapabilityCompositionMetadata.CurrentVersion)
        {
            errors.Add(
                $"Composition contract version must be {McpCapabilityCompositionMetadata.CurrentVersion}; received {version?.ToString() ?? "null"}.");
        }

        var kind = ReadRequiredString(composition, "kind", "composition", errors);
        if (kind != null
            && !string.Equals(kind, McpCapabilityCompositionMetadata.CompleteOperationKind, StringComparison.Ordinal))
        {
            errors.Add(
                $"composition.kind '{kind}' is unsupported; use '{McpCapabilityCompositionMetadata.CompleteOperationKind}'.");
        }

        var encapsulates = ParseEncapsulates(composition["encapsulates"], errors);
        var contract = version.HasValue && kind != null
            ? new McpCapabilityComposition(version.Value, kind, encapsulates)
            : null;
        return new McpCapabilityCompositionValidationResult(contract, errors);
    }

    private static IReadOnlyList<McpEncapsulatedCapability> ParseEncapsulates(
        JsonNode? node,
        List<string> errors)
    {
        if (node is not JsonArray array)
        {
            errors.Add("composition.encapsulates must be an array.");
            return Array.Empty<McpEncapsulatedCapability>();
        }
        if (array.Count == 0)
            errors.Add("composition.encapsulates must contain at least one capability.");
        if (array.Count > MaximumEncapsulatedCapabilities)
        {
            errors.Add($"composition.encapsulates must contain at most {MaximumEncapsulatedCapabilities} capabilities.");
            return Array.Empty<McpEncapsulatedCapability>();
        }

        var result = new List<McpEncapsulatedCapability>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                errors.Add($"composition.encapsulates[{index}] must be an object.");
                continue;
            }

            var prefix = $"composition.encapsulates[{index}]";
            var kind = ReadRequiredString(item, "kind", prefix, errors);
            var method = ReadRequiredString(item, "method", prefix, errors);
            if (kind == null || method == null)
                continue;
            if (kind is not ("tool" or "prompt"))
            {
                errors.Add($"{prefix}.kind '{kind}' is unsupported; use 'tool' or 'prompt'.");
                continue;
            }
            if (!seen.Add(kind + "\u001f" + method))
            {
                errors.Add($"{prefix} duplicates capability '{kind}/{method}'.");
                continue;
            }

            result.Add(new McpEncapsulatedCapability(kind, method));
        }

        return result;
    }

    private static int? ReadRequiredInt32(JsonObject source, string propertyName, List<string> errors)
    {
        if (source[propertyName] is JsonValue value && value.TryGetValue<int>(out var parsed))
            return parsed;
        errors.Add($"composition.{propertyName} must be an integer.");
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
            && !string.IsNullOrWhiteSpace(parsed)
            && parsed.Length <= 160)
        {
            return parsed.Trim();
        }

        errors.Add($"{prefix}.{propertyName} must be a non-empty string of at most 160 characters.");
        return null;
    }

    private static McpCapabilityCompositionValidationResult Invalid(string error)
        => new(null, new[] { error });
}
