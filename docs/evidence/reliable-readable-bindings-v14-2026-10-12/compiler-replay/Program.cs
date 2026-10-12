using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

if (args.Length == 2 && args[0] == "--measure")
{
    Console.WriteLine(Metrics(File.ReadAllText(args[1])).ToJsonString(new() { WriteIndented = true }));
    return;
}
if (args.Length != 2) throw new ArgumentException("workspace evidence-output-directory");
var store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var record = await store.GetAsync("planning-schema-portability", "benchmark", "schema-portability-20261002:run:compactv1320261012a-amazon-1", "GnOuGo.Planning.Benchmark")
    ?? throw new InvalidOperationException("Retained run missing.");
var saved = JsonNode.Parse(record.Value)!;
var session = saved["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
if (session.Request.SessionId != "compactv1320261012a-amazon-1" || session.Revision != 9 ||
    session.ComputeArtifactHash() != "243a67abc3c44ccdb97ef6fda8e1a8f794ab88501f3685fa47a38103af6a3d16") throw new InvalidOperationException("Retained identity changed.");
PlanningArtifactApproval.Verify(session);
var before = Compile(false);
if (before != session.Yaml) throw new InvalidOperationException("Historical v13 lowering changed.");
var after = Compile(true);
Directory.CreateDirectory(args[1]);
File.WriteAllText(Path.Combine(args[1], "live-r8-v13.yaml"), before);
File.WriteAllText(Path.Combine(args[1], "live-r8-v14.yaml"), after);
var a = Metrics(before); var b = Metrics(after);
if (b["javascript_bytes"]!.GetValue<int>() >= a["javascript_bytes"]!.GetValue<int>()) throw new InvalidOperationException("JavaScript did not improve.");
if (!Origins(before).SequenceEqual(Origins(after))) throw new InvalidOperationException("A validation origin changed.");
Console.WriteLine(new JsonObject { ["provider_calls"] = 0, ["mcp_calls"] = 0, ["historical_writes"] = 0,
    ["historical_yaml_identical"] = true, ["validation_origins_preserved"] = true, ["before"] = a, ["after"] = b }.ToJsonString(new() { WriteIndented = true }));

string Compile(bool v14)
{
    var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(session.Request, PlanningJsonContext.Default.PlanningRequest), PlanningJsonContext.Default.PlanningRequest)!;
    request.Options["compilation_profile"] = v14 ? "compact-bindings-v14" : "compact-bindings-v13";
    var graph = new TaskPlanCompiler().Compile(session.Plan!, session.Catalog!, request);
    if (graph.Diagnostics.Count != 0) throw new InvalidOperationException(string.Join(';', graph.Diagnostics));
    return new PlanningGraphCompiler().Compile(graph.Graph!, session.Catalog!, request.Name, true, true, true, true, true, true, true, true, true, true, true, v14);
}

static IEnumerable<JsonNode> Nodes(JsonNode? node) => node is null ? [] : new[] { node }.Concat(node switch
{ JsonObject obj => obj.SelectMany(p => Nodes(p.Value)), JsonArray array => array.SelectMany(Nodes), _ => [] });
static JsonNode Definition(string yaml) => JsonSerializer.SerializeToNode(WorkflowParser.Parse(yaml), WorkflowRunJsonContext.Default.WorkflowDocument)!;
static string[] Origins(string yaml) => Nodes(Definition(yaml)).OfType<JsonObject>().Where(o => o.ContainsKey("schema") && o.ContainsKey("origin"))
    .Select(o => o["origin"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
static JsonObject Metrics(string yaml)
{
    var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
    static IEnumerable<CompiledStep> Steps(IEnumerable<CompiledStep> steps) => steps.SelectMany(s => new[] { s }.Concat(Steps(s.Steps ?? []))
        .Concat(Steps(s.Default ?? [])).Concat((s.Cases ?? []).SelectMany(c => Steps(c.Steps))).Concat((s.Branches ?? []).SelectMany(Steps)));
    var allSteps = document.Workflows.Values.SelectMany(w => Steps(w.Steps.Concat(w.Finally))).ToArray();
    var programs = Nodes(Definition(yaml)).OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>())
        .Where(s => s.StartsWith("${", StringComparison.Ordinal)).SelectMany(s => ExpressionSegments.Read(s).Select(e => e.Expression)).Distinct(StringComparer.Ordinal).ToArray();
    static IEnumerable<Node> Ast(Node n) => new[] { n }.Concat(n.ChildNodes.SelectMany(Ast));
    var nodes = programs.SelectMany(p => Ast(new Parser().ParseExpression(p))).ToArray();
    static int FunctionDepth(Node n, int depth = 0)
    {
        if (n is ArrowFunctionExpression) depth++;
        return n.ChildNodes.Select(child => FunctionDepth(child, depth)).Prepend(depth).Max();
    }
    return new JsonObject { ["workflows"] = document.Workflows.Count, ["steps"] = allSteps.Length, ["sets"] = allSteps.Count(s => s.Type == "set"),
        ["yaml_bytes"] = Encoding.UTF8.GetByteCount(yaml), ["javascript_bytes"] = programs.Sum(Encoding.UTF8.GetByteCount),
        ["javascript_max_line"] = programs.SelectMany(s => s.Split('\n')).Max(s => s.Length), ["unique_programs"] = programs.Length,
        ["declarations"] = nodes.OfType<VariableDeclarator>().Count(), ["functions"] = nodes.OfType<ArrowFunctionExpression>().Count(),
        ["maximum_function_depth"] = programs.Select(p => FunctionDepth(new Parser().ParseExpression(p))).DefaultIfEmpty().Max(),
        ["technical_declarations"] = nodes.OfType<VariableDeclarator>().Count(d => d.Id is Identifier id &&
            (id.Name.StartsWith("checkedRecord", StringComparison.Ordinal) || id.Name.StartsWith("predicate", StringComparison.Ordinal) && id.Name.Contains("Result", StringComparison.Ordinal))),
        ["wrappers"] = nodes.OfType<ObjectExpression>().Count(o => o.Properties.Count == 1 && o.Properties[0] is Property p &&
            (p.Key is Identifier { Name: "value" or "outputs" } || p.Key is Identifier id && id.Name.StartsWith("n_", StringComparison.Ordinal))),
        ["select_helpers"] = nodes.OfType<CallExpression>().Count(c => c.Callee is MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "select" } }) };
}
