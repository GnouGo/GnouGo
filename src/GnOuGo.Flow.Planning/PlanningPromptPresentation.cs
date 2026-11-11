using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Presents response fields and shares identical contracts without changing their authority.</summary>
public static class PlanningPromptPresentation
{
    internal const string LifecycleScopeSemantics = "Business-block review does not execute tasks or close an owning workflow scope. A reusable group declares a body; only a call invokes it. Cleanup belongs to the scope covering the resource's actual consumers, which may be an ancestor of a helper or business block. Reuse appropriate existing ancestor cleanup; do not end a still-needed resource merely to finish a helper or request business review. Where the issued binding schema permits a composed control, select its kind together with exact inspected operationIds for its nested operations; an empty list selects none. Preserve explicit lifetime and cleanup requirements, lexical visibility, available transitions and ordinary validation. This guidance grants no new binding, repair or execution authority.";

    internal const string FinalizationSemantics = "always contains cleanup on completion or failure; preserve available payloads before nested cleanup. present tests preceding local or lexical-ancestor task completion, not caller inputs or field nullability. For a null guard, compare the exact input, output or field with null.";

    internal static string PresentLifecycleScopeGuidance(string prompt, JsonNode? bindingSchema = null)
    {
        var guidance = "\n" + LifecycleScopeSemantics + "\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        var result = contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
        return bindingSchema is null ? result : PresentNativeControlGuidance(result, bindingSchema);
    }

