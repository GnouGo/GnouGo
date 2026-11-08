using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Presents response fields and shares identical contracts without changing their authority.</summary>
public static class PlanningPromptPresentation
{
    /// <summary>Present narrower array alternatives first without changing any allowed instance.</summary>
    public static JsonNode OrderBoundedArrayAlternativesFirst(JsonNode schema)
    {
        var result = schema.DeepClone();
        Visit(result);
        return result;

        static string? TypeOf(JsonNode? node) => node is JsonObject schemaObject &&
            schemaObject["type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : null;
        static void Visit(JsonNode? node)
        {
            if (node is not JsonObject valueObject) return;
            // Traverse schema positions only. Constants, enum values, defaults,
            // examples and extension data are not schemas and must stay exact.
            foreach (var keyword in new[] { "$defs", "definitions", "properties", "patternProperties", "dependentSchemas", "dependencies" })
                if (valueObject[keyword] is JsonObject dictionary)
                    foreach (var entry in dictionary) Visit(entry.Value);
            foreach (var keyword in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
                if (valueObject[keyword] is JsonArray children)
                    foreach (var child in children) Visit(child);
            foreach (var keyword in new[] { "items", "additionalItems", "additionalProperties", "contains", "propertyNames", "not", "if", "then", "else", "unevaluatedItems", "unevaluatedProperties", "contentSchema" })
                if (valueObject[keyword] is JsonArray legacyItems)
                    foreach (var item in legacyItems) Visit(item);
                else Visit(valueObject[keyword]);
            if (valueObject["anyOf"] is not JsonArray alternatives ||
                alternatives.Count(a => TypeOf(a) == "array") < 2 ||
                alternatives.Any(a => TypeOf(a) is not ("array" or "null"))) return;
            var ordered = alternatives.Select((alternative, index) => (alternative, index))
                .OrderBy(pair => TypeOf(pair.alternative) == "null" ? 2 :
                    pair.alternative?["maxItems"] is JsonValue maximum && maximum.TryGetValue<int>(out _) ? 0 : 1)
                .ThenBy(pair => pair.index).Select(pair => pair.alternative!.DeepClone()).ToArray();
            valueObject["anyOf"] = new JsonArray(ordered);
        }
    }

    /// <summary>Request compact JSON syntax without selecting any business value or branch.</summary>
    public static string PresentCompactJsonGuidance(string prompt)
    {
        const string guidance = "\nEmit compact JSON without formatting whitespace outside strings. Close each array and object as soon as its selected contents are complete; do not pad the response.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    /// <summary>Keep the original delivery channel distinct from an additional caller-return contract.</summary>
    public static string PresentDeliveryChannelGuidance(string prompt)
    {
        const string guidance = "\nFor a missing-result concern, establish the delivery channel required by the original obligation: a file, a caller return, or both. Terms such as result, report or feedback alone do not establish an additional caller-return contract when the originals prescribe delivery in a file. A specifically requested caller return remains mandatory even if the same value is saved to a file. Assess each requested channel and its actual provenance independently.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    /// <summary>Expose required discovery wire fields without copying the response grammar or its value directories.</summary>
    public static string PresentDiscoveryResponseFields(string prompt, JsonNode schema)
    {
        var forms = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Visit(schema, "response", 0, new HashSet<string>(StringComparer.Ordinal));
        var guide = string.Join("; ", forms.Select(pair => pair.Key + ": " + string.Join(" | ", pair.Value)));
        if (guide.Length == 0) return prompt;
        var presentation = "\nDiscovery response field guide (required keys per object branch): " + guide +
            ". Emit the selected branch's required keys, including null-valued fields. Use only this request's issued choices. The complete response schema remains authoritative.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + presentation : prompt.Insert(contextOffset, presentation);

        void Add(string path, string value)
        {
            if (!forms.TryGetValue(path, out var values)) forms.Add(path, values = new(StringComparer.Ordinal));
            values.Add(value);
        }
        void Visit(JsonNode? node, string path, int depth, HashSet<string> references)
        {
            if (depth > 2 || node is not JsonObject value) return;
            if (value["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var pointer))
            {
                if (!pointer.StartsWith("#/$defs/", StringComparison.Ordinal) || !references.Add(pointer)) return;
                var key = Uri.UnescapeDataString(pointer[8..]).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                Visit(schema["$defs"]?[key], path, depth, references);
                references.Remove(pointer);
                return;
            }
            foreach (var keyword in new[] { "anyOf", "oneOf" })
                if (value[keyword] is JsonArray branches)
                    foreach (var branch in branches) Visit(branch, path, depth, references);
            if (value["properties"] is JsonObject properties)
            {
                if (value["required"] is JsonArray required)
                    Add(path, "{" + string.Join(",", required.Select(item => item!.GetValue<string>())) + "}");
                foreach (var (name, property) in properties) Visit(property, path + "." + name, depth + 1, references);
            }
            if (value["items"] is JsonObject items) Visit(items, path + "[]", depth, references);
        }
    }

    /// <summary>Present the exact response grammar without changing the final context object.</summary>
    public static string PresentResponseSchema(string prompt, JsonNode schema)
    {
        var presentation = "\nReturn an object matching this exact JSON response schema. Emit every required key in the selected branch, including null-valued fields. No prose or workflow execution.\n" + schema.ToJsonString();
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + presentation : prompt.Insert(contextOffset, presentation);
    }

    /// <summary>Distinguish result dependencies from prose prerequisites in business selection.</summary>
    public static string PresentBusinessDependencyFields(string prompt)
    {
        const string presentation = "\nBlock dependencies contain only objects with resultId (an availableResults id) and reason. When availableResults is empty, dependencies is []. Keep prerequisites and global constraints out of that array.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + presentation : prompt.Insert(contextOffset, presentation);
    }

    /// <summary>Make caller-interface wire keys explicit without duplicating the recursive response schema.</summary>
    public static string PresentIntentDeclarationFields(string prompt)
    {
        const string presentation = "\nEach input includes the keys name, type, required, default; each public output includes name, type, required. Each type includes kind and nullable. String types also include enum; arrays include items, minItems, maxItems; objects include fields. Literal defaults include kind and the matching text, number, boolean, items or members field; members include name and value. Each clarification includes id, question, alternatives, recommended; alternatives include id and description.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + presentation : prompt.Insert(contextOffset, presentation);
    }

    /// <summary>
    /// Replaces a repeated evidence schema with a pointer to its byte-identical schema
    /// already presented for the same operation identity, version and original hash.
    /// Original coordinates, ancestor constraints, reference closures and diagnostics remain.
    /// </summary>
    public static string ShareDuplicateContractSchemas(string prompt)
    {
        var offset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (offset < 0) return prompt;
        JsonObject? context;
        try { context = JsonNode.Parse(prompt[(offset + 1)..]) as JsonObject; }
        catch (JsonException) { return prompt; }
        if (context?["operations"] is not JsonArray operations || context["evidence"] is not JsonObject evidence) return prompt;
        var schemas = new Dictionary<(string Id, string Version, string Hash, string Pointer, string Schema), string>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject operation || !Identity(operation, out var id, out var version, out var hash)) continue;
            Add(operation["inputSchema"] as JsonObject, "/input", $"/operations/{i}/inputSchema");
            foreach (var name in new[] { "selectedInputs", "selectedOutputs" })
                if (operation[name] is JsonArray selected)
                    for (var j = 0; j < selected.Count; j++)
                        if (Text(selected[j]?["pointer"]) is { Length: > 0 } pointer)
                            Add(selected[j]?["schema"] as JsonObject, pointer, $"/operations/{i}/{name}/{j}/schema");

            void Add(JsonObject? schema, string original, string presented)
            {
                if (schema is null) return;
                foreach (var entry in PlanningSchemaReferences.Walk(schema, original, 0))
                    schemas.TryAdd((id, version, hash, entry.Path, entry.Schema.ToJsonString()), presented + entry.Path[original.Length..]);
            }
        }
        var shared = false;
        foreach (var row in evidence.Select(p => p.Value).OfType<JsonObject>())
            if (Identity(row, out var id, out var version, out var hash) && Text(row["pointer"]) is { Length: > 0 } pointer &&
                row["schema"] is JsonObject schema && !row.ContainsKey("schemaPresentedAt") &&
                schemas.TryGetValue((id, version, hash, pointer, schema.ToJsonString()), out var presented))
            {
                row.Remove("schema"); row["schemaPresentedAt"] = presented; shared = true;
            }
        if (!shared) return prompt;
        context["schemaPresentation"] = "schemaPresentedAt is a JSON pointer within this same context to the byte-identical schema for the retained operation id/version/hash and original pointer. Evidence retains its original ancestor constraints and reference closure. This presentation link grants no binding, contract or execution authority.";
        return prompt[..(offset + 1)] + PlanningJsonTransport.Prompt(context);
    }

    private static bool Identity(JsonObject value, out string id, out string version, out string hash)
    {
        id = Text(value["id"]); version = Text(value["version"]); hash = Text(value["hash"]);
        return id.Length > 0 && version.Length > 0 && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
