using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Presents response fields and shares identical contracts without changing their authority.</summary>
public static class PlanningPromptPresentation
{
    internal const string LifecycleScopeSemantics = "Business-block review does not execute tasks or close an owning workflow scope. A reusable group declares a body; only a call invokes it. Cleanup belongs to the scope covering the resource's actual consumers, which may be an ancestor of a helper or business block. Reuse appropriate existing ancestor cleanup; do not end a still-needed resource merely to finish a helper or request business review. Where the issued binding schema permits a composed control, select its kind together with exact inspected operationIds for its nested operations; an empty list selects none. Preserve explicit lifetime and cleanup requirements, lexical visibility, available transitions and ordinary validation. This guidance grants no new binding, repair or execution authority.";

    internal const string FinalizationSemantics = "always cleans up on completion or failure. Preserve payloads before nested cleanup. present tests preceding local or lexical-ancestor task completion. In consumer requires, short-circuit and(not_equal(exact input/output/field,null),field test). Bind the guarded value directly. Combine checks on the same observation in one requires with nested binary and/or. Pass its original outputs to the consumer.";

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
        var guidance = "\nNative TaskPlan controls have no MCP operation contract. Select a local control only when offered by the current binding schema; its scoped draft supplies the authoritative task/value grammar. always is a TaskScope finalization list, and present is a TaskValue completion test, not an external operation. Do not suspend for a runtime producer of these planning rules. " + PlanningCompactConstruction.IterationCapacityGuidance + " " + FinalizationSemantics + "\n";
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
    internal static string PresentInferenceInputGuidance(string prompt, int? maxLoopIterations)
    {
        var extractionLimit = maxLoopIterations is { } limit
            ? FormattableString.Invariant($" On the captured host, each extraction accepts at most {limit} items per invocation. Preserve source groups when the flattened collection exceeds that limit.")
            : "";
        var guidance = "\n" + PlanningCompactConstruction.TransformInputSemantics + " " + PlanningCompactConstruction.IterationCapacityGuidance + extractionLimit + "\n";
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
        const string guidance = "\nFor a missing-result concern, establish the delivery channel required by the original obligation: a caller return, file or external action. Trace the file content or external action's actual arguments to the requested information. Internal preparation alone does not deliver it. A specifically requested caller return remains mandatory alongside any other requested delivery. Assess each requested channel and its actual provenance independently.\n";
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
        var sharedReselection = ShareReselectionEvidence(context);
        var composedFields = ShareImportedObjectMembers(context);
        var shared = context["schemaPresentation"] is null && FactorImportedSchemaShapes(context);
        var sharedResults = ShareRepeatedOutputSchemas(context);
        var sharedSiblings = ShareImportedArgumentSiblings(context);
        var sharedTasks = ShareRepeatedTaskViews(context);
        var sharedTaskPages = ShareRepeatedTaskPages(context);
        var sharedReviewContracts = ShareReviewedOperationDetails(context);
        var sharedReviewSnapshots = ShareUnchangedReviewSnapshots(context);
        var sharedPrefix = false;
        var operations = context["operations"] as JsonArray ?? [];
        var evidence = context["evidence"] as JsonObject ?? [];
        var ancestorLocations = new Dictionary<(string Id, string Version, string Hash, string Ancestors), string>();
        var schemas = new Dictionary<(string Id, string Version, string Hash, string Pointer, string Schema), string>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject operation || !Identity(operation, out var id, out var version, out var hash)) continue;
            Add(id, version, hash, operation["inputSchema"] as JsonObject, "/input", $"/operations/{i}/inputSchema");
            foreach (var name in new[] { "selectedInputs", "selectedOutputs" })
            {
                if (operation[name] is not JsonArray selected) continue;
                var metadata = name + "SharedSchemaMetadata";
                sharedPrefix |= ShareSelectedAncestorPrefix(operation, selected, metadata);
                var prefix = $"/operations/{i}/{metadata}/";
                foreach (var field in new[] { "ancestors", "ancestorPrefix" })
                {
                    if (operation[metadata]?[field] is not JsonArray ancestors) continue;
                    if (field == "ancestors" && ancestors.Count > 0)
                        ancestorLocations.TryAdd((id, version, hash, ancestors.ToJsonString()), prefix + field);
                    for (var index = 0; index < ancestors.Count; index++)
                        if (ancestors[index] is JsonObject ancestor && Text(ancestor["pointer"]) is { Length: > 0 } origin)
                            Add(id, version, hash, ancestor["constraints"] as JsonObject, origin, $"{prefix}{field}/{index}/constraints");
                }
                for (var j = 0; j < selected.Count; j++)
                {
                    if (selected[j] is not JsonObject row || Text(row["pointer"]) is not { Length: > 0 } pointer ||
                        row["schema"] is not JsonObject schema) continue;
                    if (row["id"] is { } owner && Text(owner) != id ||
                        row["version"] is { } revision && Text(revision) != version ||
                        row["hash"] is { } digest && Text(digest) != hash) continue;
                    var exact = schema.ToJsonString();
                    if (schemas.TryGetValue((id, version, hash, pointer, exact), out var presented) &&
                        presented.StartsWith(prefix, StringComparison.Ordinal) && exact.Length > presented.Length + (shared ? 24 : 320))
                    {
                        row.Remove("schema");
                        row["schemaPresentedAt"] = presented;
                        shared = true;
                    }
                    else Add(id, version, hash, schema, pointer, $"/operations/{i}/{name}/{j}/schema");
                }
            }
        }
        var sharedAncestors = ShareEvidenceAncestors(evidence, ancestorLocations);
        foreach (var (address, value) in evidence)
        {
            if (value is not JsonObject row || row["ancestors"] is not JsonArray ancestors ||
                !Identity(row, out var id, out var version, out var hash)) continue;
            for (var i = 0; i < ancestors.Count; i++)
                if (ancestors[i] is JsonObject ancestor && Text(ancestor["pointer"]) is { Length: > 0 } pointer)
                    Add(id, version, hash, ancestor["constraints"] as JsonObject, pointer,
                        $"/evidence/{PlanningSchemaReferences.Escape(address)}/ancestors/{i}/constraints");
        }
        void Add(string id, string version, string hash, JsonObject? schema, string original, string presented)
        {
            if (schema is null) return;
            foreach (var entry in PlanningSchemaReferences.Walk(schema, original, 0))
                schemas.TryAdd((id, version, hash, entry.Path, entry.Schema.ToJsonString()), presented + entry.Path[original.Length..]);
        }
        foreach (var row in evidence.Select(p => p.Value).OfType<JsonObject>())
            if (Identity(row, out var id, out var version, out var hash) && Text(row["pointer"]) is { Length: > 0 } pointer &&
                row["schema"] is JsonObject schema && !row.ContainsKey("schemaPresentedAt") &&
                schemas.TryGetValue((id, version, hash, pointer, schema.ToJsonString()), out var presented))
            {
                if (presented.StartsWith("/evidence/", StringComparison.Ordinal) && schema.ToJsonString().Length <= presented.Length + (shared ? 24 : 320)) continue;
                row.Remove("schema"); row["schemaPresentedAt"] = presented; shared = true;
            }
        if (!shared && !sharedTasks && !sharedTaskPages && !sharedAncestors && !sharedPrefix && !sharedSiblings && !sharedResults && !sharedReviewContracts && !sharedReviewSnapshots && !composedFields && !sharedReselection) return prompt;
        if (sharedReselection)
            context["reselectionEvidencePresentation"] = "unchangedEvidence lists archived items whose full value appears under the same address in this request's evidence.";
        if (sharedReviewSnapshots)
            context["reviewSnapshotPresentation"] = "unchangedAt points to the identical before and current snapshot for this concern in the present review.";
        if (composedFields)
            context["composedFieldPresentation"] = "composedFieldAt points to the same imported result's field schema in an earlier object. Resolve its local references within that object's interface schema. Each result retains its own binding and producer.";
        if (sharedPrefix)
            context["ancestorPrefixPresentation"] = "For each selected input or output, prepend its direction's shared ancestorPrefix to its own ancestors in order. Together they are the original constraints.";
        if (sharedSiblings)
            context["requiredSiblingPresentation"] = "requiredSiblingsPresentedAt maps each required property to its exact schema in the selected inputs of the same producer contract.";
        if (sharedReviewContracts)
            context["reviewContractPresentation"] = "contractDetailsAt points to the action with the identical operation meaning and ports. Each action retains its own arguments, controls and results.";
        if (sharedResults)
            context["interfaceSchemaPresentation"] = "An interface's schemaPresentedAt points to its complete, identical output schema in this context. Each result retains its own binding, producer and arguments.";
        if (sharedAncestors)
            context["ancestorPresentation"] = "ancestorsPresentedAt points to the exact ancestor constraints shared by schema views of this operation and version.";
        if (shared)
            context["schemaPresentation"] = "schemaPresentedAt is a JSON pointer within this same context to the byte-identical schema for the retained operation id/version/hash and original pointer. Evidence retains its original ancestor constraints and reference closure. This presentation link grants no binding, contract or execution authority.";
        if (sharedTasks)
            context["taskPresentation"] = "taskPresentedAt is a JSON pointer within this same context to the byte-identical task declaration under the same task id, scope and tasks/always phase. Location metadata stays on each view. Read its original arguments and behavior there; distinct task results remain distinct. This presentation link grants no binding, repair or execution authority.";
        if (sharedTaskPages)
            context["taskPagePresentation"] = "A task page's itemsPresentedAt points to its complete ordered items array in this context. Its offset, total and continuation belong to the page.";
        return prompt[..(offset + 1)] + PlanningJsonTransport.Prompt(context);
    }

    private static bool ShareReselectionEvidence(JsonObject context)
    {
        if (context["evidence"] is not JsonObject evidence) return false;
        var changed = false;
        foreach (var (address, row) in evidence)
        {
            if (!address.StartsWith("rejected:", StringComparison.Ordinal) || row is not JsonObject rejected ||
                rejected["evidence"] is not JsonObject archived || rejected["step"] is not JsonObject ||
                rejected["unchangedEvidence"] is not null) continue;
            var shared = new JsonArray();
            foreach (var (key, snapshot) in archived.ToArray())
            {
                if (key.StartsWith("rejected:", StringComparison.Ordinal) || snapshot is not (JsonObject or JsonArray) ||
                    !JsonNode.DeepEquals(snapshot, evidence[key]) || snapshot.ToJsonString().Length <= key.Length + 320) continue;
                shared.Add(key);
                archived.Remove(key);
            }
            if (shared.Count == 0) continue;
            rejected["unchangedEvidence"] = shared;
            changed = true;
        }
        return changed;
    }

    private static bool ShareSelectedAncestorPrefix(JsonObject operation, JsonArray selected, string metadataName)
    {
        if (selected.Count < 2 || operation[metadataName] is not (null or JsonObject) ||
            operation[metadataName]?["ancestors"] is not null || operation[metadataName]?["ancestorPrefix"] is not null) return false;
        var lists = new List<JsonArray>();
        foreach (var node in selected)
        {
            if (node is not JsonObject row || row["ancestors"] is not JsonArray { Count: > 0 } ancestors ||
                new[] { "id", "version", "hash" }.Any(key => row.ContainsKey(key) && !JsonNode.DeepEquals(row[key], operation[key]))) return false;
            lists.Add(ancestors);
        }
        var prefix = new JsonArray();
        var length = lists.Min(list => list.Count);
        for (var index = 0; index < length; index++)
        {
            if (lists[0][index] is not JsonObject ancestor || Text(ancestor["pointer"]).Length == 0) break;
            var exact = ancestor.ToJsonString();
            if (lists.Skip(1).Any(list => list[index]?.ToJsonString() != exact)) break;
            prefix.Add(ancestor.DeepClone());
        }
        if (prefix.Count == 0 || (long)(lists.Count - 1) * prefix.ToJsonString().Length < 384) return false;
        operation[metadataName] ??= new JsonObject();
        operation[metadataName]!["ancestorPrefix"] = prefix;
        foreach (var list in lists)
            for (var remaining = prefix.Count; remaining > 0; remaining--) list.RemoveAt(0);
        return true;
    }

    private static bool ShareImportedArgumentSiblings(JsonObject context)
    {
        if (context["interfaces"] is not JsonArray interfaces) return false;
        var changed = false;
        for (var i = 0; i < interfaces.Count; i++)
        {
            if (interfaces[i] is not JsonObject row || row["producers"] is not JsonArray producers) continue;
            for (var j = 0; j < producers.Count; j++)
                if (producers[j] is JsonObject producer && producer["contract"] is JsonObject contract &&
                    Identity(contract, out _, out _, out _) && contract["selectedInputs"] is JsonArray selected)
                    changed |= ShareSelectedArgumentSiblings(contract, selected, $"/interfaces/{i}/producers/{j}/contract/selectedInputs");
        }
        return changed;
    }

    private static bool ShareSelectedArgumentSiblings(JsonObject contract, JsonArray selected, string path)
    {
        var views = selected.OfType<JsonObject>().ToArray();
        if (views.Any(view => new[] { "id", "version", "hash" }.Any(key =>
            view.ContainsKey(key) && !JsonNode.DeepEquals(view[key], contract[key])))) return false;
        var originals = new Dictionary<(string Pointer, string Schema), string>();
        for (var index = 0; index < selected.Count; index++)
            if (selected[index] is JsonObject row && Text(row["pointer"]) is { Length: > 0 } pointer && row["schema"] is JsonObject schema)
                originals.TryAdd((pointer, schema.ToJsonString()), $"{path}/{index}/schema");
        var changed = false;
        foreach (var ancestor in views.SelectMany(view => (view["ancestors"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            if (Text(ancestor["pointer"]) is not { Length: > 0 } parent || ancestor["requiredSiblings"] is not JsonObject siblings ||
                ancestor.ContainsKey("requiredSiblingsPresentedAt")) continue;
            var locations = new JsonObject();
            foreach (var (name, value) in siblings)
            {
                if (value is not JsonObject sibling) continue;
                var exact = sibling.ToJsonString();
                var coordinate = parent + "/properties/" + PlanningSchemaReferences.Escape(name);
                if (originals.TryGetValue((coordinate, exact), out var at) && exact.Length > at.Length + 200)
                    locations[name] = at;
            }
            if (locations.Count == 0) continue;
            foreach (var pair in locations) siblings.Remove(pair.Key);
            ancestor["requiredSiblingsPresentedAt"] = locations;
            changed = true;
        }
        return changed;
    }

    private static bool ShareImportedObjectMembers(JsonObject context)
    {
        if (context["interfaces"] is not JsonArray { Count: > 1 } interfaces) return false;
        var fields = new Dictionary<(string Scope, string Source, string Port), (JsonObject Schema, string Pointer)>();
        var changed = false;
        for (var index = 0; index < interfaces.Count; index++)
        {
            if (interfaces[index] is not JsonObject row || row["schema"] is not JsonObject schema ||
                row["value"] is not JsonObject binding || Text(binding["kind"]) != "output" ||
                row["producers"] is not JsonArray { Count: 1 } producers || producers[0] is not JsonObject producer ||
                Text(producer["scope"]) is not { Length: > 0 } scope || Text(producer["phase"]) != "tasks" ||
                Text(binding["source"]) is not { Length: > 0 } id || Text(producer["id"]) != id ||
                Text(binding["port"]) is not { Length: > 0 } port) continue;
            if (fields.TryGetValue((scope, id, port), out var field) && JsonNode.DeepEquals(schema, field.Schema))
            {
                row.Remove("schema");
                row["composedFieldAt"] = field.Pointer;
                changed = true;
                continue;
            }
            // Only an accepted local object construction proves that this member
            // is the original value. Equal types alone do not establish a source.
            if (producer["task"] is not JsonObject task || Text(task["id"]) != id || Text(task["kind"]) != "value" ||
                producer["contract"] is not null || task["outputs"] is not JsonArray outputs ||
                outputs.OfType<JsonObject>().FirstOrDefault(o => Text(o["name"]) == port)?["value"] is not JsonObject value ||
                Text(value["kind"]) != "object" || value["members"] is not JsonArray members ||
                schema["properties"] is not JsonObject properties || HasReferenceKeyword(schema)) continue;
            foreach (var member in members.OfType<JsonObject>())
            {
                var name = Text(member["name"]);
                if (member["value"] is not JsonObject reference || Text(reference["kind"]) != "output" ||
                    Text(reference["source"]) is not { Length: > 0 } source || Text(reference["port"]) is not { Length: > 0 } sourcePort ||
                    properties[name] is not JsonObject property || property.ToJsonString().Length < 512) continue;
                fields.TryAdd((scope, source, sourcePort), (property, $"/interfaces/{index}/schema/properties/{PlanningSchemaReferences.Escape(name)}"));
            }
        }
        return changed;

        // Unknown dialects and nested reference scopes retain the original
        // standalone view. Check extension objects as well as known schema slots.
        static bool HasReferenceKeyword(JsonNode? node) => node switch
        {
            JsonObject obj => obj.Any(pair => pair.Key.StartsWith('$') || HasReferenceKeyword(pair.Value)),
            JsonArray array => array.Any(HasReferenceKeyword),
            _ => false
        };
    }

    private static bool ShareRepeatedOutputSchemas(JsonObject context)
    {
        if (context["interfaces"] is not JsonArray interfaces) return false;
        var originals = new Dictionary<(string Id, string Version, string Hash, string Scope, string Phase, string Port, string Schema), string>();
        var changed = false;
        for (var index = 0; index < interfaces.Count; index++)
        {
            if (interfaces[index] is not JsonObject row || row["schema"] is not JsonObject schema ||
                row["value"] is not JsonObject binding || Text(binding["kind"]) != "output" ||
                row["producers"] is not JsonArray { Count: 1 } producers || producers[0] is not JsonObject producer ||
                Text(binding["source"]) is not { Length: > 0 } source || Text(producer["id"]) != source ||
                Text(producer["scope"]) is not { Length: > 0 } scope || Text(producer["phase"]) is not ("tasks" or "always") ||
                producer["contract"] is not JsonObject contract || !Identity(contract, out var id, out var version, out var hash)) continue;
            var exact = schema.ToJsonString();
            if (exact.Length < 320) continue;
            var key = (id, version, hash, scope, Text(producer["phase"]), Text(binding["port"]), exact);
            if (originals.TryGetValue(key, out var pointer))
            {
                row.Remove("schema");
                row["schemaPresentedAt"] = pointer;
                changed = true;
            }
            else originals.Add(key, $"/interfaces/{index}/schema");
        }
        return changed;
    }

    private static bool ShareUnchangedReviewSnapshots(JsonObject context)
    {
        if (Text(context["review"]) != "completed_workflow" || context["priorConcerns"] is not JsonArray concerns) return false;
        var shared = false;
        foreach (var section in new[] { "actions", "scopes" })
        {
            if (context[section] is not JsonArray rows) continue;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject row || Text(row["id"]) is not { } id) continue;
                foreach (var concern in concerns.OfType<JsonObject>())
                {
                    if (concern["targets"] is not JsonArray { Count: 1 } targets || Text(targets[0]) != id ||
                        !JsonNode.DeepEquals(row, concern["current"]) || !JsonNode.DeepEquals(row, concern["before"])) continue;
                    concern.Remove("before"); concern.Remove("current");
                    concern["unchangedAt"] = $"/{section}/{index}";
                    shared = true;
                }
            }
        }
        return shared;
    }

    private static bool ShareReviewedOperationDetails(JsonObject context)
    {
        if (Text(context["review"]) is not ("current_block" or "completed_workflow") || context["actions"] is not JsonArray actions)
            return false;
        var originals = new Dictionary<(string Operation, string Contract, string Meaning, string Ports), int>();
        var shared = false;
        for (var index = 0; index < actions.Count; index++)
        {
            if (actions[index] is not JsonObject action || Text(action["kind"]) != "operation" ||
                Text(action["operation"]) is not { Length: > 0 } operation || Text(action["contract"]) is not { Length: > 0 } contract ||
                Text(action["meaning"]) is not { Length: > 0 } meaning || action["ports"] is not JsonArray ports)
                continue;
            var exactPorts = ports.ToJsonString();
            if (meaning.Length + exactPorts.Length < 256) continue;
            var key = (operation, contract, meaning, exactPorts);
            if (originals.TryAdd(key, index)) continue;
            action.Remove("meaning");
            action.Remove("ports");
            action["contractDetailsAt"] = $"/actions/{originals[key]}";
            shared = true;
        }
        return shared;
    }

    private static bool ShareEvidenceAncestors(JsonObject evidence,
        Dictionary<(string Id, string Version, string Hash, string Ancestors), string> originals)
    {
        var changed = false;
        foreach (var (address, value) in evidence)
        {
            if (value is not JsonObject row || row["schema"] is not JsonObject || row["ancestors"] is not JsonArray { Count: > 0 } ancestors ||
                row.ContainsKey("ancestorsPresentedAt") || !Identity(row, out var id, out var version, out var hash)) continue;
            var exact = ancestors.ToJsonString();
            var key = (id, version, hash, exact);
            if (!originals.TryGetValue(key, out var at))
                originals.Add(key, "/evidence/" + PlanningSchemaReferences.Escape(address) + "/ancestors");
            else if (exact.Length > at.Length + 32)
            {
                row.Remove("ancestors");
                row["ancestorsPresentedAt"] = at;
                changed = true;
            }
        }
        return changed;
    }

    private static bool ShareRepeatedTaskPages(JsonObject context)
    {
        if (context["evidence"] is not JsonObject evidence) return false;
        var pages = evidence.Where(pair => pair.Key.StartsWith("page:tasks:", StringComparison.Ordinal))
            .Select(pair => pair.Value).OfType<JsonObject>().Where(page => page["items"] is JsonArray { Count: > 0 }).ToArray();
        if (pages.Length == 0) return false;
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, scope) in evidence.Where(pair => pair.Key.StartsWith("scope:", StringComparison.Ordinal)))
        {
            if (scope is not JsonObject scopeView) continue;
            foreach (var phase in new[] { "tasks", "always" })
                if (scopeView[phase] is JsonArray { Count: > 0 } rows)
                    locations.TryAdd(rows.ToJsonString(), "/evidence/" + PlanningSchemaReferences.Escape(address) + "/" + phase);
        }
        var changed = false;
        foreach (var page in pages)
        {
            var items = page["items"]!.ToJsonString();
            if (!locations.TryGetValue(items, out var pointer) || items.Length <= pointer.Length + 200) continue;
            page.Remove("items");
            page["itemsPresentedAt"] = pointer;
            changed = true;
        }
        return changed;
    }

    private static bool ShareRepeatedTaskViews(JsonObject context)
    {
        var evidence = context["evidence"] as JsonObject ?? [];
        var declarations = new Dictionary<(string Id, string Scope, string Phase, string Json), string>();
        foreach (var (address, node) in evidence)
        {
            if (node is not JsonObject scope || Text(scope["path"]) is not { Length: > 0 } path) continue;
            foreach (var phase in new[] { "tasks", "always" })
                if (scope[phase] is JsonArray tasks)
                    for (var index = 0; index < tasks.Count; index++)
                        if (tasks[index] is JsonObject task && Text(task["id"]) is { Length: > 0 } id && task["kind"] is JsonValue)
                            declarations.TryAdd((id, path, phase, task.ToJsonString()),
                                "/evidence/" + PlanningSchemaReferences.Escape(address) + "/" + phase + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var changed = false;
        foreach (var item in (context["interfaces"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var producer in (item["producers"] as JsonArray ?? []).OfType<JsonObject>())
                if (producer["task"] is JsonObject task && Pointer(producer, task) is { } pointer && pointer.Length + 24 < task.ToJsonString().Length)
                { producer.Remove("task"); producer["taskPresentedAt"] = pointer; changed = true; }
        foreach (var row in evidence.Select(pair => pair.Value).OfType<JsonObject>())
        {
            if (row["kind"] is not JsonValue || row.ContainsKey("taskPresentedAt")) continue;
            var task = row.DeepClone().AsObject(); task.Remove("scope"); task.Remove("phase");
            if (Pointer(row, task) is not { } pointer) continue;
            var view = new JsonObject { ["id"] = row["id"]!.DeepClone(), ["scope"] = row["scope"]!.DeepClone(),
                ["phase"] = row["phase"]!.DeepClone(), ["taskPresentedAt"] = pointer };
            if (view.ToJsonString().Length >= row.ToJsonString().Length) continue;
            row.Clear(); foreach (var (key, value) in view) row[key] = value?.DeepClone(); changed = true;
        }
        return changed;

        string? Pointer(JsonObject location, JsonObject task)
        {
            var id = Text(location["id"]); var scope = Text(location["scope"]); var phase = Text(location["phase"]);
            return id.Length > 0 && Text(task["id"]) == id && scope.Length > 0 && phase is "tasks" or "always" &&
                declarations.TryGetValue((id, scope, phase, task.ToJsonString()), out var pointer) ? pointer : null;
        }
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
        var evidence = (context["evidence"] as JsonObject ?? []).Select(entry => entry.Value).OfType<JsonObject>().ToArray();
        foreach (var contract in evidence)
            if (Identity(contract, out _, out _, out _)) Factor(contract, "inputSchema");
        foreach (var operation in (context["operations"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Identity(operation, out var id, out _, out _) || operation["selectedInputs"] is JsonArray { Count: > 0 } ||
                evidence.Any(view => Text(view["id"]) == id && Text(view["pointer"]).StartsWith("/input", StringComparison.Ordinal))) continue;
            Factor(operation, "inputSchema");
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