    /// <summary>Distinguish offered local control forms from external operation contracts.</summary>
    public static string PresentNativeControlGuidance(string prompt, JsonNode schema)
    {
        if (!Find(schema["properties"]?["binding"], 0) && !Find(schema["properties"]?["step"], 0)) return prompt;
        var guidance = "\nNative TaskPlan controls have no MCP operation contract. Select a local control only when offered by the current binding schema; its scoped draft supplies the authoritative task/value grammar. always is a TaskScope finalization list, and present is a TaskValue completion test, not an external operation. Do not suspend for a runtime producer of these planning rules. " + FinalizationSemantics + "\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);

        bool Find(JsonNode? node, int depth)
        {
            if (depth > 32 || node is not JsonObject value) return false;
            if (value["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var pointer))
                return pointer.StartsWith("#/", StringComparison.Ordinal) && Find(PlanningSchemaReferences.Read(schema, pointer[1..]), depth + 1);
            if (value["properties"]?["kind"] is JsonObject kind && FindKind(kind, depth + 1)) return true;
            return new[] { "anyOf", "oneOf" }.Any(key => value[key] is JsonArray branches && branches.Any(branch => Find(branch, depth + 1)));
        }

        bool FindKind(JsonNode? node, int depth)
        {
            if (depth > 32 || node is not JsonObject value) return false;
            if (value["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var pointer))
                return pointer.StartsWith("#/", StringComparison.Ordinal) && FindKind(PlanningSchemaReferences.Read(schema, pointer[1..]), depth + 1);
            if (value["enum"] is JsonArray kinds && kinds.Any(kind => kind?.ToString() is "sequence" or "conditional" or "foreach" or "parallel")) return true;
            return new[] { "anyOf", "oneOf" }.Any(key => value[key] is JsonArray branches && branches.Any(branch => FindKind(branch, depth + 1)));
        }
    }

    internal static string PresentFailureObligationGuidance(string prompt)
    {
        var guidance = "\n" + PlanningPrompt.FailureObligationSemantics + "\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    internal static bool OffersRuntimePrerequisite(JsonNode schema)
    {
        return Find(schema["properties"]?["reselectStep"], 0);

        bool Find(JsonNode? node, int depth)
        {
            if (depth > 32) return false;
            node = Resolve(node);
            if (node is not JsonObject) return false;
            if (Resolve(node["properties"]?["disposition"]) is JsonObject disposition && disposition["enum"] is JsonArray choices &&
                choices.Any(choice => choice?.ToString() == "suspend")) return true;
            return new[] { "anyOf", "oneOf" }.Any(key => node?[key] is JsonArray branches && branches.Any(branch => Find(branch, depth + 1)));
        }

        JsonNode? Resolve(JsonNode? node)
        {
            for (var depth = 0; depth < 32; depth++)
            {
                if (node is not JsonObject value || value["$ref"] is null) return node;
                var reference = value["$ref"]!.ToString();
                if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
                node = PlanningSchemaReferences.Read(schema, reference[1..]);
            }
            return null;
        }
    }

    /// <summary>Distinguish contract acquisition from a typed runtime-data prerequisite.</summary>
    public static string PresentRuntimePrerequisiteGuidance(string prompt)
    {
        const string guidance = "\nOperation contracts, capability descriptions and catalogue availability are planning evidence, not missing runtime values. Keep their discovery in the offered evidence/reselection path. Do not suspend a consumer merely to emit that metadata as a value or transform task. Use suspend only when an executable producer must supply runtime data needed by the retained consumer. If a selected operation lacks another required operation or its cleanup contract, use reselectStep with disposition:abandon, then discover/select the connected operations together. This preserves the business block and accepted work; no side effect executes during planning.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    /// <summary>Present a singleton evidence view as an object without changing the selectable evidence or batch bounds.</summary>
    public static JsonNode PresentEvidenceSelectionObjects(JsonNode schema) => PlanningEvidenceSelectionWire.Project(schema);

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

    /// <summary>Explain the existing transition from inspected operation descriptions to exact contract retrieval.</summary>
    public static string PresentContractMaterializationGuidance(string prompt)
    {
        const string guidance = "\nInspect interfaces through offered contractInspection: discoveryRequests with sourceId, operationIds, cursor:null and query:null retrieves metadata without a task binding. Source exploration returns descriptions; repetition does not retrieve schemas. Alternatively, naming discovered operationIds in binding asks the coordinator to retrieve its exact owned contract before drafting. Selection does not assert known parameter values, successful execution or business completion. Compare constraints and lifecycle dependencies, reuse loaded evidence, then select connected operations and draft or reselect against the actual contract.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    /// <summary>Explain the existing explicit reusable-group dependency for a composed control fragment.</summary>
    public static string PresentGroupCompositionGuidance(string prompt)
    {
        const string guidance = "\nWhen a control fragment must invoke an already closed reusable group, select both the control kind and that existing group's groupId. groupId selects a callable dependency; it neither declares a new group nor executes it. A control selected with groupId:null has no selected reusable-call dependency. Inspect and reuse the existing group's exact interface, and bind each call's values in its own scope. Available local controls and group calls require no external tool discovery.\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
    }

    /// <summary>Guide consumers to reuse typed evidence without duplicating full observations.</summary>
    internal static string PresentInferenceInputGuidance(string prompt)
    {
        var guidance = "\n" + PlanningCompactConstruction.TransformInputSemantics + "\n";
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        return contextOffset < 0 ? prompt + guidance : prompt.Insert(contextOffset, guidance);
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
        if (context is null) return prompt;
        // Bound interfaces can repeat large record shapes even when their
        // producer contracts differ through compiler-applied constraints. Factor
        // each schema independently; never substitute one owner's contract for
        // another or detach the original schema's reference scope.
        var shared = context["schemaPresentation"] is null && FactorImportedSchemaShapes(context);
        var operations = context["operations"] as JsonArray ?? [];
        var evidence = context["evidence"] as JsonObject ?? [];
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

    private static bool FactorImportedSchemaShapes(JsonObject context)
    {
        var changed = false;
        foreach (var row in (context["interfaces"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Factor(row, "schema");
            foreach (var producer in (row["producers"] as JsonArray ?? []).OfType<JsonObject>())
                if (producer["contract"] is JsonObject contract)
                    foreach (var field in new[] { "selectedInputs", "selectedOutputs" })
                        foreach (var view in (contract[field] as JsonArray ?? []).OfType<JsonObject>())
                            Factor(view, "schema");
        }
        return changed;

        void Factor(JsonObject owner, string field)
        {
            if (owner[field] is not JsonObject original || original.ContainsKey("$defs")) return;
            var copy = original.DeepClone().AsObject();
            var schemas = PlanningSchemaReferences.Walk(copy, "", 0).Select(entry => entry.Schema).ToArray();
            // Nested identifiers introduce reference scopes. Keep those exact;
            // this optimization only factors within one unchanged schema root.
            if (schemas.Any(schema => new[] { "$id", "$anchor", "$dynamicAnchor", "$dynamicRef", "$recursiveRef", "$ref" }
                    .Any(schema.ContainsKey))) return;
            var definitions = new JsonObject(); copy["$defs"] = definitions;
            PlanningSchemas.ShareRepeatedSchemas(copy, definitions, schemaNodes: schemas);
            if (definitions.Count == 0 || copy.ToJsonString().Length >= original.ToJsonString().Length) return;
            owner[field] = copy; changed = true;
        }
    }

    private static bool Identity(JsonObject value, out string id, out string version, out string hash)
    {
        id = Text(value["id"]); version = Text(value["version"]); hash = Text(value["hash"]);
        return id.Length > 0 && version.Length > 0 && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
