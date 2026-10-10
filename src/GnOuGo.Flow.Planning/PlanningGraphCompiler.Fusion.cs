using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    private static string LocalName(string key, LoweringScope scope)
    {
        var group = scope.FusedOutputs[key];
        if (!scope.ReadableMappings) return "v" + Array.IndexOf(group, key).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var used = new HashSet<string>(StringComparer.Ordinal) { "data", "m", "source", "item", "index", "checkedMapping", "arguments", "eval",
            "const", "let", "var", "return", "if", "else", "switch", "case", "default", "class", "function", "new", "this", "null", "true", "false", "delete", "typeof", "void", "in", "of", "for", "while", "do", "break", "continue", "try", "catch", "finally", "throw", "yield", "await", "import", "export", "super", "extends", "with", "debugger", "instanceof",
            "enum", "implements", "interface", "package", "private", "protected", "public", "static", "undefined", "NaN", "Infinity", "Number", "Object", "Array", "JSON", "json" };
        foreach (var candidate in group)
        {
            var node = scope.Nodes[candidate];
            var field = node.OutputSchema?.Properties.FirstOrDefault(p => p.Name != "value")?.Name ??
                (node.Input.Kind == "object" ? node.Input.Members.FirstOrDefault(m => m.Name != "value")?.Name : null) ?? candidate.Split('/').Last();
            var stem = new string(field.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').Take(48).ToArray());
            if (stem.Length == 0 || !char.IsAsciiLetter(stem[0]) && stem[0] != '_') stem = "binding_" + stem;
            var name = stem; var suffix = 2;
            while (!used.Add(name)) name = stem + "_" + (suffix++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (candidate == key) return name;
        }
        throw new InvalidOperationException("A fused binding has no local identity.");
    }
    private static string OutputAddress(string key, LoweringScope scope) => scope.LocalOutputs.Contains(key)
        ? LocalName(key, scope) : scope.FusedOutputs.TryGetValue(key, out var group)
        ? "data.steps." + scope.NodeIds[group[0]] + "." + LocalName(key, scope) : "data.steps." + scope.NodeIds[key];

    private static IEnumerable<PlanningValue> BindingValues(PlanningValue? value) => value is null ? [] :
        new[] { value }.Concat(value.Items.SelectMany(BindingValues)).Concat(value.Members.SelectMany(m => BindingValues(m.Value)));

    private static void PrepareFusion(PlanningWorkflow workflow, LoweringScope scope, bool consumerBindings = false)
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
            void Flush(PlanningNode? consumer = null)
            {
                var group = run.ToArray();
                var inline = consumerBindings && group.Length > 0 && consumer is not null && TryConsumer(consumer, group);
                if (run.Count > 1 || inline) foreach (var key in group) scope.FusedOutputs.Add(key, group);
                run.Clear();
            }
            foreach (var node in nodes)
            {
                if (node.InternalRole?.StartsWith("inline:", StringComparison.Ordinal) == true) continue;
                if (Eligible(node)) run.Add(node.Key); else Flush(node);
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

        bool TryConsumer(PlanningNode consumer, string[] group)
        {
            if (consumer.If is not null || consumer.Expr is not null || consumer.Output is not null || consumer.Retry is not null || consumer.OnError.Count > 0 ||
                consumer.Type == "set" || consumer.Input.Kind != "object" ||
                consumer.Steps.Count + consumer.Default.Count + consumer.Cases.Count + consumer.Branches.Count != 0) return false;
            bool Reads(PlanningValue value) => value.Kind is "output" or "present" && value.Source is { } source && group.Contains(source, StringComparer.Ordinal);
            if (!BindingValues(consumer.Input).Any(Reads) || workflow.Outputs.SelectMany(o => BindingValues(o.Value)).Any(Reads) ||
                scope.Nodes.Values.Where(n => n.Key != consumer.Key && !group.Contains(n.Key, StringComparer.Ordinal)).SelectMany(Values).Any(Reads)) return false;
            // Exactly one dynamic input subtree: check once, before the consumer,
            // without changing the evaluation order of independent expressions.
            var value = consumer.Input; var path = new List<string>();
            while (value.Kind == "object")
            {
                var dynamic = value.Members.Where(m => !Static(m.Value)).ToArray();
                if (dynamic.Length != 1) break;
                path.Add(dynamic[0].Name); value = dynamic[0].Value;
            }
            if (path.Count == 0) return false;
            if (consumer.CapabilityId is { } id)
            {
                var contract = scope.Catalog.Capabilities.Single(c => c.Id == id);
                if (contract.FixedInput.ContainsKey(path[0]) || consumer.Type == "mcp.call" && path[0] != "request") return false;
                var pointer = "/" + string.Join('/', path.Select(p => p.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
                if (contract.RequestBindings.Any(b => pointer == "/request" + b.Path || pointer.StartsWith("/request" + b.Path + "/", StringComparison.Ordinal) ||
                    ("/request" + b.Path).StartsWith(pointer + "/", StringComparison.Ordinal))) return false;
            }
            scope.ConsumerBindings.Add(consumer.Key, (group, path.ToArray(), value));
            return true;
        }

        static bool Static(PlanningValue value) => value.Kind is "null" or "string" or "number" or "boolean" or "workflow" ||
            value.Kind == "array" && value.Items.All(Static) || value.Kind == "object" && value.Members.All(m => Static(m.Value));
    }

    private static JsonObject LowerFused(string[] group, LoweringScope scope, PlanningValue? consumerInput = null)
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
        if (consumerInput is not null) program.Append("return ").Append(ToExpression(consumerInput, local)[2..^1]).Append(";})()");
        else program.Append("return ({").Append(string.Join(',', group.Where(scope.FusedExports.Contains).Select(k => LocalName(k, scope) + ":" + LocalName(k, scope)))).Append("});})()");
        ExpressionEvaluator.Validate(program.ToString());
        var result = new JsonObject { ["id"] = scope.NodeIds[group[0]], ["type"] = "set",
            ["input"] = "${checkedMapping(\n" + (scope.NativeMappings ? ProgramLiteral(program.ToString(), scope) : JsonValue.Create(program.ToString())!.ToJsonString(new System.Text.Json.JsonSerializerOptions(PlanningJsonContext.Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) + ",data," + contracts.ToJsonString() + "\n)}",
            ["output_schema"] = new JsonObject { ["type"] = "object", ["properties"] = fields,
                ["required"] = new JsonArray(group.Where(scope.FusedExports.Contains).Select(k => (JsonNode?)JsonValue.Create(LocalName(k, scope))).ToArray()), ["additionalProperties"] = false } };
        if (scope.Descriptions) result["description"] = "Check and assemble consecutive deterministic values.";
        return result;
    }
}
