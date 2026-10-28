using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The model sees business tasks and ports, never executor graph plumbing.</summary>
internal static class PlanningSchemas
{
    internal static JsonObject RepairSchema(PlanningSession state)
    {
        if (!PlanningRepairPatch.Active(state))
            throw new PlanningConflictException("Typed repair requires an existing plan and exact edit authority.");
        return PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state));
    }

    // A shared semantic type grammar for compiler/repair tooling. This is not a
    // model response envelope and contains no discovery or requirement actions.
    internal static JsonObject TaskPlanGrammar(PlanningSession state)
    {
        var definitions = SemanticDefinitions();
        definitions["value"]!["anyOf"]!.AsArray().Add((JsonNode)Described(Object(("kind", Enum("lookup")), ("port", Ref("goal")),
            ("items", Array(Ref("value"), 2, 2))), "items=[records,IDs]; port=key. Preserve order/repeats; reject missing/ambiguous IDs."));
        definitions["task"] = Tasks(state, definitions);
        var grammar = definitions["plan"]!.DeepClone().AsObject();
        grammar["$defs"] = definitions;
        return grammar;
    }

    // Shared semantic grammar only: no discovery actions, requirement capture,
    // operation catalog traversal or unrelated task alternatives.
    internal static JsonObject ConstructionDefinitions(PlanningSession state, string kind)
    {
        var definitions = SemanticDefinitions();
        definitions.Remove("plan");
        definitions["value"]!["anyOf"]!.AsArray().Add((JsonNode)Described(Object(("kind", Enum("lookup")), ("port", Ref("goal")),
            ("items", Array(Ref("value"), 2, 2))), "items=[records,IDs]; port=key. Preserve order/repeats; reject missing/ambiguous IDs."));
        definitions["task"] = new JsonObject { ["anyOf"] = new JsonArray(TaskShapes(state, kind).ToArray()) };
        return definitions;
    }

    private static JsonObject SemanticDefinitions()
    {
        return new JsonObject
        {
            ["id"] = Identity(), ["goal"] = Nonblank(),
            ["identities"] = Array(Identity()),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("output")))),
                Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Described(Object(("kind", Enum("json", "flatten")), ("items", Array(Ref("value"), 1, 1))), "json encodes one value; flatten concatenates one typed array-of-arrays level in order."),
                Described(Object(("kind", Enum("field")), ("items", Array(Ref("value"), 1, 1)), ("port", Ref("goal"))), "Select one literal declared field from a typed object/item; nest for nested fields."),
                Object(("kind", Enum("input")), ("source", String())),
                Object(("kind", Enum("choice", "present")), ("source", Ref("id"))),
                Object(("kind", Enum("output")), ("source", Ref("id")), ("port", Nullable(String()))),
                Object(("kind", Enum("item", "index"))),
                Object(("kind", Enum("arithmetic")), ("text", Enum("negate")), ("items", Array(Ref("value"), 1, 1))),
                Object(("kind", Enum("arithmetic")), ("text", Enum("add", "subtract", "multiply", "divide", "remainder")), ("items", Array(Ref("value"), 2, 2))),
                Object(("kind", Enum("predicate")), ("predicate", Enum("not")), ("items", Array(Ref("value"), 1, 1))),
                Object(("kind", Enum("predicate")), ("predicate", Enum("and", "or", "equal", "not_equal", "less", "less_equal", "greater", "greater_equal")), ("items", Array(Ref("value"), 2, 2)))) },
            ["literal"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Object(("name", String()), ("value", Ref("literal")))))),
                Object(("kind", Enum("array")), ("items", Array(Ref("literal"))))) },
            ["businessType"] = BusinessTypes(transform: false),
            ["field"] = Input(objectField: true),
            ["resultType"] = BusinessTypes(transform: true),
            ["resultField"] = Object(("name", Ref("goal")), ("type", Ref("resultType"))),
            ["input"] = Described(Input(objectField: false), "Omitted required=true/default=absent. Declare inputs once."),
            ["output"] = Object(("name", String()), ("value", Ref("value"))),
            ["plan"] = Object(("inputs", Array(Ref("input"))), ("root", Ref("scope")), ("groups", Array(Ref("group"))), ("choices", Array(Ref("choice")))),
            ["scope"] = Object(("tasks", Array(Ref("task"))), ("outputs", Array(Ref("output"))), ("always", Array(Ref("task")))),
            ["group"] = Object(("id", Ref("id")), ("inputs", Array(Ref("input"))), ("body", Ref("scope"))),
            ["choice"] = Object(("id", Ref("id")), ("question", Ref("goal")), ("type", Ref("businessType")),
                ("alternatives", Array(Object(("id", Ref("goal")), ("description", String()), ("value", Ref("literal"))), 2)),
                ("recommended", String()))
        };
    }

    internal static JsonObject Compact(JsonObject root, bool preserveDescriptions = false)
    {
        // Contract descriptions already accompany the admitted operations in the
        // prompt. Remove only schema annotations, never properties named description.
        if (!preserveDescriptions)
            foreach (var (_, schema) in PlanningSchemaReferences.Walk(root, "", 0)) schema.Remove("description");
        var definitions = root["$defs"]!.AsObject();
        ShareRepeatedSchemas(root, definitions);
        for (var pass = 0; pass < 3; pass++) { ShareRepeatedSchemas(root, definitions, 30); CollapseAliases(root, definitions); PruneDefinitions(root, definitions); }
        CompactDefinitionNames(root, definitions);
        return root;
    }

    internal static JsonObject Clarifications() => Array(Object(("id", Nonblank()), ("question", Nonblank()),
        ("alternatives", Array(Object(("id", Nonblank()), ("description", Nonblank())), 0, 3)),
        ("recommended", Nullable(Nonblank()))), 1, 3);

    private static void CompactDefinitionNames(JsonObject root, JsonObject definitions)
    {
        var names = definitions.Select((p, i) => (p.Key, Name: i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)))
            .ToDictionary(p => p.Key, p => p.Name, StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"]?.ToString() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                    obj["$ref"] = "#/$defs/" + names[reference[8..]];
                foreach (var value in obj.Select(p => p.Value)) Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(root);
        var renamed = definitions.Select(p => (Name: names[p.Key], p.Value)).ToArray();
        definitions.Clear(); foreach (var (name, value) in renamed) definitions.Add(name, value);
    }

    private static void PruneDefinitions(JsonObject root, JsonObject definitions)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"]?.ToString() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal) && used.Add(reference[8..]))
                    Visit(definitions[reference[8..]]);
                foreach (var (key, value) in obj) if (key != "$defs") Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(root);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(key);
    }

    private static void CollapseAliases(JsonObject root, JsonObject definitions)
    {
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"]?.ToString() is { } reference)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    while (reference.StartsWith("#/$defs/", StringComparison.Ordinal) && seen.Add(reference) &&
                        definitions[reference[8..]] is JsonObject { Count: 1 } alias && alias["$ref"]?.ToString() is { } next) reference = next;
                    obj["$ref"] = reference;
                }
                foreach (var value in obj.Select(p => p.Value)) Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(root);
    }

    // Lossless JSON Schema factoring keeps per-operation domains affordable.
    // DTO values and contract checks do not change; only duplicate wire schemas do.
    internal static void ShareRepeatedSchemas(JsonObject root, JsonObject definitions, int minimumSize = 80,
        IEnumerable<JsonObject>? schemaNodes = null, string referencePrefix = "#/$defs/")
    {
        var nodes = new List<JsonObject>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if ((obj["type"] is JsonValue || obj["anyOf"] is JsonArray) && obj["properties"]?["dependsOn"] is null &&
                    !(obj.Parent is JsonObject parent && (ReferenceEquals(parent["kind"], obj) || ReferenceEquals(parent["slot"], obj) || ReferenceEquals(parent["action"], obj))))
                    nodes.Add(obj);
                foreach (var value in obj.Select(p => p.Value)) Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        if (schemaNodes is null) Visit(root); else nodes.AddRange(schemaNodes);
        var groups = nodes.GroupBy(n => n.ToJsonString(), StringComparer.Ordinal).Where(g => g.Count() > 1 && g.Key.Length > minimumSize)
            .OrderByDescending(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            // Factoring a parent detaches its old children. Count only live sites.
            var attached = group.Where(n =>
            {
                JsonNode top = n; while (top.Parent is not null) top = top.Parent;
                return ReferenceEquals(top, root);
            }).ToArray();
            if (attached.Length < 2) continue;
            var schema = attached[0]; var ordinal = definitions.Count;
            while (definitions.ContainsKey("s" + ordinal)) ordinal++;
            var name = "s" + ordinal;
            JsonObject Reference() => new() { ["$ref"] = referencePrefix + name };
            var referenceLength = Reference().ToJsonString().Length;
            var size = schema.ToJsonString().Length;
            if ((size - referenceLength) * attached.Length <= size + name.Length + 4) continue;
            definitions[name] = schema.DeepClone();
            foreach (var node in attached)
                if (node.Parent is JsonArray array) array[array.IndexOf(node)] = Reference();
                else if (node.Parent is JsonObject obj) obj[obj.Single(p => ReferenceEquals(p.Value, node)).Key] = Reference();
        }
    }
    // Omitted flags use the existing DTO defaults. Every actual business value stays explicit.
    private static JsonObject BusinessTypes(bool transform)
    {
        var alternatives = new List<JsonNode?>();
        foreach (var nullable in new[] { false, true })
        {
            JsonObject Shape(params (string Name, JsonObject Schema)[] fields) => Object(nullable ? [..fields, ("nullable", Boolean(true))] : fields);
            var nested = transform ? "resultType" : "businessType";
            alternatives.AddRange(ArrayTypeShapes(nested, nullable));
            alternatives.Add(Shape(("kind", Enum("object")), ("fields", Array(Ref(transform ? "resultField" : "field")))));
            alternatives.Add(Shape(("kind", Enum("string")), ("enum", Array(String(), 1, 256))));
            alternatives.Add(Shape(("kind", transform ? Enum("string", "number", "integer", "boolean") : Enum("string", "number", "integer", "boolean", "any"))));
        }
        return new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
    }

    private static IEnumerable<JsonObject> ArrayTypeShapes(string nested, bool nullable)
    {
        var fields = new List<(string, JsonObject)> { ("kind", Enum("array")), ("items", Ref(nested)) };
        fields.Add(("minItems", Nullable(Integer(0, int.MaxValue))));
        fields.Add(("maxItems", Nullable(Integer(0, int.MaxValue))));
        if (nullable) fields.Add(("nullable", Boolean(true)));
        yield return Object(fields.ToArray());
    }

    private static JsonObject Input(bool objectField)
    {
        var alternatives = new List<JsonNode?>();
        foreach (var required in new[] { true, false })
            foreach (var hasDefault in new[] { false, true })
            {
                if (!objectField && !required && !hasDefault) continue;
                var fields = new List<(string Name, JsonObject Schema)> { ("name", String()), ("type", Ref("businessType")) };
                if (!required) fields.Add(("required", Boolean(false)));
                if (hasDefault) fields.Add(("default", Ref("literal")));
                alternatives.Add(Object(fields.ToArray()));
            }
        return new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
    }

    private static JsonObject Boolean(bool value) => new() { ["type"] = "boolean", ["enum"] = new JsonArray(value) };
    private static JsonObject TaskShape(string kind, params (string Name, JsonObject Schema)[] fields) =>
        Object(new (string Name, JsonObject Schema)[]
        { ("id", Ref("id")), ("kind", Enum(kind)), ("objective", Ref("goal")), ("dependsOn", Ref("identities")),
          ("requires", Nullable(Ref("value"))) }.Concat(fields).ToArray());

    private static JsonObject Tasks(PlanningSession state, JsonObject definitions)
    {
        var operations = OperationTasks(state, definitions, (ids, inputs) => TaskShape("operation", ("operation", ids), ("inputs", inputs)));
        return new() { ["anyOf"] = new JsonArray(operations.Concat(TaskShapes(state, null)).ToArray()) };
    }

    private static IEnumerable<JsonNode?> TaskShapes(PlanningSession state, string? kind)
    {
        if (kind is null or "value")
            yield return Described(TaskShape("value", ("outputs", Array(Ref("output")))), "Copies/assembles values; objectives do not compute.");
        if (kind is null or "transform")
        {
            yield return Described(TaskShape("transform", ("mode", Enum("extract")),
                ("each", Object(("input", Described(Ref("id"), "Exact named binding in this task's inputs.")),
                    ("output", Described(Ref("id"), "Exact name of the sole array result field.")))), ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", Array(Object(("name", Ref("goal")),
                    ("type", new JsonObject { ["anyOf"] = new JsonArray(ArrayTypeShapes("resultType", false).Cast<JsonNode?>().ToArray()) })), 1, 1))))),
                "Independent extraction: each names one collection input and the sole array result field. Produce exactly one result per item in order; nested arrays stay nested. No global comparison, filtering or implicit flattening. Shared inputs are read-only. Mapping examples are bounded; " +
                (state.Request.Options["mapping_profile"]?.ToString() == TaskPlanCompiler.AdaptiveMappingProfile
                    ? "all items are checked under the configured shared runtime budget; unresolved items may require specialization."
                    : "all items are checked under one shared two-attempt allowance."));
            yield return Described(TaskShape("transform", ("mode", Enum("extract", "interpret")), ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", NonEmptyArray(Ref("resultField")))))),
                "Extract observed data or interpret it explicitly. Copies use value/field/object bindings. Declare result fields; no invented defaults.");
        }
        if (kind is null or "sequence") yield return TaskShape("sequence", ("body", Ref("scope")));
        if (kind is null or "conditional") yield return TaskShape("conditional", ("condition", Ref("value")), ("body", Ref("scope")), ("otherwise", Ref("scope")));
        if (kind is null or "parallel") yield return TaskShape("parallel", ("branches", Array(Ref("scope"), 2)), ("maxConcurrency", Integer(1, 100)));
        if (kind is null or "foreach")
            yield return TaskShape("foreach", ("items", Ref("value")), ("body", Ref("scope")), ("parallel", Type("boolean")), ("maxItems", Described(Integer(1, 10000),
                "TOTAL iteration ceiling, not a nonempty guarantee; declare array cardinality in its type.")), ("maxConcurrency", Integer(1, 100)));
        if (kind is null or "call") yield return TaskShape("call", ("group", Ref("id")), ("inputs", Array(Ref("output"))));
    }
    private static IEnumerable<JsonNode?> OperationTasks(PlanningSession state, JsonObject definitions, Func<JsonObject, JsonObject, JsonObject> task)
    {
        var fixedOperations = TaskPlanRevisions.FixedOperations(state)
            ? TaskPlanRevisions.Tasks(state.Plan!).Where(t => t.Kind == "operation").Select(t => t.Operation).ToHashSet(StringComparer.Ordinal) : null;
        var resolved = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .Where(c => state.Catalog!.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
            .GroupBy(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal).Where(g => g.Count() == 1 && TaskOperations.Validate(g.First()).Count == 0)
            .Select(g => PlanningCapabilityArguments.Editable(g.First()))
            .Where(o => fixedOperations is null || fixedOperations.Contains(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var operations = resolved.Select(o => (o.Id, Inputs: Inputs(o))).ToArray();
        foreach (var group in operations.GroupBy(o => o.Inputs.ToJsonString(), StringComparer.Ordinal))
            yield return task(Enum(group.Select(o => o.Id).ToArray()), group.First().Inputs);
        string StepType(string id) => state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).First(c => TaskOperations.Describe(c).Id == id).StepType;

        JsonObject Inputs(PlanningOperation operation)
        {
            var ports = operation.Inputs.OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (p.Name, Value: PlanningBindingSchemas.For(p.Schema, definitions,
                    literalOnly: TaskPlanCompiler.LiteralScopeInput(StepType(operation.Id), p.Path[0]), workspace: TaskPlanCompiler.FixedWorkspaceInput(StepType(operation.Id), p.Path[0])))).ToArray();
            if (ports.Length == 0) return Array(Ref("output"), 0, 0);
            var bindings = ports.GroupBy(p => p.Value.ToJsonString(), StringComparer.Ordinal)
                .Select(g => (JsonNode?)Object(("name", Enum(g.Select(p => p.Name).ToArray())), ("value", g.First().Value))).ToArray();
            return Array(bindings.Length == 1 ? bindings[0]!.AsObject() : new JsonObject { ["anyOf"] = new JsonArray(bindings) });
        }
    }

    private static JsonObject Described(JsonObject schema, string description) { schema["description"] = description; return schema; }
    private static JsonObject Identity() => new() { ["type"] = "string", ["pattern"] = TaskPlanCompiler.IdentityPattern };
    internal static JsonObject Nonblank() => new() { ["type"] = "string", ["pattern"] = @"\S" };
    internal static JsonObject Integer(int minimum, int maximum) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    internal static JsonObject String() => Type("string");
    internal static JsonObject Type(string type) => new() { ["type"] = type };
    internal static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    internal static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    internal static JsonObject Array(JsonObject item, int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = item };
        if (minimum is { } min) schema["minItems"] = min;
        if (maximum is { } max) schema["maxItems"] = max;
        return schema;
    }
    internal static JsonObject NonEmptyArray(JsonObject item) { var array = Array(item); array["minItems"] = 1; return array; }
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
}
