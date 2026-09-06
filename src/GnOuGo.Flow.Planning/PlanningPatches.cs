using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Atomic, field-scoped patches; model responses cannot replace a workflow graph.</summary>
public static class PlanningPatches
{
    private static readonly string[] NodeFields = ["input", "outputSchema", "structuredOutput", "expr", "if", "onError"];

    public static JsonObject Schema(PlanningPreparation preparation)
    {
        var definitions = PlanningSchemas.Graph(preparation)["$defs"]!.DeepClone().AsObject();
        JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
        JsonObject Nullable(JsonObject type) => new() { ["anyOf"] = new JsonArray(type, new JsonObject { ["type"] = "null" }) };
        JsonObject Variant(string field, JsonObject value, bool workflow = false, bool root = false)
        {
            var properties = new JsonObject
            {
                ["workflow"] = new JsonObject { ["type"] = root ? "null" : "string" }, ["node"] = new JsonObject { ["type"] = workflow ? "null" : "string" },
                ["field"] = new JsonObject { ["type"] = "string", [field == "cases/when" ? "pattern" : "enum"] = field == "cases/when" ? JsonValue.Create("^cases/[0-9]+/when$") : new JsonArray(field) }, ["value"] = value
            };
            return new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("workflow", "node", "field", "value"), ["additionalProperties"] = false };
        }
        var structured = definitions["node"]!["properties"]!["structuredOutput"]!.DeepClone().AsObject();
        var variants = new JsonArray(
            Variant("input", Ref("value")), Variant("outputSchema", Nullable(Ref("schema"))), Variant("structuredOutput", structured),
            Variant("cases/when", Nullable(Ref("value"))), Variant("expr", Nullable(Ref("value"))), Variant("if", Nullable(Ref("value"))),
            Variant("onError", new() { ["type"] = "array", ["items"] = Ref("errorCase") }),
            Variant("functions", Nullable(new() { ["type"] = "string" }), true),
            Variant("functions", Nullable(new() { ["type"] = "string" }), true, true),
            Variant("inputs", new() { ["type"] = "array", ["items"] = Ref("port") }, true),
            Variant("outputs", new() { ["type"] = "array", ["items"] = Ref("output") }, true));
        return new() { ["type"] = "object", ["properties"] = new JsonObject { ["patches"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = variants } } }, ["required"] = new JsonArray("patches"), ["additionalProperties"] = false, ["$defs"] = definitions };
    }

    public static PlanningGraph Apply(PlanningGraph original, JsonObject response, IReadOnlySet<string> allowed, PlanningPreparation preparation)
    {
        var errors = PlanningContractValidation.ValidateInstance(response, Schema(preparation));
        if (errors.Count != 0) throw new InvalidOperationException("Invalid patch response: " + string.Join("; ", errors));
        var graph = JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.PlanningGraph)!.AsObject();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in response["patches"]!.AsArray().OfType<JsonObject>())
        {
            var workflowKey = patch["workflow"]?.GetValue<string>(); var nodeKey = patch["node"]?.GetValue<string>(); var field = patch["field"]!.GetValue<string>();
            var coordinate = Coordinate(workflowKey, nodeKey, field);
            if (!changed.Add(coordinate)) throw new InvalidOperationException("A patch field was submitted more than once.");
            var workflow = workflowKey is null ? graph : graph["workflows"]!.AsArray().OfType<JsonObject>().SingleOrDefault(w => w["key"]!.GetValue<string>() == workflowKey) ?? throw new InvalidOperationException("Unknown patch workflow.");
            JsonObject target = workflow;
            if (nodeKey is not null)
                target = Nodes(workflow["steps"]!.AsArray().Concat(workflow["finally"]!.AsArray())).SingleOrDefault(n => n["key"]!.GetValue<string>() == nodeKey) ?? throw new InvalidOperationException("Unknown patch node.");
            if (!allowed.Contains(coordinate))
            {
                var node = nodeKey is null ? null : JsonSerializer.Deserialize(target, PlanningJsonContext.Default.PlanningNode);
                var schema = field == "outputSchema" && patch["value"] is { } value ? JsonSerializer.Deserialize(value, PlanningJsonContext.Default.PlanningSchema) : null;
                // Non-set annotations never emit an assertion or establish a producer contract.
                // Removing one preserves both execution and authoritative provenance checks.
                var annotationRemoval = field == "outputSchema" && patch["value"] is null && node is { Type: not "set" };
                // Adding a proven runtime assertion to a literal set cannot change its value.
                var provenAssertion = node?.Type == "set" && node.OutputSchema is null && schema is not null && PlanningGraphValidation.IsLiteral(node.Input) && PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(node.Input), PlanningGraphCompiler.ToJsonSchema(schema, preparation)).Count == 0;
                if (!annotationRemoval && !provenAssertion)
                    throw new InvalidOperationException("The patch targets a field outside the diagnosed dependency scope: " + coordinate);
            }
            if (field.StartsWith("cases/", StringComparison.Ordinal))
            {
                var parts = field.Split('/');
                if (!int.TryParse(parts[1], out var index) || target["cases"] is not JsonArray cases || index >= cases.Count)
                    throw new InvalidOperationException("Unknown patch decision outcome.");
                cases[index]!["when"] = patch["value"]?.DeepClone();
            }
            else target[field] = patch["value"]?.DeepClone();
        }
        if (changed.Count == 0) throw new InvalidOperationException("An empty repair cannot resolve diagnostics.");
        return JsonSerializer.Deserialize(graph, PlanningJsonContext.Default.PlanningGraph)!;
    }

    public static HashSet<string> Scope(PlanningGraph graph, IReadOnlyList<PlanningDiagnostic> diagnostics)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (diagnostics.Any(d => d.Location == "/functions" || d.Location.StartsWith("/functions/", StringComparison.Ordinal))) allowed.Add(Coordinate(null, null, "functions"));
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var path = "/workflows/" + wi;
            var all = PlanningGraphValidation.Located(workflow.Steps, path + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, path + "/finally")).ToArray();
            var global = diagnostics.Any(d => d.Location is "$" or "/workflows" || d.Location == workflow.Key || d.Location == "/functions");
            var affected = all.Where(n => global || diagnostics.Any(d => d.Location == n.Path || d.Location.StartsWith(n.Path + "/", StringComparison.Ordinal))).Select(n => n.Node.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (node, location) in all.Where(n => n.Node.Type is "loop.sequential" or "loop.parallel"))
                if (diagnostics.Any(d => d.Location.StartsWith(location + "/steps/", StringComparison.Ordinal))) allowed.Add(Coordinate(workflow.Key, node.Key, "input"));
            bool changed;
            do
            {
                changed = false;
                foreach (var (node, _) in all)
                    if (NodeReferences(node).Any(affected.Contains)) changed |= affected.Add(node.Key);
            } while (changed);
            foreach (var (node, location) in all.Where(n => affected.Contains(n.Node.Key)))
                foreach (var field in NodeFields)
                    if (field is not ("if" or "expr") || diagnostics.Any(d => d.Location.StartsWith(location + "/" + field, StringComparison.Ordinal))) allowed.Add(Coordinate(workflow.Key, node.Key, field));
            foreach (var (node, location) in all)
                for (var i = 0; i < node.Cases.Count; i++)
                    if (diagnostics.Any(d => d.Location.StartsWith(location + "/cases/" + i + "/when", StringComparison.Ordinal)))
                        allowed.Add(Coordinate(workflow.Key, node.Key, "cases/" + i + "/when"));
            foreach (var field in new[] { "inputs", "outputs", "functions" })
                if (global || diagnostics.Any(d => d.Location.StartsWith(path + "/" + field, StringComparison.Ordinal))) allowed.Add(Coordinate(workflow.Key, null, field));
            var values = all.Where(n => affected.Contains(n.Node.Key)).SelectMany(n => new[] { n.Node.Input, n.Node.Expr, n.Node.If }.OfType<PlanningValue>())
                .Concat(diagnostics.Any(d => d.Location.StartsWith(path + "/outputs", StringComparison.Ordinal)) ? workflow.Outputs.Select(o => o.Value) : []);
            var calls = values.SelectMany(FunctionCalls).ToHashSet(StringComparer.Ordinal);
            if (Declarations(workflow.Functions).Any(calls.Contains)) allowed.Add(Coordinate(workflow.Key, null, "functions"));
            if (Declarations(graph.Functions).Any(calls.Contains)) allowed.Add(Coordinate(null, null, "functions"));
        }
        return allowed;
    }

    private static IEnumerable<string> Declarations(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return [];
        try { return Ast(new Acornima.Parser().ParseScript(script)).OfType<Acornima.Ast.FunctionDeclaration>().Where(f => f.Id is not null).Select(f => f.Id!.Name).ToArray(); }
        catch (Acornima.ParseErrorException) { return []; }
    }
    private static IEnumerable<string> FunctionCalls(PlanningValue value)
    {
        if (value.Kind == "expression" && value.Text is { } text)
        {
            var names = new List<string>();
            try
            {
                var expression = text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}') ? text[2..^1] : text;
                foreach (var call in Ast(new Acornima.Parser().ParseExpression(expression)).OfType<Acornima.Ast.CallExpression>())
                    if (call.Callee is Acornima.Ast.Identifier id) names.Add(id.Name);
                    else if (call.Callee is Acornima.Ast.MemberExpression { Object: Acornima.Ast.Identifier { Name: "functions" }, Property: Acornima.Ast.Identifier member, Computed: false }) names.Add(member.Name);
            }
            catch (Acornima.ParseErrorException) { }
            foreach (var name in names) yield return name;
        }
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var name in FunctionCalls(child)) yield return name;
    }
    private static IEnumerable<Acornima.Ast.Node> Ast(Acornima.Ast.Node node)
    {
        yield return node;
        foreach (var child in node.ChildNodes) foreach (var nested in Ast(child)) yield return nested;
    }

    public static string Coordinate(string? workflow, string? node, string field) => JsonSerializer.Serialize(new[] { workflow, node, field }, PlanningJsonContext.Default.StringArray);

    private static IEnumerable<string> NodeReferences(PlanningNode node) =>
        new[] { node.Input, node.Expr, node.If }.Concat(node.Cases.Select(c => c.When))
            .Concat(node.OnError.SelectMany(e => new[] { e.If, e.SetOutput })).OfType<PlanningValue>().SelectMany(References);

    private static IEnumerable<string> References(PlanningValue value)
    {
        if (value.Kind == "output" && value.Source is not null) yield return value.Source;
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var key in References(child)) yield return key;
    }
    private static IEnumerable<JsonObject> Nodes(IEnumerable<JsonNode?> nodes)
    {
        foreach (var node in nodes.OfType<JsonObject>())
        {
            yield return node;
            foreach (var child in Nodes(node["steps"]!.AsArray().Concat(node["default"]!.AsArray()).Concat(node["cases"]!.AsArray().Concat(node["branches"]!.AsArray()).OfType<JsonObject>().SelectMany(b => b["steps"]!.AsArray())))) yield return child;
        }
    }
}
