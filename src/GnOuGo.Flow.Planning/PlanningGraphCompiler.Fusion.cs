using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    private static string LocalName(string key, LoweringScope scope) => "v" + Array.IndexOf(scope.FusedOutputs[key], key).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string OutputAddress(string key, LoweringScope scope) => scope.LocalOutputs.Contains(key)
        ? LocalName(key, scope) : scope.FusedOutputs.TryGetValue(key, out var group)
        ? "data.steps." + scope.NodeIds[group[0]] + "." + LocalName(key, scope) : "data.steps." + scope.NodeIds[key];

    private static IEnumerable<PlanningValue> BindingValues(PlanningValue? value) => value is null ? [] :
        new[] { value }.Concat(value.Items.SelectMany(BindingValues)).Concat(value.Members.SelectMany(m => BindingValues(m.Value)));

    private static void PrepareFusion(PlanningWorkflow workflow, LoweringScope scope)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<PlanningValue> Values(PlanningNode n) => BindingValues(n.Input).Concat(BindingValues(n.If)).Concat(BindingValues(n.Expr))
            .Concat(n.Cases.SelectMany(c => BindingValues(c.When)))
            .Concat(n.OnError.SelectMany(e => BindingValues(e.If).Concat(BindingValues(e.SetOutput))));
        var values = scope.Nodes.Values.SelectMany(Values).Concat(workflow.Outputs.SelectMany(o => BindingValues(o.Value))).ToArray();
        foreach (var value in values)
        {
            if (value.Kind == "present" && value.Source is not null) blocked.Add(value.Source);
            // Container results and raw expressions observe physical child identities.
            if (value.Kind is "expression" or "template") blocked.UnionWith(scope.Nodes.Keys);
            if (value.Kind == "output" && value.Source is not null && scope.Nodes.TryGetValue(value.Source, out var owner) &&
                owner.Type is "sequence" or "switch" or "parallel" or "loop.sequential" or "loop.parallel")
                blocked.UnionWith(Enumerate([owner]).Skip(1).Select(n => n.Key));
        }
        // A failure handler may need a successfully completed earlier value even
        // when a later checked value fails. Such receipts must remain independent.
        foreach (var value in Enumerate(workflow.Finally).SelectMany(Values).Concat(scope.Nodes.Values
            .SelectMany(n => n.OnError).SelectMany(e => BindingValues(e.If).Concat(BindingValues(e.SetOutput)))))
            if (value.Source is not null) blocked.Add(value.Source);
        bool Eligible(PlanningNode n) => !blocked.Contains(n.Key) &&
            (n.Type == "set" && n.Input.Kind != "dynamic_mapping" || n.InternalRole is "typed_projection" or "typed_index_projection") &&
            n.CapabilityId is null && n.If is null && n.Expr is null && n.Output is null && n.Retry is null && n.OnError.Count == 0;
        void Scan(List<PlanningNode> nodes)
        {
            var run = new List<string>();
            void Flush()
            {
                if (run.Count > 1) { var group = run.ToArray(); foreach (var key in group) scope.FusedOutputs.Add(key, group); }
                run.Clear();
            }
            foreach (var node in nodes)
            {
                if (node.InternalRole?.StartsWith("inline:", StringComparison.Ordinal) == true) continue;
                if (Eligible(node)) run.Add(node.Key); else Flush();
                // Each branch and nested execution scope has its own sequence.
                Scan(node.Steps); Scan(node.Default);
                foreach (var branch in node.Branches) Scan(branch.Steps);
                foreach (var branch in node.Cases) Scan(branch.Steps);
            }
            Flush();
        }
        Scan(workflow.Steps); Scan(workflow.Finally);
        foreach (var node in scope.Nodes.Values)
            foreach (var value in Values(node).Where(v => v.Kind == "output" && v.Source is not null))
                if (scope.FusedOutputs.TryGetValue(value.Source!, out var group) && !group.Contains(node.Key, StringComparer.Ordinal))
                    scope.FusedExports.Add(value.Source!);
        foreach (var value in workflow.Outputs.SelectMany(o => BindingValues(o.Value)))
            if (value.Kind == "output" && value.Source is not null) scope.FusedExports.Add(value.Source);
    }

    private static JsonObject LowerFused(string[] group, LoweringScope scope)
    {
        var local = scope with { LocalOutputs = new(StringComparer.Ordinal) };
        var program = new StringBuilder("(()=>{"); var contracts = new JsonObject(); var fields = new JsonObject();
        foreach (var key in group)
        {
            var node = scope.Nodes[key]; var lowered = LowerNode(node, local); var id = LocalName(key, scope);
            var whole = node.Input.Kind == "projection" && PlanningGraphValidation.Literal(PlanningGraphValidation.Member(node.Input, "paths")!) is JsonArray { Count: 1 } paths &&
                paths[0] is JsonArray { Count: 0 } && PlanningGraphValidation.Member(node.Input, "each")?.Boolean != true;
            var expression = whole ? ToExpression(new() { Kind = "object", Members = [new("value", PlanningGraphValidation.Member(node.Input, "value")!)] }, local) :
                node.InternalRole is "typed_assembly" or "typed_projection" or "typed_index_projection"
                ? lowered["input"]!.GetValue<string>() : ToExpression(node.Input, local);
            // A closed, already checked record need not be assembled again under
            // identical property names. Its own check still runs at this position.
            if (node.Input is { Kind: "object", Members.Count: > 0 } assembly && assembly.Members[0].Value.Source is { } source &&
                scope.Nodes.TryGetValue(source, out var producer) && producer.OutputSchema is { } output &&
                assembly.Members.All(m => m.Value.Kind == "output" && m.Value.Source == source && m.Value.Path.SequenceEqual([m.Name]) && m.Value.ResultChannel is null))
            {
                var shape = ToJsonSchema(output, scope.Catalog);
                if (shape["additionalProperties"]?.ToJsonString() == "false" && shape["properties"] is JsonObject properties &&
                    properties.Select(p => p.Key).SequenceEqual(assembly.Members.Select(m => m.Name)) &&
                    shape["required"] is JsonArray required && required.Count == properties.Count)
                    expression = ToExpression(new() { Kind = "output", Source = source }, local);
            }
            program.Append("const ").Append(id).Append('=').Append(expression[2..^1]).Append(';');
            contracts[id] = new JsonObject { ["origin"] = key, ["schema"] = lowered["output_schema"]?.DeepClone() };
            if (scope.FusedExports.Contains(key)) fields[id] = lowered["output_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
            local.LocalOutputs.Add(key);
        }
        program.Append("return ({").Append(string.Join(',', group.Where(scope.FusedExports.Contains).Select(k => LocalName(k, scope) + ":" + LocalName(k, scope)))).Append("});})()");
        ExpressionEvaluator.Validate(program.ToString());
        var result = new JsonObject { ["id"] = scope.NodeIds[group[0]], ["type"] = "set",
            ["input"] = "${checkedMapping(\n" + JsonValue.Create(program.ToString())!.ToJsonString(new System.Text.Json.JsonSerializerOptions(PlanningJsonContext.Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + ",data," + contracts.ToJsonString() + "\n)}",
            ["output_schema"] = new JsonObject { ["type"] = "object", ["properties"] = fields,
                ["required"] = new JsonArray(group.Where(scope.FusedExports.Contains).Select(k => (JsonNode?)JsonValue.Create(LocalName(k, scope))).ToArray()), ["additionalProperties"] = false } };
        if (scope.Descriptions) result["description"] = "Check and assemble consecutive deterministic values.";
        return result;
    }
}
