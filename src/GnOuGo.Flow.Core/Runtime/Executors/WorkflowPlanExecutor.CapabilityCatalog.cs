using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const int CapabilitySchemaMaxDepth = 4;
    private const int CapabilitySelectorMaxValues = 64;
    private const int CapabilityDescriptionMaxCharacters = 512;
    private const int CapabilityCatalogMaxCharacters = 256_000;

    private sealed record CapabilityInventoryOperation(string Id, string Description, bool Required);
    private sealed record CapabilityInventoryConstraint(string Id, string Description, bool Required);
    private sealed record CapabilityInventory(
        IReadOnlyList<CapabilityInventoryOperation> Operations,
        IReadOnlyList<CapabilityInventoryConstraint> Constraints);

    private sealed record CapabilityCatalogEntry(
        string Id,
        string Resolution,
        string? Server,
        string? Kind,
        string Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings);

    private sealed record CapabilityCatalog(
        IReadOnlyList<CapabilityCatalogEntry> Entries,
        string Text);

    private sealed record SelectorVariant(IReadOnlyList<CapabilityRequestBinding> Bindings);

    private static CapabilityCatalog BuildSchemaAwareCapabilityCatalog(
        IReadOnlyList<McpServerDiscovery> discovered,
        IReadOnlySet<string> nativeStepTypes)
    {
        var pending = new List<(string Resolution, string? Server, string? Kind, string Method, string Description, IReadOnlyList<CapabilityRequestBinding> Bindings, string Card)>();

        foreach (var native in nativeStepTypes.OrderBy(static value => value, StringComparer.Ordinal))
        {
            pending.Add(("native", null, null, native, $"Native Flow step type {native}.", Array.Empty<CapabilityRequestBinding>(),
                $"resolution=native method={native}"));
        }

        foreach (var server in discovered.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            foreach (var prompt in server.Prompts.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                var description = LimitCapabilityDescription(prompt.Description);
                var arguments = prompt.Arguments is { Count: > 0 }
                    ? string.Join(", ", prompt.Arguments.Select(static argument => argument.Required
                        ? $"{argument.Name}:string(required)"
                        : $"{argument.Name}:string"))
                    : "none";
                pending.Add(("mcp", server.Name, "prompt", prompt.Name, description, Array.Empty<CapabilityRequestBinding>(),
                    $"resolution=mcp server={server.Name} kind=prompt method={prompt.Name} description={description} arguments=[{arguments}]"));
            }

            foreach (var tool in server.Tools.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                var description = LimitCapabilityDescription(tool.Description);
                var arguments = BuildCompactArgumentSummary(tool.InputSchema);
                var variants = ExtractSelectorVariants(tool.InputSchema);
                if (variants.Count == 0)
                {
                    pending.Add(("mcp", server.Name, "tool", tool.Name, description, Array.Empty<CapabilityRequestBinding>(),
                        $"resolution=mcp server={server.Name} kind=tool method={tool.Name} description={description} arguments=[{arguments}]"));
                    continue;
                }

                foreach (var variant in variants.OrderBy(static item => CanonicalizeBindings(item.Bindings), StringComparer.Ordinal))
                {
                    var bindings = FormatBindingsCompact(variant.Bindings);
                    pending.Add(("mcp", server.Name, "tool", tool.Name, description, variant.Bindings,
                        $"resolution=mcp server={server.Name} kind=tool method={tool.Name} description={description} arguments=[{arguments}] request_bindings=[{bindings}]"));
                }
            }
        }

        var ordered = pending
            .OrderBy(static item => item.Resolution, StringComparer.Ordinal)
            .ThenBy(static item => item.Server, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.Method, StringComparer.Ordinal)
            .ThenBy(static item => CanonicalizeBindings(item.Bindings), StringComparer.Ordinal)
            .ToArray();
        var entries = new List<CapabilityCatalogEntry>(ordered.Length);
        var text = new StringBuilder();
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var id = $"cap_{index + 1:D6}";
            var line = $"{id} {item.Card}";
            if (text.Length + line.Length + Environment.NewLine.Length > CapabilityCatalogMaxCharacters)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    "The schema-aware capability catalog exceeds the safe inference limit.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_catalog",
                        ["reason"] = "catalog_too_large",
                        ["maximum_characters"] = CapabilityCatalogMaxCharacters,
                        ["entry_count"] = ordered.Length
                    });
            }

            text.AppendLine(line);
            entries.Add(new CapabilityCatalogEntry(id, item.Resolution, item.Server, item.Kind, item.Method, item.Bindings));
        }

        return new CapabilityCatalog(entries, text.ToString());
    }

    private static string LimitCapabilityDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description)
            ? "No description supplied."
            : string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= CapabilityDescriptionMaxCharacters
            ? normalized
            : normalized[..CapabilityDescriptionMaxCharacters];
    }

    private static string BuildCompactArgumentSummary(JsonNode? inputSchema)
    {
        if (inputSchema is not JsonObject root)
            return "unknown";
        var values = new List<string>();
        CollectArgumentSummaries(root, string.Empty, 0, GetRequiredPropertyNames(root), values);
        return values.Count == 0 ? "none" : string.Join(", ", values);
    }

    private static void CollectArgumentSummaries(
        JsonObject schema,
        string pointer,
        int depth,
        IReadOnlyCollection<string> required,
        List<string> values)
    {
        if (depth >= CapabilitySchemaMaxDepth || schema["properties"] is not JsonObject properties)
            return;
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;
            var path = pointer + "/" + EncodeJsonPointerToken(property.Key);
            var type = ReadCompactSchemaType(propertySchema);
            values.Add($"{path}:{type}{(required.Contains(property.Key) ? "(required)" : string.Empty)}");
            CollectArgumentSummaries(propertySchema, path, depth + 1, GetRequiredPropertyNames(propertySchema), values);
        }
    }

    private static string ReadCompactSchemaType(JsonObject schema)
    {
        if (schema["type"] is JsonValue value && value.TryGetValue<string>(out var type) && !string.IsNullOrWhiteSpace(type))
            return type;
        if (schema["type"] is JsonArray types)
            return string.Join('|', types.Select(static node => node?.GetValue<string>()).Where(static item => !string.IsNullOrWhiteSpace(item)));
        if (schema.ContainsKey("const") || schema["enum"] is JsonArray)
            return "scalar";
        if (schema["oneOf"] is JsonArray)
            return "oneOf";
        if (schema["anyOf"] is JsonArray)
            return "anyOf";
        return "unknown";
    }

    private static IReadOnlyList<SelectorVariant> ExtractSelectorVariants(JsonNode? inputSchema)
    {
        if (inputSchema is not JsonObject root)
            return Array.Empty<SelectorVariant>();
        var variants = new List<SelectorVariant>();
        CollectSelectorVariants(root, string.Empty, 0, variants);
        return variants
            .GroupBy(static variant => CanonicalizeBindings(variant.Bindings), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void CollectSelectorVariants(
        JsonObject schema,
        string pointer,
        int depth,
        List<SelectorVariant> variants)
    {
        if (depth > CapabilitySchemaMaxDepth)
            return;

        CollectDiscriminatorVariants(schema["discriminator"] as JsonObject, pointer, variants);
        CollectComposedBranchVariants(schema["oneOf"] as JsonArray, pointer, depth, variants);
        CollectComposedBranchVariants(schema["anyOf"] as JsonArray, pointer, depth, variants);

        if (schema["properties"] is not JsonObject properties || depth == CapabilitySchemaMaxDepth)
            return;
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;
            var propertyPointer = pointer + "/" + EncodeJsonPointerToken(property.Key);
            if (IsSensitiveSelectorPath(propertyPointer))
                continue;
            var scalarValues = ReadDocumentedScalarValues(propertySchema, propertyPointer);
            foreach (var value in scalarValues)
            {
                variants.Add(new SelectorVariant([
                    new CapabilityRequestBinding(propertyPointer, value?.DeepClone())
                ]));
            }
            CollectSelectorVariants(propertySchema, propertyPointer, depth + 1, variants);
        }
    }

    private static void CollectDiscriminatorVariants(
        JsonObject? discriminator,
        string pointer,
        List<SelectorVariant> variants)
    {
        var propertyName = discriminator?["propertyName"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(propertyName))
            return;
        var path = pointer + "/" + EncodeJsonPointerToken(propertyName);
        if (IsSensitiveSelectorPath(path) || discriminator?["mapping"] is not JsonObject mapping)
            return;
        if (mapping.Count > CapabilitySelectorMaxValues)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                $"A capability discriminator at '{path}' exceeds the supported value limit.",
                details: new JsonObject
                {
                    ["phase"] = "capability_catalog",
                    ["reason"] = "selector_value_limit_exceeded",
                    ["selector_path"] = path,
                    ["maximum_values"] = CapabilitySelectorMaxValues
                });
        }
        foreach (var entry in mapping.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            variants.Add(new SelectorVariant([
                new CapabilityRequestBinding(path, JsonValue.Create(entry.Key))
            ]));
        }
    }

    private static void CollectComposedBranchVariants(
        JsonArray? branches,
        string pointer,
        int depth,
        List<SelectorVariant> variants)
    {
        if (branches == null)
            return;
        foreach (var branchNode in branches)
        {
            if (branchNode is not JsonObject branch)
                continue;
            var dimensions = new List<(string Path, IReadOnlyList<JsonNode?> Values)>();
            CollectBranchSelectorDimensions(branch, pointer, depth, dimensions);
            var combinations = new List<List<CapabilityRequestBinding>> { new() };
            foreach (var dimension in dimensions)
            {
                if ((long)combinations.Count * dimension.Values.Count > 4096)
                {
                    throw new WorkflowRuntimeException(
                        ErrorCodes.CapabilityPreflightInferenceFailed,
                        "A composed capability schema expands to too many selector combinations.",
                        details: new JsonObject
                        {
                            ["phase"] = "capability_catalog",
                            ["reason"] = "catalog_too_large",
                            ["selector_path"] = dimension.Path
                        });
                }
                combinations = combinations.SelectMany(existing => dimension.Values.Select(value =>
                {
                    var next = new List<CapabilityRequestBinding>(existing)
                    {
                        new(dimension.Path, value?.DeepClone())
                    };
                    return next;
                })).ToList();
            }
            variants.AddRange(combinations.Where(static bindings => bindings.Count > 0).Select(static bindings => new SelectorVariant(bindings)));
        }
    }

    private static void CollectBranchSelectorDimensions(
        JsonObject schema,
        string pointer,
        int depth,
        List<(string Path, IReadOnlyList<JsonNode?> Values)> dimensions)
    {
        if (depth > CapabilitySchemaMaxDepth)
            return;
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (property.Value is not JsonObject propertySchema)
                    continue;
                var propertyPointer = pointer + "/" + EncodeJsonPointerToken(property.Key);
                if (IsSensitiveSelectorPath(propertyPointer))
                    continue;
                var values = ReadDocumentedScalarValues(propertySchema, propertyPointer);
                if (values.Count > 0)
                    dimensions.Add((propertyPointer, values));
                CollectBranchSelectorDimensions(propertySchema, propertyPointer, depth + 1, dimensions);
            }
        }
    }

    private static IReadOnlyList<JsonNode?> ReadDocumentedScalarValues(JsonObject schema, string path)
    {
        var values = new List<JsonNode?>();
        if (schema.ContainsKey("const") && IsJsonScalar(schema["const"]))
            values.Add(schema["const"]?.DeepClone());
        if (schema["enum"] is JsonArray enumValues)
        {
            var scalarValues = enumValues.Where(IsJsonScalar).ToArray();
            if (scalarValues.Length > CapabilitySelectorMaxValues)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    $"A capability selector at '{path}' exceeds the supported value limit.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_catalog",
                        ["reason"] = "selector_value_limit_exceeded",
                        ["selector_path"] = path,
                        ["maximum_values"] = CapabilitySelectorMaxValues
                    });
            }
            values.AddRange(scalarValues.Select(static value => value?.DeepClone()));
        }
        return values
            .GroupBy(static value => CanonicalScalar(value), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static bool AlternativeBindingsMatchSchema(CapabilityAlternative alternative, McpServerDiscovery server)
    {
        if (alternative.RequestBindings.Count == 0)
            return true;
        if (alternative.Kind != "tool")
            return false;
        var tool = server.Tools.FirstOrDefault(candidate => string.Equals(candidate.Name, alternative.Method, StringComparison.Ordinal));
        if (tool == null)
            return false;
        var documented = ExtractSelectorVariants(tool.InputSchema)
            .SelectMany(static variant => variant.Bindings)
            .GroupBy(static binding => binding.Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => group.Select(binding => CanonicalScalar(binding.Value)).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        return alternative.RequestBindings.All(binding => documented.TryGetValue(binding.Path, out var values)
                                                           && values.Contains(CanonicalScalar(binding.Value)));
    }

    private static bool IsJsonScalar(JsonNode? node) => node == null || node is JsonValue;

    private static bool IsValidJsonPointer(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '~')
                continue;
            if (index + 1 >= path.Length || path[index + 1] is not ('0' or '1'))
                return false;
            index++;
        }
        return true;
    }

    private static string EncodeJsonPointerToken(string token)
        => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string DecodeJsonPointerToken(string token)
        => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static string CanonicalScalar(JsonNode? value) => value?.ToJsonString() ?? "null";

    private static string CanonicalizeBindings(IReadOnlyList<CapabilityRequestBinding> bindings)
        => string.Join("|", bindings.OrderBy(static binding => binding.Path, StringComparer.Ordinal)
            .Select(static binding => binding.Path + "=" + CanonicalScalar(binding.Value)));

    private static string FormatBindingsCompact(IReadOnlyList<CapabilityRequestBinding> bindings)
        => string.Join(",", bindings.OrderBy(static binding => binding.Path, StringComparer.Ordinal)
            .Select(static binding => binding.Path + "=" + CanonicalScalar(binding.Value)));

    private static bool IsSensitiveSelectorPath(string path)
    {
        var lastToken = DecodeJsonPointerToken(path.Split('/').Last()).Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return lastToken.Contains("secret", StringComparison.Ordinal)
               || lastToken.Contains("token", StringComparison.Ordinal)
               || lastToken.Contains("password", StringComparison.Ordinal)
               || lastToken.Contains("authorization", StringComparison.Ordinal)
               || lastToken.Contains("credential", StringComparison.Ordinal)
               || lastToken.Contains("apikey", StringComparison.Ordinal);
    }

    private static JsonArray BuildRequestBindingsJson(IReadOnlyList<CapabilityRequestBinding> bindings)
    {
        var array = new JsonArray();
        foreach (var binding in bindings.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            array.Add((JsonNode)new JsonObject
            {
                ["path"] = binding.Path,
                ["value"] = binding.Value?.DeepClone()
            });
        }
        return array;
    }

    private static bool RequestContainsLiteralBindings(JsonNode? request, IReadOnlyList<CapabilityRequestBinding> bindings)
    {
        if (bindings.Count == 0)
            return true;
        if (request is not JsonObject)
            return false;
        foreach (var binding in bindings)
        {
            if (!TryResolveJsonPointer(request, binding.Path, out var actual)
                || !JsonNode.DeepEquals(actual, binding.Value)
                || IsDynamicExpressionNode(actual))
                return false;
        }
        return true;
    }

    private static bool TryResolveJsonPointer(JsonNode root, string pointer, out JsonNode? value)
    {
        value = root;
        foreach (var token in pointer.Split('/').Skip(1).Select(DecodeJsonPointerToken))
        {
            if (value is JsonObject obj && obj.TryGetPropertyValue(token, out value))
                continue;
            if (value is JsonArray array && int.TryParse(token, out var index) && index >= 0 && index < array.Count)
            {
                value = array[index];
                continue;
            }
            value = null;
            return false;
        }
        return true;
    }

    private static bool IsDynamicExpressionNode(JsonNode? node)
        => node is JsonValue value
           && value.TryGetValue<string>(out var text)
           && (text.Contains("${{", StringComparison.Ordinal)
               || text.Contains("{{", StringComparison.Ordinal)
               || text.StartsWith("$", StringComparison.Ordinal));
}
