using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    // Only compiler-marked copy loops qualify. Business loops retain their scopes,
    // effects, ordering and runtime receipts.
    private static JsonObject LowerCopyLoop(PlanningNode loop, LoweringScope scope)
    {
        var call = loop.Steps.Single();
        var body = scope.Workflows[PlanningGraphValidation.Member(call.Input, "ref")!.Source!];
        var args = PlanningGraphValidation.Member(call.Input, "args")!;
        var captures = new List<PlanningMember>();
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args.Members)
        {
            if (arg.Value.Kind == "loop_item" && arg.Value.Source == loop.Key)
                inputs[arg.Name] = "item" + string.Concat(arg.Value.Path.Select(Segment));
            else
            {
                captures.Add(arg);
                inputs[arg.Name] = "source" + Segment(arg.Name);
            }
        }
        const string collection = "__collection";
        captures.Add(new(collection, PlanningGraphValidation.Member(loop.Input, "items")!));
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        string Resolve(PlanningValue value) => value.Kind switch
        {
            "input" => inputs[value.Source!] + string.Concat(value.Path.Select(Segment)),
            "output" => "(" + expressions[value.Source!] + ")" + string.Concat(value.Path.Select(Segment)),
            "object" => "({" + string.Join(",", value.Members.Select(m => Quote(m.Name) + ":" + Resolve(m.Value))) + "})",
            "array" => "[" + string.Join(",", value.Items.Select(Resolve)) + "]",
            "flatten" => FlattenExpression(Resolve(value.Items.Single())),
            "null" or "string" or "number" or "boolean" => LowerValue(value, scope, allowReferences: false)?.ToJsonString() ?? "null",
            "projection" => "({value:m.select(" + Resolve(PlanningGraphValidation.Member(value, "value")!) + "," +
                PlanningGraphValidation.Literal(PlanningGraphValidation.Member(value, "paths")!)!.ToJsonString() + ",false)})",
            _ => throw new InvalidOperationException("A compiled copy loop contains a non-structural value.")
        };
        foreach (var node in body.Steps.Concat(body.Finally))
        {
            if (node.Type != "set" || node.Input.Kind == "dynamic_mapping")
                throw new InvalidOperationException("A compiled copy loop contains executable work.");
            expressions.Add(node.Key, Resolve(node.Input));
        }
        var outputs = "({" + string.Join(",", body.Outputs.Select(o => Quote(o.Name) + ":" + Resolve(o.Value))) + "})";
        var callId = scope.NodeIds[call.Key];
        var script = "({results:source." + collection + ".map(item=>({" + Quote(callId) + ":{outputs:" + outputs + "}})),count:source." + collection + ".length})";
        JintSandbox.ValidateMapping(script, learned: false);
        var source = ToExpression(new() { Kind = "object", Members = captures }, scope)[2..^1];
        var properties = new JsonObject(body.Outputs.Select(o => new KeyValuePair<string, JsonNode?>(o.Name, ToJsonSchema(o.Schema, scope.Catalog))));
        var outputSchema = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(body.Outputs.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()), ["additionalProperties"] = false };
        static JsonObject ObjectSchema(string name, JsonObject value) => new() { ["type"] = "object", ["properties"] = new JsonObject { [name] = value }, ["required"] = new JsonArray(name) };
        return new() { ["id"] = scope.NodeIds[loop.Key], ["type"] = "set",
            ["input"] = "${checkedMapping(" + Quote(script) + "," + source + ")}",
            ["output_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["results"] = new JsonObject { ["type"] = "array", ["items"] = ObjectSchema(callId, ObjectSchema("outputs", outputSchema)) },
                ["count"] = new JsonObject { ["type"] = "integer" } }, ["required"] = new JsonArray("results", "count") } };
    }

    private static string LowerAssembly(PlanningNode node, LoweringScope scope)
    {
        var sources = new List<PlanningMember>();
        string Resolve(PlanningValue value)
        {
            if (value.Kind == "output" && scope.Nodes[value.Source!].InternalRole == "inline:" + node.Key)
            {
                var selection = scope.Nodes[value.Source!].Input;
                var source = Resolve(PlanningGraphValidation.Member(selection, "value")!);
                var paths = PlanningGraphValidation.Literal(PlanningGraphValidation.Member(selection, "paths")!)!;
                if (!value.Path.SequenceEqual(new[] { "value" })) throw new InvalidOperationException("An inline selection must consume its checked value.");
                return "m.select(" + source + "," + paths.ToJsonString() + ",false)";
            }
            if (value.Kind == "object") return "({" + string.Join(",", value.Members.Select(m => Quote(m.Name) + ":" + Resolve(m.Value))) + "})";
            if (value.Kind == "array") return "[" + string.Join(",", value.Items.Select(Resolve)) + "]";
            if (value.Kind == "flatten") return FlattenExpression(Resolve(value.Items.Single()));
            var name = "v" + sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sources.Add(new(name, value)); return "source." + name;
        }
        var script = Resolve(node.Input); JintSandbox.ValidateMapping(script, learned: false);
        var sourceExpression = ToExpression(new() { Kind = "object", Members = sources }, scope)[2..^1];
        return "${checkedMapping(" + Quote(script) + "," + sourceExpression + ")}";
    }

    private static string FlattenExpression(string source)
        => "(" + source + ").flatMap(item=>m.select(item,[[]],true))";

    private static string Quote(string value) => JsonValue.Create(value)!.ToJsonString();
}
