using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The model sees business tasks and ports, never executor graph plumbing.</summary>
internal static class PlanningSchemas
{
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

    internal static JsonObject Proposal(PlanningSession state) => PlanningRepairPatch.Active(state)
        ? PlanningRepairPatch.Schema(state, FullProposal(state, compact: false)) : FullProposal(state);

    internal static JsonObject FullProposal(PlanningSession state, bool compact = true, bool clarifications = true)
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
        if (state.Requirements is null || clarifications && state.IntentVersion == 1 && state.Requirements.Inputs is null)
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
            if (compact)
            {
                // Already explained in the prompt; omit duplicate annotations only.
                definitions["input"]!.AsObject().Remove("description");
                foreach (var value in definitions["value"]!["anyOf"]!.AsArray().OfType<JsonObject>()) value.Remove("description");
            }
        }
        if (state.OutcomeVersion == 1 && clarifications)
        {
            var outcome = definitions["requirements"]!["properties"]!["outcomes"]!["items"]!;
            outcome["properties"]!["execution"] = Enum("data", "read", "write", "execute", "lifecycle");
            outcome["properties"]!["always"] = Type("boolean");
            outcome["properties"]!["conditional"] = Type("boolean");
            foreach (var name in new[] { "execution", "always", "conditional" }) outcome["required"]!.AsArray().Add((JsonNode?)JsonValue.Create(name));
            root["properties"]!["outcomeBindings"] = Nullable(Array(Object(
                ("outcomeId", state.Requirements is { } accepted ? Enum(accepted.Outcomes.Select(o => o.Id).ToArray()) : Nonblank()),
                ("taskIds", Array(Ref("id"))), ("outputs", Array(Nonblank()))), 1));
            root["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("outcomeBindings"));
        }
        definitions["task"] = Tasks(state, definitions);
        if (state.Requirements is not null && (!clarifications || state.IntentVersion != 1 || state.Requirements.Inputs is not null)) root["$defs"]!.AsObject().Remove("requirements");
        if (compact) { ShareRepeatedSchemas(root, definitions); CompactDefinitionNames(root, definitions); }
        return root;
    }

    internal static JsonObject Clarifications() => Array(Object(("id", Nonblank()), ("question", Nonblank()),
        ("alternatives", Array(Object(("id", Nonblank()), ("description", Nonblank())), 0, 3)),
        ("recommended", Nullable(Nonblank()))), 1, 3);

    private static void CompactDefinitionNames(JsonObject root, JsonObject definitions)
    {
        var names = definitions.Select((p, i) => (p.Key, Name: "d" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)))
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

    // Lossless JSON Schema factoring keeps per-operation domains affordable.
    // DTO values and contract checks do not change; only duplicate wire schemas do.
    private static void ShareRepeatedSchemas(JsonObject root, JsonObject definitions)
    {
        var nodes = new List<JsonObject>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if ((obj["type"] is JsonValue || obj["anyOf"] is JsonArray) && obj["properties"]?["dependsOn"] is null &&
                    !(obj.Parent is JsonObject parent && ReferenceEquals(parent["kind"], obj)))
                    nodes.Add(obj);
                foreach (var value in obj.Select(p => p.Value)) Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(root);
        var groups = nodes.GroupBy(n => n.ToJsonString(), StringComparer.Ordinal).Where(g => g.Count() > 1 && g.Key.Length > 80)
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
            var schema = attached[0]; var name = "s" + definitions.Count;
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
    private static JsonObject Tasks(PlanningSession state, JsonObject definitions)
    {
        JsonObject Task(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new (string Name, JsonObject Schema)[]
        { ("id", Ref("id")), ("kind", Enum(kind)), ("objective", Ref("goal")), ("dependsOn", Ref("identities")) }.Concat(fields).ToArray());
        var operations = OperationTasks(state, definitions, (ids, inputs) => Task("operation", ("operation", ids), ("inputs", inputs)));
        return new() { ["anyOf"] = new JsonArray(operations.Concat(new JsonNode?[] {
            Described(Task("value", ("outputs", Array(Ref("output")))), "Copies/assembles values; objectives do not compute."),
            Described(Task("transform", ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", NonEmptyArray(Ref("resultField")))))),
                "LLM interpretation only, never copying/extraction/validation. Preserve domains; typed fields, no defaults/opacity."),
            Task("sequence", ("body", Ref("scope"))),
            Task("conditional", ("condition", Ref("value")), ("body", Ref("scope")), ("otherwise", Ref("scope"))),
            Task("parallel", ("branches", Array(Ref("scope"), 2)), ("maxConcurrency", Integer(1, 100))),
            Task("foreach", ("items", Ref("value")), ("body", Ref("scope")), ("parallel", Type("boolean")), ("maxItems", Described(Integer(1, 10000), "TOTAL items limit: requested bound, else 100. Excess fails. 1 accepts only a singleton, regardless of workers.")), ("maxConcurrency", Integer(1, 100))),
            Task("call", ("group", Ref("id")), ("inputs", Array(Ref("output")))) }).ToArray()) };
    }
    private static IEnumerable<JsonNode?> OperationTasks(PlanningSession state, JsonObject definitions, Func<JsonObject, JsonObject, JsonObject> task)
    {
        var fixedOperations = TaskPlanRevisions.FixedOperations(state)
            ? TaskPlanRevisions.Tasks(state.Plan!).Where(t => t.Kind == "operation").Select(t => t.Operation).ToHashSet(StringComparer.Ordinal) : null;
        var resolved = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .GroupBy(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal).Where(g => g.Count() == 1 && TaskOperations.Validate(g.First()).Count == 0)
            .Select(g => PlanningCapabilityArguments.Editable(g.First()))
            .Where(o => fixedOperations is null || fixedOperations.Contains(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        if (resolved.Length == 0 && fixedOperations is null && !state.Discovery.Pages.SelectMany(p => p.Capabilities).Any(c => c.Operation is not null))
        { yield return task(String(), Array(Ref("output"))); yield break; }
        // Index-only domains are compact discovery hints, not resolved contracts.
        // Keep these operations selectable; exact resolution validates ownership,
        // versions and values before graph emission and before any repair request.
        var known = resolved.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var operations = resolved.Select(o => (o.Id, Inputs: Inputs(o))).ToArray();
        foreach (var group in operations.GroupBy(o => o.Inputs.ToJsonString(), StringComparer.Ordinal))
            yield return task(Enum(group.Select(o => o.Id).ToArray()), group.First().Inputs);
        var unresolved = state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Operation?.Id)
            .OfType<string>().Where(id => !known.Contains(id) && (fixedOperations is null || fixedOperations.Contains(id)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unresolved.Length > 0) yield return task(Enum(unresolved), Array(Ref("output")));

        JsonObject Inputs(PlanningOperation operation)
        {
            var ports = operation.Inputs.OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (p.Name, Value: DomainValue(p.Schema, definitions))).ToArray();
            if (ports.Length == 0) return Array(Ref("output"), 0, 0);
            var bindings = ports.GroupBy(p => p.Value.ToJsonString(), StringComparer.Ordinal)
                .Select(g => (JsonNode?)Object(("name", Enum(g.Select(p => p.Name).ToArray())), ("value", g.First().Value))).ToArray();
            return Array(bindings.Length == 1 ? bindings[0]!.AsObject() : new JsonObject { ["anyOf"] = new JsonArray(bindings) });
        }
    }

    internal static JsonObject DomainValue(JsonObject schema, JsonObject definitions)
    {
        var declared = TaskOperations.FiniteDomain(schema);
        var values = declared.ContainsKey("const") ? new JsonArray([declared["const"]?.DeepClone()]) : declared["enum"]?.DeepClone() as JsonArray;
        if (values is null || values.Any(v => v is JsonObject or JsonArray)) return Ref("value");
        if (declared.ContainsKey("const") && declared["enum"] is JsonArray allowed && !allowed.Any(v => JsonNode.DeepEquals(v, declared["const"]))) values.Clear();
        if (!definitions.ContainsKey("binding"))
        {
            var all = definitions["value"]!["anyOf"]!.AsArray();
            var bindings = all.Where(v => v!["properties"]!["kind"]!["enum"]![0]!.ToString() is not ("null" or "string" or "number" or "boolean" or "object" or "array")).ToArray();
            definitions["binding"] = new JsonObject { ["anyOf"] = new JsonArray(bindings.Select(v => v!.DeepClone()).ToArray()) };
            foreach (var binding in bindings) all.Remove(binding);
            all.Add((JsonNode)Ref("binding"));
        }
        var alternatives = new JsonArray(Ref("binding"));
        foreach (var group in values.GroupBy(v => v?.GetValueKind() ?? System.Text.Json.JsonValueKind.Null).OrderBy(g => g.Key))
        {
            var type = group.Key switch { System.Text.Json.JsonValueKind.String => "string", System.Text.Json.JsonValueKind.Number => "number",
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean", _ => "null" };
            if (type == "null") alternatives.Add((JsonNode)Object(("kind", Enum("null"))));
            else alternatives.Add((JsonNode)Object(("kind", Enum(type)), (type == "string" ? "text" : type,
                new JsonObject { ["type"] = type, ["enum"] = new JsonArray(group.OrderBy(v => v!.ToJsonString(), StringComparer.Ordinal)
                    .DistinctBy(v => v!.ToJsonString()).Select(v => v!.DeepClone()).ToArray()) })));
        }
        var domain = new JsonObject { ["anyOf"] = alternatives };
        var existing = definitions.FirstOrDefault(d => d.Key.StartsWith("domain", StringComparison.Ordinal) && JsonNode.DeepEquals(d.Value, domain));
        if (existing.Key is not null) return Ref(existing.Key);
        var name = "domain" + definitions.Count; definitions[name] = domain; return Ref(name);
    }

    private static JsonObject Described(JsonObject schema, string description) { schema["description"] = description; return schema; }
    private static JsonObject Identity() => new() { ["type"] = "string", ["pattern"] = TaskPlanCompiler.IdentityPattern };
    private static JsonObject Nonblank() => new() { ["type"] = "string", ["pattern"] = @"\S" };
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
