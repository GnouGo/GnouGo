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

    private sealed record CapabilityInventoryOperation(
        string Id,
        string Description,
        bool Required,
        string ExecutionKind,
        string ExternalEffectKind);
    private sealed record CapabilityInventoryConstraint(string Id, string Description, bool Required);
    private sealed record CapabilityInventoryIncompleteReason(string Id, string Description);
    private sealed record CapabilityInventory(
        bool Complete,
        IReadOnlyList<CapabilityInventoryOperation> Operations,
        IReadOnlyList<CapabilityInventoryConstraint> Constraints,
        IReadOnlyList<CapabilityInventoryIncompleteReason> IncompleteReasons);

    private sealed record CapabilityCatalogEntry(
        string Id,
        string Resolution,
        string? Server,
        string? Kind,
        string Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings,
        string Card,
        IReadOnlyList<CapabilitySchemaField> RequiredInputs,
        IReadOnlyList<CapabilitySchemaField> Outputs,
        McpArtifactContract? ArtifactContract);

    private sealed record CapabilitySchemaField(
        string Path,
        string Type,
        string Description);

    private sealed record CapabilityMatchingIssue(
        string OperationId,
        string Description,
        bool Required,
        string Status,
        string Reason,
        IReadOnlyList<string> CandidateCatalogIds);

    private sealed record CapabilityOperationMatch(
        CapabilityInventoryOperation Operation,
        string Status,
        string Reason,
        IReadOnlyList<string> CatalogIds,
        IReadOnlyList<string> CandidateCatalogIds);

    private sealed record CapabilityConstraintMatch(
        CapabilityInventoryConstraint Constraint,
        string Status,
        string Reason,
        IReadOnlyList<string> DeniedCatalogIds,
        IReadOnlyList<string> CandidateCatalogIds);

    private sealed record CapabilityMatchingEvaluation(
        IReadOnlyList<CapabilityOperationMatch> OperationMatches,
        IReadOnlyList<CapabilityConstraintMatch> ConstraintMatches,
        IReadOnlyList<CapabilityMatchingIssue> Issues,
        bool ContractValid);

    private sealed record CapabilityCatalog(
        IReadOnlyList<CapabilityCatalogEntry> Entries,
        string Text);

    private sealed record SelectorVariant(IReadOnlyList<CapabilityRequestBinding> Bindings);

    private static CapabilityCatalog BuildSchemaAwareCapabilityCatalog(
        IReadOnlyList<McpServerDiscovery> discovered,
        IReadOnlySet<string> nativeStepTypes,
        IReadOnlyList<McpServerDiscovery>? completeDiscovery = null)
    {
        var pending = new List<(string Resolution, string? Server, string? Kind, string Method, string Description, IReadOnlyList<CapabilityRequestBinding> Bindings, string Card, IReadOnlyList<CapabilitySchemaField> RequiredInputs, IReadOnlyList<CapabilitySchemaField> Outputs, McpArtifactContract? ArtifactContract)>();

        foreach (var native in nativeStepTypes.OrderBy(static value => value, StringComparer.Ordinal))
        {
            pending.Add(("native", null, null, native, $"Native Flow step type {native}.", Array.Empty<CapabilityRequestBinding>(),
                $"resolution=native method={native}", Array.Empty<CapabilitySchemaField>(), Array.Empty<CapabilitySchemaField>(), null));
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
                    $"resolution=mcp server={server.Name} kind=prompt method={prompt.Name} description={description} arguments=[{arguments}]",
                    Array.Empty<CapabilitySchemaField>(), Array.Empty<CapabilitySchemaField>(), null));
            }

            foreach (var tool in server.Tools.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                var description = LimitCapabilityDescription(tool.Description);
                var arguments = BuildCompactArgumentSummary(tool.InputSchema, includeDescriptions: true);
                var outputs = BuildCompactOutputSummary(tool.OutputSchema);
                var requiredInputs = BuildCapabilitySchemaFields(tool.InputSchema, requiredOnly: true);
                var outputFields = BuildCapabilitySchemaFields(tool.OutputSchema, requiredOnly: false);
                var artifactContract = GetValidatedMcpArtifactContract(tool, server.Name);
                var artifactSummary = FormatArtifactContractSummary(artifactContract);
                var variants = ExtractSelectorVariants(tool.InputSchema);
                pending.Add(("mcp", server.Name, "tool", tool.Name, description, Array.Empty<CapabilityRequestBinding>(),
                    $"resolution=mcp server={server.Name} kind=tool method={tool.Name} description={description} arguments=[{arguments}] outputs=[{outputs}]{artifactSummary}",
                    requiredInputs, outputFields, artifactContract));

                foreach (var variant in variants.OrderBy(static item => CanonicalizeBindings(item.Bindings), StringComparer.Ordinal))
                {
                    var bindings = FormatBindingsCompact(variant.Bindings);
                    pending.Add(("mcp", server.Name, "tool", tool.Name, description, variant.Bindings,
                        $"resolution=mcp server={server.Name} kind=tool method={tool.Name} variant_of={server.Name}/tool/{tool.Name} request_bindings=[{bindings}]",
                        requiredInputs, outputFields, artifactContract));
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
        var rendered = ordered.Select((item, index) => new
        {
            Item = item,
            Id = $"cap_{index + 1:D6}",
            Line = $"cap_{index + 1:D6} {item.Card}"
        }).ToArray();
        var totalCharacters = rendered.Sum(static item => item.Line.Length + Environment.NewLine.Length);
        if (totalCharacters > CapabilityCatalogMaxCharacters)
        {
            var largestContributors = new JsonArray(rendered
                .GroupBy(static item => new
                {
                    item.Item.Resolution,
                    item.Item.Server,
                    item.Item.Kind,
                    item.Item.Method
                })
                .Select(static group => new
                {
                    group.Key.Resolution,
                    group.Key.Server,
                    group.Key.Kind,
                    group.Key.Method,
                    Characters = group.Sum(static item => item.Line.Length + Environment.NewLine.Length),
                    Entries = group.Count(),
                    Variants = group.Count(static item => item.Item.Bindings.Count > 0)
                })
                .OrderByDescending(static item => item.Characters)
                .ThenBy(static item => item.Server, StringComparer.Ordinal)
                .ThenBy(static item => item.Method, StringComparer.Ordinal)
                .Take(8)
                .Select(static item => (JsonNode)new JsonObject
                {
                    ["resolution"] = item.Resolution,
                    ["server"] = item.Server,
                    ["kind"] = item.Kind,
                    ["method"] = item.Method,
                    ["characters"] = item.Characters,
                    ["entry_count"] = item.Entries,
                    ["variant_count"] = item.Variants
                }).ToArray());
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "The schema-aware capability catalog exceeds the safe inference limit.",
                details: new JsonObject
                {
                    ["phase"] = "capability_catalog",
                    ["reason"] = "catalog_too_large",
                    ["maximum_characters"] = CapabilityCatalogMaxCharacters,
                    ["total_characters"] = totalCharacters,
                    ["entry_count"] = ordered.Length,
                    ["base_entry_count"] = ordered.Count(static item => item.Bindings.Count == 0),
                    ["variant_count"] = ordered.Count(static item => item.Bindings.Count > 0),
                    ["selected_server_count"] = discovered.Count,
                    ["selected_tool_count"] = discovered.Sum(static server => server.Tools.Count),
                    ["selected_prompt_count"] = discovered.Sum(static server => server.Prompts.Count),
                    ["full_server_count"] = completeDiscovery?.Count ?? discovered.Count,
                    ["full_tool_count"] = completeDiscovery?.Sum(static server => server.Tools.Count)
                                          ?? discovered.Sum(static server => server.Tools.Count),
                    ["full_prompt_count"] = completeDiscovery?.Sum(static server => server.Prompts.Count)
                                            ?? discovered.Sum(static server => server.Prompts.Count),
                    ["largest_contributors"] = largestContributors
                });
        }

        var entries = new List<CapabilityCatalogEntry>(rendered.Length);
        var text = new StringBuilder(totalCharacters);
        foreach (var renderedItem in rendered)
        {
            var item = renderedItem.Item;
            text.AppendLine(renderedItem.Line);
            entries.Add(new CapabilityCatalogEntry(
                renderedItem.Id,
                item.Resolution,
                item.Server,
                item.Kind,
                item.Method,
                item.Bindings,
                item.Card,
                item.RequiredInputs,
                item.Outputs,
                item.ArtifactContract));
        }

        return new CapabilityCatalog(entries, text.ToString());
    }

    private static string FormatArtifactContractSummary(McpArtifactContract? contract)
    {
        if (contract == null)
            return string.Empty;

        var produces = string.Join(",", contract.Produces.Select(static artifact =>
            $"{artifact.Kind}:{artifact.Pointer}:{artifact.Mode}"));
        var consumes = string.Join(",", contract.Consumes.Select(static artifact =>
            $"{artifact.Kind}:{artifact.Pointer}:{(artifact.Required ? "required" : "optional")}"));
        return $" artifacts=[produces({produces}) consumes({consumes})]";
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

    private static string BuildCompactArgumentSummary(JsonNode? inputSchema, bool includeDescriptions)
    {
        if (inputSchema is not JsonObject root)
            return "unknown";
        var values = new List<string>();
        CollectArgumentSummaries(root, string.Empty, 0, GetRequiredPropertyNames(root), values, includeDescriptions);
        var conditions = new List<string>();
        CollectConditionalRequirementSummaries(root, string.Empty, 0, conditions);
        var arguments = values.Count == 0 ? "none" : string.Join(", ", values);
        return conditions.Count == 0
            ? arguments
            : arguments + "; conditional_requirements=[" + string.Join(", ", conditions.Distinct(StringComparer.Ordinal)) + "]";
    }

    private static string BuildCompactOutputSummary(JsonNode? outputSchema)
    {
        if (outputSchema is not JsonObject root)
            return "unknown";
        var values = new List<string>();
        CollectOutputSummaries(root, string.Empty, 0, values);
        return values.Count == 0 ? ReadCompactSchemaType(root) : string.Join(", ", values);
    }

    private static IReadOnlyList<CapabilitySchemaField> BuildCapabilitySchemaFields(
        JsonNode? schema,
        bool requiredOnly)
    {
        if (schema is not JsonObject root)
            return Array.Empty<CapabilitySchemaField>();

        var fields = new List<CapabilitySchemaField>();
        CollectCapabilitySchemaFields(
            root,
            string.Empty,
            0,
            parentRequired: true,
            requiredOnly,
            fields);
        return fields;
    }

    private static void CollectCapabilitySchemaFields(
        JsonObject schema,
        string pointer,
        int depth,
        bool parentRequired,
        bool requiredOnly,
        List<CapabilitySchemaField> fields)
    {
        if (depth >= CapabilitySchemaMaxDepth || schema["properties"] is not JsonObject properties)
            return;

        var required = GetRequiredPropertyNames(schema);
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;

            var path = pointer + "/" + EncodeJsonPointerToken(property.Key);
            if (IsSensitiveSelectorPath(path))
                continue;

            var isRequired = parentRequired && required.Contains(property.Key);
            if (!requiredOnly || isRequired)
            {
                fields.Add(new CapabilitySchemaField(
                    path,
                    ReadCompactSchemaType(propertySchema),
                    ReadCapabilitySchemaFieldDescription(propertySchema)));
            }

            CollectCapabilitySchemaFields(
                propertySchema,
                path,
                depth + 1,
                isRequired,
                requiredOnly,
                fields);
        }
    }

    private static string ReadCapabilitySchemaFieldDescription(JsonObject schema)
        => schema["description"] is JsonValue value
           && value.TryGetValue<string>(out var description)
           && !string.IsNullOrWhiteSpace(description)
            ? LimitCapabilityDescription(description)
            : string.Empty;

    private static void CollectOutputSummaries(JsonObject schema, string pointer, int depth, List<string> values)
    {
        if (depth >= 2 || schema["properties"] is not JsonObject properties)
            return;
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;
            var path = pointer + "/" + EncodeJsonPointerToken(property.Key);
            if (IsSensitiveSelectorPath(path))
                continue;
            values.Add($"{path}:{ReadCompactSchemaType(propertySchema)}");
            CollectOutputSummaries(propertySchema, path, depth + 1, values);
        }
    }

    private static void CollectArgumentSummaries(
        JsonObject schema,
        string pointer,
        int depth,
        IReadOnlyCollection<string> required,
        List<string> values,
        bool includeDescriptions)
    {
        if (depth >= CapabilitySchemaMaxDepth || schema["properties"] is not JsonObject properties)
            return;
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;
            var path = pointer + "/" + EncodeJsonPointerToken(property.Key);
            var type = ReadCompactSchemaType(propertySchema);
            var description = !includeDescriptions || IsSensitiveSelectorPath(path)
                ? string.Empty
                : ReadCompactPropertyDescription(propertySchema);
            values.Add($"{path}:{type}{(required.Contains(property.Key) ? "(required)" : string.Empty)}{description}");
            CollectArgumentSummaries(propertySchema, path, depth + 1, GetRequiredPropertyNames(propertySchema), values, includeDescriptions);
        }
    }

    private static string ReadCompactPropertyDescription(JsonObject schema)
    {
        if (schema["description"] is not JsonValue value
            || !value.TryGetValue<string>(out var description)
            || string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var normalized = LimitCapabilityDescription(description)
            .Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal);
        return $"(description={normalized})";
    }

    private static void CollectConditionalRequirementSummaries(
        JsonObject schema,
        string pointer,
        int depth,
        List<string> values)
    {
        if (depth > CapabilitySchemaMaxDepth)
            return;

        if (schema["dependentRequired"] is JsonObject dependentRequired)
        {
            foreach (var (propertyName, dependenciesNode) in dependentRequired.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (dependenciesNode is not JsonArray dependencies)
                    continue;
                var triggerPath = pointer + "/" + EncodeJsonPointerToken(propertyName);
                if (IsSensitiveSelectorPath(triggerPath))
                    continue;
                var dependencyPaths = dependencies.OfType<JsonValue>()
                    .Select(node => node.TryGetValue<string>(out var dependency) ? dependency : null)
                    .Where(static dependency => !string.IsNullOrWhiteSpace(dependency))
                    .Select(dependency => pointer + "/" + EncodeJsonPointerToken(dependency!))
                    .Where(static path => !IsSensitiveSelectorPath(path))
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToArray();
                if (dependencyPaths.Length > 0)
                    values.Add($"when {triggerPath} is present require {string.Join('|', dependencyPaths)}");
            }
        }

        if (schema["if"] is JsonObject condition
            && schema["then"] is JsonObject consequence)
        {
            var selectors = new List<string>();
            if (condition["properties"] is JsonObject conditionProperties)
            {
                foreach (var (propertyName, propertyNode) in conditionProperties.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    if (propertyNode is not JsonObject propertySchema)
                        continue;
                    var selectorPath = pointer + "/" + EncodeJsonPointerToken(propertyName);
                    if (IsSensitiveSelectorPath(selectorPath))
                        continue;
                    var selectorValues = ReadDocumentedScalarValues(propertySchema, selectorPath);
                    if (selectorValues.Count > 0)
                    {
                        selectors.Add(selectorPath + "=" + string.Join('|', selectorValues.Select(CanonicalScalar)));
                    }
                }
            }

            var requiredPaths = GetRequiredPropertyNames(consequence)
                .Select(propertyName => pointer + "/" + EncodeJsonPointerToken(propertyName))
                .Where(static path => !IsSensitiveSelectorPath(path))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            if (selectors.Count > 0 && requiredPaths.Length > 0)
                values.Add($"when {string.Join('&', selectors)} require {string.Join('|', requiredPaths)}");
        }

        if (depth == CapabilitySchemaMaxDepth || schema["properties"] is not JsonObject properties)
            return;
        foreach (var (propertyName, propertyNode) in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (propertyNode is not JsonObject propertySchema)
                continue;
            var propertyPointer = pointer + "/" + EncodeJsonPointerToken(propertyName);
            if (IsSensitiveSelectorPath(propertyPointer))
                continue;
            CollectConditionalRequirementSummaries(propertySchema, propertyPointer, depth + 1, values);
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
        CollectBoundedIndependentSelectorCombination(root, variants);
        return variants
            .GroupBy(static variant => CanonicalizeBindings(variant.Bindings), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void CollectBoundedIndependentSelectorCombination(
        JsonObject root,
        List<SelectorVariant> variants)
    {
        var dimensions = new List<(string Path, IReadOnlyList<JsonNode?> Values)>();
        CollectIndependentSelectorDimensions(root, string.Empty, 0, dimensions);
        if (dimensions.Count is < 2 or > 4)
            return;
        long product = 1;
        foreach (var dimension in dimensions)
        {
            product *= dimension.Values.Count;
            if (product > 128)
                return;
        }

        var combinations = new List<List<CapabilityRequestBinding>> { new() };
        foreach (var dimension in dimensions)
        {
            combinations = combinations.SelectMany(existing => dimension.Values.Select(value =>
            {
                var next = new List<CapabilityRequestBinding>(existing)
                {
                    new(dimension.Path, value?.DeepClone())
                };
                return next;
            })).ToList();
        }
        variants.AddRange(combinations.Select(static bindings => new SelectorVariant(bindings)));
    }

    private static void CollectIndependentSelectorDimensions(
        JsonObject schema,
        string pointer,
        int depth,
        List<(string Path, IReadOnlyList<JsonNode?> Values)> dimensions)
    {
        if (depth >= CapabilitySchemaMaxDepth || schema["properties"] is not JsonObject properties)
            return;
        foreach (var property in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
                continue;
            var path = pointer + "/" + EncodeJsonPointerToken(property.Key);
            if (IsSensitiveSelectorPath(path))
                continue;
            var values = ReadDocumentedScalarValues(propertySchema, path);
            if (values.Count > 0)
                dimensions.Add((path, values));
            CollectIndependentSelectorDimensions(propertySchema, path, depth + 1, dimensions);
        }
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
