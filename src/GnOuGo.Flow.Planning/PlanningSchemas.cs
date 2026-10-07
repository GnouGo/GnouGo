using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The model sees business tasks and ports, never executor graph plumbing.</summary>
internal static class PlanningSchemas
{
    internal static bool DeclaresOutputs(JsonNode? schema)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(schema?["properties"]?["requirements"]);
        bool Visit(JsonNode? node)
        {
            if (node is not JsonObject obj) return false;
            if (obj["properties"] is JsonObject properties && properties.ContainsKey("outputs")) return true;
            if (obj["$ref"]?.ToString() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal) && visited.Add(reference))
                return Visit(schema?["$defs"]?[reference[8..]]);
            return obj["anyOf"] is JsonArray alternatives && alternatives.Any(Visit);
        }
    }
    internal static bool HasQueryProperty(JsonNode? node) => node switch
    {
        JsonObject obj => obj["properties"] is JsonObject properties && properties.ContainsKey("query") || obj.Any(p => HasQueryProperty(p.Value)),
        JsonArray array => array.Any(HasQueryProperty),
        _ => false
    };

    internal static bool AllowsNoPlan(JsonNode? schema) =>
        schema?["properties"]?["discoveryRequests"]?["type"]?.ToString() == "null" &&
        schema["properties"]?["plan"]?["anyOf"] is JsonArray alternatives &&
        alternatives.Any(a => a?["type"]?.ToString() == "null");

    internal static JsonObject Proposal(PlanningSession state, IReadOnlySet<string>? admitted = null) => PlanningRepairPatch.Active(state)
        ? PlanningRepairPatch.Schema(state, FullProposal(state, compact: false)) : FullProposal(state, admitted: admitted);

    internal static JsonObject FullProposal(PlanningSession state, bool compact = true, bool clarifications = true, IReadOnlySet<string>? admitted = null, bool scopeGuidance = true)
    {
        var actions = new List<JsonNode?>();
        if (PlanningDiscoveryContext.CanDiscover(state))
            foreach (var source in state.Discovery.Sources)
            {
                var receipts = state.Discovery.Pages.Where(p => p.SourceId == source.Id).ToArray();
                // Even a small exhausted source may fall outside the global shortlist.
                var canRefine = receipts.Any(p => p.UnavailableReason is null && (p.NextCursor is not null || p.Capabilities.Count > 0));
                if (receipts.Length == 0 || canRefine)
                    actions.Add(Object(("sourceId", Enum(source.Id)), ("cursor", Type("null")),
                        ("query", Nullable(new JsonObject { ["type"] = "string", ["pattern"] = @"\S", ["maxLength"] = 512 })), ("operationIds", Type("null"))));
                foreach (var cursor in receipts.Select(p => p.NextCursor).OfType<string>().Distinct(StringComparer.Ordinal)
                    .Where(cursor => !receipts.Any(p => p.Cursor == cursor)))
                    actions.Add(Object(("sourceId", Enum(source.Id)), ("cursor", Enum(cursor)), ("query", Type("null")), ("operationIds", Type("null"))));
                if (receipts.Any(p => p.Capabilities.Count > 0) || state.Discovery.Inspections?.Any(i => i.SourceId == source.Id) == true)
                    actions.Add(Described(Object(("sourceId", Enum(source.Id)), ("cursor", Type("null")), ("query", Type("null")),
                        ("operationIds", Array(Ref("goal"), 0, receipts.SelectMany(p => p.Capabilities).Select(c => c.Operation?.Id).OfType<string>().Distinct(StringComparer.Ordinal).Count()))),
                        "Replace this source's inspection selection with already-discovered operation IDs; empty clears it. Selected exact contracts remain visible across pages. This grants no permission."));
            }
        var inspectedSources = state.Discovery.Sources.Where(s => state.Discovery.Pages.Any(p => p.SourceId == s.Id)).Select(s => s.Id).ToArray();
        var prerequisiteKinds = PlanningDiscoveryContext.Prerequisites(state).Where(kind =>
            !PlanningDiscoveryContext.Candidates(state).Any(c => PlanningDiscoveryContext.Artifacts(state, c)?.Produces.Any(a => a.Kind == kind) == true) ||
            state.Discovery.Pages.Any(p => p.ProducedArtifactKind == kind)).ToArray();
        if (PlanningDiscoveryContext.CanDiscover(state) && inspectedSources.Length > 0 && prerequisiteKinds.Length > 0)
            actions.Add(Object(("sourceId", Enum(inspectedSources)), ("cursor", Type("null")),
                ("query", Nullable(new JsonObject { ["type"] = "string", ["pattern"] = @"\S", ["maxLength"] = 512 })),
                ("operationIds", Type("null")), ("producedArtifactKind", Enum(prerequisiteKinds))));
        var root = Object(
            ("discoveryRequests", actions.Count == 0 ? Type("null") : Nullable(Array(new JsonObject { ["anyOf"] = new JsonArray(actions.ToArray()) }, 1, 4))),
            ("plan", Nullable(Ref("plan"))));
        if (clarifications)
        {
            root["properties"]!["clarifications"] = Nullable(Clarifications());
            root["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("clarifications"));
        }
        if (state.Requirements is null || clarifications && state.IntentVersion == 2 && state.Requirements.Inputs is null)
        {
            root["properties"]!["requirements"] = clarifications ? Nullable(Ref("requirements")) : Ref("requirements");
            root["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("requirements"));
        }
        root["$defs"] = new JsonObject
        {
            ["id"] = Identity(), ["goal"] = Nonblank(),
            ["identities"] = Array(Identity()),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("output")))),
                Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Described(Object(("kind", Enum("json")), ("items", Array(Ref("value"), 1, 1))), "Serialize one business value as JSON text; no inference."),
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
            ["requirements"] = Object(("summary", String()), ("outcomes", NonEmptyArray(Object(("id", String()), ("description", String()))))),
            ["plan"] = Object(("inputs", Array(Ref("input"))), ("root", Ref("scope")), ("groups", Array(Ref("group"))), ("choices", Array(Ref("choice")))),
            ["scope"] = Object(("tasks", Array(Ref("task"))), ("outputs", Array(Ref("output"))), ("always", Array(Ref("task")))),
            ["group"] = Object(("id", Ref("id")), ("inputs", Array(Ref("input"))), ("body", Ref("scope"))),
            ["choice"] = Object(("id", Ref("id")), ("question", Ref("goal")), ("type", Ref("businessType")),
                ("alternatives", Array(Object(("id", Ref("goal")), ("description", String()), ("value", Ref("literal"))), 2)),
                ("recommended", String()))
        };
        var definitions = root["$defs"]!.AsObject();
        if (clarifications)
        {
            definitions["requirements"]!["properties"]!["inputs"] = Nullable(Array(Ref("input")));
            definitions["requirements"]!["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("inputs"));
            definitions["requirements"]!["properties"]!["outputs"] = Array(Object(("name", Ref("goal")), ("type", Ref("businessType")), ("required", Type("boolean"))));
            definitions["requirements"]!["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("outputs"));
            if (compact)
            {
                // Already explained in the prompt; omit duplicate annotations only.
                definitions["input"]!.AsObject().Remove("description");
                foreach (var value in definitions["value"]!["anyOf"]!.AsArray().OfType<JsonObject>()) value.Remove("description");
            }
        }
        definitions["task"] = Tasks(state, definitions, admitted);
        if (scopeGuidance)
        {
            definitions["scope"]!["properties"]!["outputs"]!["description"] = "Explicit business exports via enclosingTask.export. Conditional alternatives project the same consumer-facing ports, types, nullability and requiredness from their own values. Prefer direct ports over differently shaped whole results. Nullable ports need explicit null guards on the consuming path; a separate boolean does not prove non-null.";
            definitions["scope"]!["properties"]!["always"]!["description"] = "Failure-path work. Preserve available payloads here before nested cleanup; hidden descendants and absent results remain inaccessible.";
            foreach (var task in definitions["task"]!["anyOf"]!.AsArray())
                task!["properties"]!["dependsOn"]!["description"] = "Eligible same-scope tasks only. Business bindings already establish data dependencies; available ancestor values use captures without cross-scope dependsOn.";
            foreach (var value in definitions["value"]!["anyOf"]!.AsArray().OfType<JsonObject>())
                if (value["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(k => k?.ToString() == "present"))
                    value["description"] = "Choice selects a declared alternative. present tests preceding local or lexical-ancestor task completion, never a hidden descendant or the presence of a business field.";
        }
        if (state.Requirements is not null && (!clarifications || state.IntentVersion != 2 || state.Requirements.Inputs is not null)) root["$defs"]!.AsObject().Remove("requirements");
        return compact ? Compact(root) : root;
    }

    internal static JsonObject Compact(JsonObject root)
    {
        // Contract descriptions already accompany the admitted operations in the
        // prompt. Remove only schema annotations, never properties named description.
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
    private static void ShareRepeatedSchemas(JsonObject root, JsonObject definitions, int minimumSize = 80)
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
        Visit(root);
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
            var referenceLength = Ref(name).ToJsonString().Length;
            var size = schema.ToJsonString().Length;
            if ((size - referenceLength) * attached.Length <= size + name.Length + 4) continue;
            definitions[name] = schema.DeepClone();
            foreach (var node in attached)
                if (node.Parent is JsonArray array) array[array.IndexOf(node)] = Ref(name);
                else if (node.Parent is JsonObject obj) obj[obj.Single(p => ReferenceEquals(p.Value, node)).Key] = Ref(name);
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
            alternatives.Add(Shape(("kind", Enum("array")), ("items", Ref(nested))));
            alternatives.Add(Shape(("kind", Enum("object")), ("fields", Array(Ref(transform ? "resultField" : "field")))));
            alternatives.Add(Shape(("kind", Enum("string")), ("enum", Array(String(), 1, 256))));
            alternatives.Add(Shape(("kind", transform ? Enum("string", "number", "integer", "boolean") : Enum("string", "number", "integer", "boolean", "any"))));
        }
        return new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
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
    private static JsonObject Tasks(PlanningSession state, JsonObject definitions, IReadOnlySet<string>? admitted = null)
    {
        JsonObject Task(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new (string Name, JsonObject Schema)[]
        { ("id", Ref("id")), ("kind", Enum(kind)), ("objective", Ref("goal")), ("dependsOn", Ref("identities")),
          ("requires", Nullable(Ref("value"))) }.Concat(fields).ToArray());
        var operations = OperationTasks(state, definitions, (ids, inputs) => Task("operation", ("operation", ids), ("inputs", inputs)), admitted);
        return new() { ["anyOf"] = new JsonArray(operations.Concat(new JsonNode?[] {
            Described(Task("value", ("outputs", Array(Ref("output")))), "Copies/assembles values; objectives do not compute."),
            Described(Task("transform", ("mode", Enum("extract")),
                ("each", Object(("input", Described(Ref("id"), "Exact named binding in this task's inputs.")),
                    ("output", Described(Ref("id"), "Exact name of the sole array result field.")))), ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", Array(Object(("name", Ref("goal")),
                    ("type", Object(("kind", Enum("array")), ("items", Ref("resultType"))))), 1, 1))))),
                "Independent extraction: each names one collection input and the sole array result field. Produce exactly one result per item in order; nested arrays stay nested. No global comparison, filtering or implicit flattening. Shared inputs are read-only. Mapping examples are bounded; all items are checked under one shared two-attempt allowance."),
            Described(Task("transform", ("mode", Enum("extract", "interpret")), ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", NonEmptyArray(Ref("resultField")))))),
                "Extract observed data or interpret it explicitly. Copies use value/field/object bindings. Declare result fields; no invented defaults."),
            Described(Task("transform", ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", NonEmptyArray(Ref("resultField")))))),
                "Historical interpretation form; omission of mode retains interpret semantics."),
            Task("sequence", ("body", Ref("scope"))),
            Task("conditional", ("condition", Ref("value")), ("body", Ref("scope")), ("otherwise", Ref("scope"))),
            Task("parallel", ("branches", Array(Ref("scope"), 2)), ("maxConcurrency", Integer(1, 100))),
            Task("foreach", ("items", Ref("value")), ("body", Ref("scope")), ("parallel", Type("boolean")), ("maxItems", Described(Integer(1, 10000), "TOTAL items limit: requested bound, else 100. Excess fails. 1 accepts only a singleton, regardless of workers.")), ("maxConcurrency", Integer(1, 100))),
            Task("call", ("group", Ref("id")), ("inputs", Array(Ref("output")))) }).ToArray()) };
    }
    private static IEnumerable<JsonNode?> OperationTasks(PlanningSession state, JsonObject definitions, Func<JsonObject, JsonObject, JsonObject> task, IReadOnlySet<string>? admitted)
    {
        var fixedOperations = TaskPlanRevisions.FixedOperations(state)
            ? TaskPlanRevisions.Tasks(state.Plan!).Where(t => t.Kind == "operation").Select(t => t.Operation).ToHashSet(StringComparer.Ordinal) : null;
        var resolved = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .Where(c => state.Catalog!.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
            .GroupBy(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal).Where(g => g.Count() == 1 && TaskOperations.Validate(g.First()).Count == 0)
            .Select(g => PlanningCapabilityArguments.Editable(g.First()))
            .Where(o => (fixedOperations is null || fixedOperations.Contains(o.Id)) && (admitted is null || admitted.Contains(o.Id))).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
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
