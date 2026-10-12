using System.Text;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    private static void PreparePureBranches(PlanningWorkflow workflow, LoweringScope scope)
    {
        IEnumerable<PlanningValue> Values(PlanningNode n) => BindingValues(n.Input).Concat(BindingValues(n.If)).Concat(BindingValues(n.Expr))
            .Concat(n.Cases.SelectMany(c => BindingValues(c.When))).Concat(n.OnError.SelectMany(c => BindingValues(c.If).Concat(BindingValues(c.SetOutput))));
        var uses = scope.Nodes.Values.SelectMany(Values).Concat(workflow.Outputs.SelectMany(o => BindingValues(o.Value))).ToArray();
        if (uses.Any(v => v.Kind is "expression" or "template")) return;
        foreach (var node in scope.Nodes.Values)
        {
            if (node is not { Type: "switch", Expr: not null, If: null, Retry: null, Output: null, CapabilityId: null, Cases.Count: 1 } ||
                node.OnError.Count != 0 || node.Cases[0].Value != "true" || node.Cases[0].When is not null ||
                node.Default.Count == 0 || node.Cases[0].Steps.Count == 0) continue;
            var children = node.Cases[0].Steps.Concat(node.Default).ToArray();
            bool Pure(PlanningNode n) => n.If is null && n.Expr is null && n.Retry is null && n.OnError.Count == 0 && n.Output is null && n.CapabilityId is null &&
                (n.Type == "set" && n.Input.Kind != "dynamic_mapping" && n.Steps.Count + n.Cases.Count + n.Default.Count + n.Branches.Count == 0 ||
                 n.Type == "workflow.call" && PureCall(n, scope));
            if (!children.All(Pure)) continue;
            var references = uses.Where(v => v.Source == node.Key).ToArray();
            var projections = uses.Where(v => v.Kind == "projection" && PlanningGraphValidation.Member(v, "value")?.Source == node.Key).ToArray();
            // Only explicit selections of these branch results observe the
            // envelope. Whole-result, presence and dynamic observers retain it.
            if (references.Length == 0 || references.Any(v => v.Kind != "output" || v.Path.Count != 0 ||
                !projections.Any(p => ReferenceEquals(PlanningGraphValidation.Member(p, "value"), v))) ||
                projections.Any(p => PlanningGraphValidation.Member(p, "each")?.Boolean == true ||
                    PlanningGraphValidation.Literal(PlanningGraphValidation.Member(p, "paths")!) is not JsonArray { Count: > 0 } paths ||
                    paths.Any(path => path is not JsonArray { Count: > 0 } a || !children.Any(c => c.Key == a[0]?.ToString())))) continue;
            if (uses.Any(v => v.Kind == "present" && children.Any(c => c.Key == v.Source)) ||
                scope.Nodes.Values.Where(n => children.All(c => c.Key != n.Key)).SelectMany(Values).Concat(workflow.Outputs.SelectMany(o => BindingValues(o.Value)))
                    .Any(v => children.Any(c => c.Key == v.Source)) ||
                scope.Nodes.Values.Any(n => n.Dependencies.Any(d => children.Any(c => c.Key == d))) ||
                scope.Nodes.Values.SelectMany(n => n.OnError).SelectMany(c => BindingValues(c.If).Concat(BindingValues(c.SetOutput))).Any(v => v.Source == node.Key)) continue;
            scope.PureBranches.Add(node.Key);
        }
    }

    private static JsonObject LowerPureBranch(PlanningNode node, LoweringScope scope)
    {
        var contracts = new JsonObject();
        var serial = 0;
        var branchNumber = (scope.PureBranches.ToList().IndexOf(node.Key) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var field = BindingValues(node.Expr).SelectMany(v => v.Path).LastOrDefault(p => p is not ("value" or "outputs" or "json"));
        var branchName = field is null ? "predicate" + branchNumber : FunctionalName(field) + "Branch" + branchNumber;
        string Branch(List<PlanningNode> steps)
        {
            var local = scope with { LexicalOutputs = new(scope.LexicalOutputs, StringComparer.Ordinal) };
            var program = new StringBuilder("(() => {"); var fields = new List<string>();
            foreach (var child in steps)
            {
                var lowered = child.Type == "workflow.call" ? LowerPureCall(child, local,
                    branchName + "Values" + (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture)) : ReadableStep(LowerNode(child, local), local);
                if (lowered["expression_contracts"]?[""] is JsonObject inner)
                    foreach (var (key, entry) in inner) contracts.Add(key, entry?.DeepClone());
                var name = branchName + "Result" + (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var expression = lowered["input"] is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                    ? ExpressionSegments.Read(text).Single().Expression
                    : ToExpression(child.Input, local)[2..^1];
                program.Append("const ").Append(name).Append(" = ").Append(expression).Append(';');
                contracts[name] = new JsonObject { ["origin"] = child.Key, ["source"] = name, ["order"] = fields.Count,
                    ["schema"] = lowered["output_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }, ["object_only"] = true };
                local.LexicalOutputs[child.Key] = name;
                fields.Add(scope.NodeIds[child.Key] + ": " + name);
            }
            return program.Append("return { ").Append(string.Join(", ", fields)).Append(" }; })()").ToString();
        }
        return new JsonObject { ["id"] = scope.NodeIds[node.Key], ["type"] = "set",
            ["input"] = "${((condition) => condition === true || condition === \"true\")( " + ToExpression(node.Expr!, scope)[2..^1] + ") ? " + Branch(node.Cases[0].Steps) + " : " + Branch(node.Default) + "}",
            ["__lexical_contracts"] = contracts, ["output_schema"] = new JsonObject { ["type"] = "object" } };
    }

    private static bool PureCall(PlanningNode call, LoweringScope scope)
    {
        var key = PlanningGraphValidation.Member(call.Input, "ref")?.Source;
        if (key is null || !scope.Workflows.TryGetValue(key, out var body) || body.Finally.Count != 0 || body.Outputs.Count != 1 ||
            !string.IsNullOrEmpty(body.Functions) || body.Inputs.Any(i => i.Default is not null || !i.Required)) return false;
        var args = PlanningGraphValidation.Member(call.Input, "args");
        if (args?.Kind != "object" || !args.Members.Select(m => m.Name).Order().SequenceEqual(body.Inputs.Select(i => i.Name).Order())) return false;
        return body.Steps.All(n => n.CapabilityId is null && n.If is null && n.Expr is null && n.Retry is null && n.OnError.Count == 0 && n.Output is null &&
            (n.Type == "set" && n.Input.Kind != "dynamic_mapping" && n.Steps.Count + n.Cases.Count + n.Default.Count + n.Branches.Count == 0 ||
             n.InternalRole is "typed_projection" or "typed_index_projection" || n.InternalRole?.StartsWith("inline:", StringComparison.Ordinal) == true));
    }

    private static JsonObject LowerPureCall(PlanningNode call, LoweringScope parent, string prefix)
    {
        var body = parent.Workflows[PlanningGraphValidation.Member(call.Input, "ref")!.Source!];
        var nodes = Enumerate(body.Steps).ToArray();
        var scope = new LoweringScope(parent.Catalog, nodes.ToDictionary(n => n.Key, n => "n_" + Fingerprint(n.Key)[..16], StringComparer.Ordinal),
            parent.WorkflowIds, body.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal), nodes.ToDictionary(n => n.Key, n => n.Type, StringComparer.Ordinal),
            LoopVariables(nodes), nodes.ToDictionary(n => n.Key, StringComparer.Ordinal), parent.Workflows, parent.Descriptions)
        { DirectProjections = true, NativeMappings = true, ReadableMappings = true, LeanMappings = true, CompactProjections = true, InlineConsumers = true, DirectValues = true };
        PrepareCopyProjections(body, scope); PrepareFusion(body, scope, true);
        var lowered = LowerSteps(body.Steps, scope);
        if (lowered.Any(n => n?["type"]?.ToString() != "set")) throw new InvalidOperationException("Pure branch contains an execution boundary.");
        var program = new StringBuilder("(() => {"); var contracts = new JsonObject(); var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = prefix + "Inputs";
        var inputProperties = new JsonObject();
        foreach (var port in body.Inputs) inputProperties.Add(port.Name, ToJsonSchema(port.Schema, parent.Catalog));
        var inputSchema = new JsonObject { ["type"] = "object", ["properties"] = inputProperties,
            ["required"] = new JsonArray(body.Inputs.Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()) };
        var order = 0;
        void Declare(string name, string expression, JsonNode? schema, string origin)
        {
            program.Append("const ").Append(name).Append(" = ").Append(expression).Append(';');
            contracts.Add(name, new JsonObject { ["source"] = name, ["order"] = order++, ["origin"] = origin, ["schema"] = schema?.DeepClone() ?? new JsonObject { ["type"] = "object" } });
        }
        var argsExpression = ReadableStep(new JsonObject { ["input"] = ToExpression(PlanningGraphValidation.Member(call.Input, "args")!, parent) }, parent);
        Declare(arguments, RebaseLexicalProgram(ExpressionSegments.Read(argsExpression["input"]!.GetValue<string>()).Single().Expression, argsExpression["expression_contracts"]?[""] as JsonObject, arguments, addresses, contracts), inputSchema, call.Key + "/inputs");
        addresses["data.inputs"] = arguments;
        var serial = 0;
        foreach (var step in lowered.Cast<JsonObject>())
        {
            var name = prefix + "Value" + (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var original = nodes.Single(n => scope.NodeIds[n.Key] == step["id"]!.GetValue<string>());
            var expression = step["input"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? ExpressionSegments.Read(text).Single().Expression : ToExpression(original.Input, scope)[2..^1];
            var embedded = RebaseLexicalProgram(expression, step["expression_contracts"]?[""] as JsonObject, name, addresses, contracts);
            Declare(name, embedded, step["output_schema"], original.Key);
            addresses["data.steps." + step["id"]!.GetValue<string>()] = name;
        }
        var output = body.Outputs[0];
        var returned = ReadableStep(new JsonObject { ["input"] = ToExpression(output.Value, scope) }, scope);
        var resultName = prefix + "Result";
        Declare(resultName, RebaseLexicalProgram(ExpressionSegments.Read(returned["input"]!.GetValue<string>()).Single().Expression,
            returned["expression_contracts"]?[""] as JsonObject, resultName, addresses, contracts), ToJsonSchema(output.Schema, scope.Catalog), body.Key + "/outputs/" + output.Name);
        program.Append("return { outputs: { ").Append(JsonValue.Create(output.Name)!.ToJsonString()).Append(": ").Append(resultName).Append(" } }; })()");
        return new JsonObject { ["input"] = "${" + program + "}", ["expression_contracts"] = new JsonObject { [""] = contracts },
            ["output_schema"] = new JsonObject { ["type"] = "object" } };
    }

    private static string RebaseLexicalProgram(string program, JsonObject? checks, string prefix,
        Dictionary<string, string> addresses, JsonObject destination)
    {
        var names = checks?.ToDictionary(p => p.Key, p => prefix + char.ToUpperInvariant(p.Key[0]) + p.Key[1..], StringComparer.Ordinal) ?? new();
        if (checks is not null)
            foreach (var (name, entry) in checks)
            {
                var copy = entry!.DeepClone().AsObject(); copy.Remove("scope");
                copy["source"] = names[copy["source"]?.GetValue<string>() ?? name];
                destination.Add(names[name], copy);
            }
        string Write(Node node)
        {
            if (addresses.TryGetValue(program[node.Start..node.End], out var address)) return address;
            if (node is Identifier id && names.TryGetValue(id.Name, out var renamed)) return renamed;
            if (node is MemberExpression { Computed: false } member) return Write(member.Object) + "." + program[member.Property.Start..member.Property.End];
            if (node is Property { Computed: false } property)
                return program[property.Key.Start..property.Key.End] + ": " + Write(property.Value);
            var text = new StringBuilder(); var start = node.Start;
            foreach (var child in node.ChildNodes)
            {
                if (child.Start < start) continue;
                text.Append(program[start..child.Start]).Append(Write(child)); start = child.End;
            }
            return text.Append(program[start..node.End]).ToString();
        }
        return Write(new Parser().ParseExpression(program));
    }

    private static void QualifyLexicalContracts(string program, JsonObject contracts)
    {
        var syntax = new Parser().ParseExpression(program); var index = 0;
        var declarationScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(Node node)
        {
            if (node is CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Body: BlockStatement body } })
            {
                var scope = "block" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var declaration in body.Body.OfType<VariableDeclaration>())
                    if (declaration.Declarations[0].Id is Identifier name && contracts.ContainsKey(name.Name)) declarationScopes.Add(name.Name, scope);
            }
            foreach (var child in node.ChildNodes) Visit(child);
        }
        Visit(syntax);
        foreach (var (_, entry) in contracts)
        {
            var source = entry!["source"]!.GetValue<string>();
            if (!declarationScopes.TryGetValue(source, out var scope)) throw new InvalidOperationException("Checked local has no unique lexical scope: " + source);
            entry["scope"] = scope;
        }
    }
}
