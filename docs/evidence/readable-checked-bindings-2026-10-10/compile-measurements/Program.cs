using System.Text.Json;
using System.Text.Json.Nodes;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

var root = JsonNode.Parse(await File.ReadAllTextAsync(args[0]))!;
var session = JsonSerializer.Deserialize(root["session"], PlanningJsonContext.Default.PlanningSession)!;
Directory.CreateDirectory(args[1]);
var compiler = new PlanningGraphCompiler();
var measurements = new JsonArray();
foreach (var profile in new[] { 7, 8 })
{
    var yaml = compiler.Compile(session.Graph!, session.Catalog!, session.Request.Name, true, true, true, true, true, profile == 8);
    await File.WriteAllTextAsync(Path.Combine(args[1], "readable-v" + profile + ".yaml"), yaml);
    var document = WorkflowParser.Parse(yaml);
    var steps = document.Workflows.Values.SelectMany(w => Steps(w.Steps.Concat(w.Finally))).ToArray();
    measurements.Add(new JsonObject {
        ["profile"] = "compact-bindings-v" + profile,
        ["yaml_sha256"] = PlanningGraphCompiler.Fingerprint(yaml),
        ["yaml_bytes"] = System.Text.Encoding.UTF8.GetByteCount(yaml),
        ["max_line_characters"] = yaml.Split('\n').Max(s => s.Length),
        ["steps"] = steps.Length, ["sets"] = steps.Count(s => s.Type == "set"),
        ["workflows"] = document.Workflows.Count,
        ["checked_mapping_occurrences"] = yaml.Split("checkedMapping(").Length - 1,
        ["selection_helpers"] = yaml.Split("m.select(").Length - 1,
        ["max_checked_program_nesting"] = steps.SelectMany(s => Expressions(s.Input)).Select(p => Depth(new Acornima.Parser().ParseExpression(p))).DefaultIfEmpty().Max()
    });
}
Console.WriteLine(measurements.ToJsonString(new() { WriteIndented = true }));

IEnumerable<StepDef> Steps(IEnumerable<StepDef> steps) => steps.SelectMany(s => new[] { s }
    .Concat(Steps(s.Steps ?? [])).Concat(Steps(s.Default ?? []))
    .Concat(s.Branches?.SelectMany(b => Steps(b.Steps)) ?? [])
    .Concat(s.Cases?.SelectMany(c => Steps(c.Steps)) ?? []));
IEnumerable<string> Expressions(JsonNode? input)
{
    if (input is JsonObject obj) return obj.SelectMany(p => Expressions(p.Value));
    if (input is JsonArray array) return array.SelectMany(Expressions);
    return input is JsonValue value && value.TryGetValue<string>(out var text)
        ? ExpressionSegments.Read(text).Select(s => s.Expression) : [];
}
int Depth(Node node)
{
    var depth = node.ChildNodes.Select(Depth).DefaultIfEmpty().Max();
    if (node is not CallExpression { Callee: Identifier { Name: "checkedMapping" } } call) return depth;
    var program = call.Arguments[0] switch {
        StringLiteral literal => literal.Value,
        TemplateLiteral { Expressions.Count: 0, Quasis.Count: 1 } literal => literal.Quasis[0].Value.Cooked,
        _ => null
    };
    return 1 + Math.Max(depth, program is null ? 0 : Depth(new Acornima.Parser().ParseExpression(program)));
}
