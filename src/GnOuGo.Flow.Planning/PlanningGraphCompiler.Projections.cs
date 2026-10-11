using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    // A pure loop's historical call/outputs envelope is unnecessary only when
    // every observer selects declared iteration exports. Whole-result, presence,
    // handler and raw-expression observers retain the historical representation.
    private static void PrepareCopyProjections(PlanningWorkflow workflow, LoweringScope scope)
    {
        var candidates = scope.Nodes.Values.Where(n => n.InternalRole is "typed_projection" or "typed_index_projection" &&
            n.If is null && n.Expr is null && n.Output is null && n.CapabilityId is null && n.Retry is null && n.OnError.Count == 0 && n.Steps.Count == 1)
            .ToDictionary(n => n.Key, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Inspect(PlanningValue? value)
        {
            if (value is null) return;
            if (value.Kind is "expression" or "template") { candidates.Clear(); return; }
            if (value.Kind == "projection" && PlanningGraphValidation.Member(value, "each")?.Boolean == true &&
                PlanningGraphValidation.Member(value, "value") is { Kind: "output", Source: not null, ResultChannel: null or "default", Path.Count: 1 } source &&
                source.Path[0] == "results" && candidates.TryGetValue(source.Source, out var loop) &&
                PlanningGraphValidation.Literal(PlanningGraphValidation.Member(value, "paths")!) is JsonArray { Count: > 0 } paths)
            {
                var call = loop.Steps[0];
                var body = scope.Workflows[PlanningGraphValidation.Member(call.Input, "ref")!.Source!];
                if (paths.All(p => p is JsonArray { Count: >= 3 } path && path[0]?.ToString() == call.Key &&
                    path[1]?.ToString() == "outputs" && body.Outputs.Any(o => o.Name == path[2]?.ToString())))
                { used.Add(source.Source); return; }
            }
            if (value.Source is not null && value.Kind is "output" or "present" or "loop_previous") candidates.Remove(value.Source);
            foreach (var member in value.Members) Inspect(member.Value);
            foreach (var item in value.Items) Inspect(item);
        }
        foreach (var node in scope.Nodes.Values)
        {
            Inspect(node.Input); Inspect(node.If); Inspect(node.Expr);
            foreach (var branch in node.Cases) Inspect(branch.When);
            foreach (var handler in node.OnError) { Inspect(handler.If); Inspect(handler.SetOutput); }
        }
        foreach (var port in workflow.Outputs) Inspect(port.Value);
        scope.DirectCopyOutputs.UnionWith(used.Where(candidates.ContainsKey));
    }

    // Only compiler-marked copy loops qualify. Business loops retain their scopes,
    // effects, ordering and runtime receipts.
    private static JsonObject LowerCopyLoop(PlanningNode loop, LoweringScope scope)
    {
        var call = loop.Steps.Single();
        var body = scope.Workflows[PlanningGraphValidation.Member(call.Input, "ref")!.Source!];
        var args = PlanningGraphValidation.Member(call.Input, "args")!;
        var indexed = loop.InternalRole == "typed_index_projection";
        var captures = new List<PlanningMember>();
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args.Members)
        {
            if (arg.Value.Kind == "loop_item" && arg.Value.Source == loop.Key)
                inputs[arg.Name] = "item" + string.Concat(arg.Value.Path.Select(Segment));
            else if (indexed && arg.Value.Kind == "loop_index" && arg.Value.Source == loop.Key)
                inputs[arg.Name] = "index";
            else
            {
                captures.Add(arg);
                inputs[arg.Name] = "source" + Segment(arg.Name);
            }
        }
        const string collection = "__collection";
        captures.Add(new(collection, PlanningGraphValidation.Member(loop.Input, "items")!));
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, PlanningValue>(StringComparer.Ordinal);
        string ResolveOutput(PlanningValue value)
        {
            // Drop only sole-property wrappers: discarding siblings could skip
            // missing-value checks or an independently constrained value.
            if ((indexed || scope.CompactProjections) && definitions.TryGetValue(value.Source!, out var definition))
            {
                var remaining = value.Path;
                while (remaining.Count > 0 && definition.Kind == "object" && definition.Members.Count == 1 && definition.Members[0].Name == remaining[0])
                { definition = definition.Members[0].Value; remaining = remaining.Skip(1).ToList(); }
                if (remaining.Count == 0) return Resolve(definition);
            }
            return "(" + expressions[value.Source!] + ")" + string.Concat(value.Path.Select(Segment));
        }
        string Resolve(PlanningValue value) => value.Kind switch
        {
            "input" => inputs[value.Source!] + string.Concat(value.Path.Select(Segment)),
            "output" => ResolveOutput(value),
            "object" => "({" + string.Join(",", value.Members.Select(m => Quote(m.Name) + ":" + Resolve(m.Value))) + "})",
            "array" => "[" + string.Join(",", value.Items.Select(Resolve)) + "]",
            "flatten" => FlattenExpression(Resolve(value.Items.Single())),
            "lookup" => LookupExpression(Resolve(value.Items[0]), Resolve(value.Items[1]), value.Text!),
            "null" or "string" or "number" or "boolean" => LowerValue(value, scope, allowReferences: false)?.ToJsonString() ?? "null",
            "projection" => "({value:" + SelectExpression(Resolve(PlanningGraphValidation.Member(value, "value")!),
                PlanningGraphValidation.Literal(PlanningGraphValidation.Member(value, "paths")!)!, false, scope) + "})",
            _ => throw new InvalidOperationException("A compiled copy loop contains a non-structural value.")
        };
        foreach (var node in body.Steps.Concat(body.Finally))
        {
            if (node.Type != "set" || node.Input.Kind == "dynamic_mapping")
                throw new InvalidOperationException("A compiled copy loop contains executable work.");
            expressions.Add(node.Key, Resolve(node.Input));
            definitions.Add(node.Key, node.Input);
        }
        var outputs = "({" + string.Join(",", body.Outputs.Select(o => Quote(o.Name) + ":" + Resolve(o.Value))) + "})";
        var callId = scope.NodeIds[call.Key];
        var direct = scope.DirectCopyOutputs.Contains(loop.Key);
        var record = direct ? body.Outputs.Count == 1 ? Resolve(body.Outputs[0].Value) : outputs : "({" + Quote(callId) + ":{outputs:" + outputs + "}})";
        var script = "({results:source." + collection + ".map(" + (indexed ? "(item,index)" : "item") + "=>" + record + "),count:source." + collection + ".length})";
        JintSandbox.ValidateMapping(script, learned: false);
        var source = ToExpression(new() { Kind = "object", Members = captures }, scope)[2..^1];
        var properties = new JsonObject(body.Outputs.Select(o => new KeyValuePair<string, JsonNode?>(o.Name, ToJsonSchema(o.Schema, scope.Catalog))));
        var outputSchema = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(body.Outputs.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()), ["additionalProperties"] = false };
        static JsonObject ObjectSchema(string name, JsonObject value) => new() { ["type"] = "object", ["properties"] = new JsonObject { [name] = value }, ["required"] = new JsonArray(name) };
        return new() { ["id"] = scope.NodeIds[loop.Key], ["type"] = "set",
            ["input"] = "${checkedMapping(" + ProgramLiteral(script, scope) + "," + source + ")}",
            ["output_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["results"] = new JsonObject { ["type"] = "array", ["items"] = direct ? body.Outputs.Count == 1 ?
                    ToJsonSchema(body.Outputs[0].Schema, scope.Catalog) : outputSchema : ObjectSchema(callId, ObjectSchema("outputs", outputSchema)) },
                ["count"] = new JsonObject { ["type"] = "integer" } }, ["required"] = new JsonArray("results", "count") } };
    }

    private static string LowerAssembly(PlanningNode node, LoweringScope scope)
    {
        var sources = new List<PlanningMember>();
        string Resolve(PlanningValue value, string? hint = null)
        {
            if (value.Kind == "output" && scope.Nodes[value.Source!].InternalRole == "inline:" + node.Key)
            {
                var selection = scope.Nodes[value.Source!].Input;
                var source = Resolve(PlanningGraphValidation.Member(selection, "value")!);
                var paths = PlanningGraphValidation.Literal(PlanningGraphValidation.Member(selection, "paths")!)!;
                if (!value.Path.SequenceEqual(new[] { "value" })) throw new InvalidOperationException("An inline selection must consume its checked value.");
                return SelectExpression(source, paths, false, scope);
            }
            if (value.Kind == "object") return "({" + string.Join(",", value.Members.Select(m => Quote(m.Name) + ":" + Resolve(m.Value, m.Name))) + "})";
            if (value.Kind == "array") return "[" + string.Join(",", value.Items.Select(v => Resolve(v))) + "]";
            if (value.Kind == "flatten") return FlattenExpression(Resolve(value.Items.Single()));
            if (value.Kind == "lookup") return LookupExpression(Resolve(value.Items[0]), Resolve(value.Items[1]), value.Text!);
            var name = "v" + sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (scope.ReadableMappings)
            {
                var field = hint ?? value.Path.LastOrDefault() ?? value.Source ?? "value";
                var stem = "input_" + new string(field.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').Take(48).ToArray());
                name = stem; var suffix = 2;
                while (sources.Any(s => s.Name == name)) name = stem + "_" + (suffix++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            sources.Add(new(name, value)); return "source." + name;
        }
        var script = Resolve(node.Input); JintSandbox.ValidateMapping(script, learned: false);
        var sourceExpression = ToExpression(new() { Kind = "object", Members = sources }, scope)[2..^1];
        return "${checkedMapping(" + ProgramLiteral(script, scope) + "," + sourceExpression + ")}";
    }

    private static string FlattenExpression(string source)
        => "(" + source + ").flatMap(item=>m.select(item,[[]],true))";

    private static string LookupExpression(string records, string selected, string field)
        => "m.lookup(" + records + "," + selected + "," + Quote(field) + ")";

    private static string SelectExpression(string source, JsonNode paths, bool each, LoweringScope scope)
    {
        if (scope.NativeMappings && paths is JsonArray { Count: 1 } selections && selections[0] is JsonArray path &&
            path.All(p => p!.GetValue<string>() is not ("length" or "__proto__" or "constructor" or "prototype")))
        {
            var suffix = string.Concat(path.Select(p => Segment(p!.GetValue<string>())));
            // The checked structural evaluator rejects missing members and keeps
            // present nulls and exact JSON scalars. map still requires an array.
            return each ? "(" + source + ").map(item=>item" + suffix + ")" : "(" + source + ")" + suffix;
        }
        return "m.select(" + source + "," + paths.ToJsonString() + "," + (each ? "true" : "false") + ")";
    }

    private static string ProgramLiteral(string script, LoweringScope scope)
    {
        if (!scope.NativeMappings) return Quote(script);
        // Lexical whitespace only: never split strings, regexes or template data.
        var tokens = new Acornima.Tokenizer(script);
        var formatted = new System.Text.StringBuilder(); var position = 0; var indent = 0;
        while (true)
        {
            var token = tokens.GetToken();
            if (token.Kind == Acornima.TokenKind.EOF) break;
            formatted.Append(script.AsSpan(position, token.End - position)); position = token.End;
            if (token.Kind != Acornima.TokenKind.Punctuator) continue;
            if (token.Value is "{") indent++;
            if (token.Value is "}") indent = Math.Max(0, indent - 1);
            if (token.Value is "{" or "," or ";") formatted.Append('\n').Append(' ', indent * 2);
        }
        formatted.Append(script.AsSpan(position));
        return "`\n" + formatted.ToString().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("${", "\\${", StringComparison.Ordinal) + "\n`";
    }

    private static string Quote(string value) => JsonValue.Create(value)!.ToJsonString();
}
