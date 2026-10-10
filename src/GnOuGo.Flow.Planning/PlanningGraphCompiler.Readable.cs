using System.Text;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    // Final lowering only. Expand the existing closed binding programs, keeping
    // their lexical scopes and evaluation order; no logical-plan rewriting.
    private static JsonObject ReadableStep(JsonObject step, LoweringScope scope)
    {
        if (!scope.ReadableMappings || step["input"] is null) return step;
        var metadata = new JsonObject();
        JsonNode? RewriteInput(JsonNode? value, string pointer)
        {
            if (value is JsonObject obj)
                return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, RewriteInput(p.Value,
                    pointer + "/" + p.Key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)))));
            if (value is JsonArray array)
                return new JsonArray(array.Select((item, index) => RewriteInput(item, pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || !text.Contains("checkedMapping(", StringComparison.Ordinal)) return value?.DeepClone();
            var segments = ExpressionSegments.Read(text);
            // Embedded text and historical expressions retain their own boundaries.
            if (segments.Count != 1 || !string.IsNullOrWhiteSpace(text[..segments[0].Start]) || !string.IsNullOrWhiteSpace(text[(segments[0].Start + segments[0].Length)..])) return value.DeepClone();
            var program = segments[0].Expression;
            var parsed = new Parser().ParseExpression(program);
            var contracts = new JsonObject();
            if (parsed is CallExpression { Callee: Identifier { Name: "checkedMapping" }, Arguments.Count: 3 } group)
            {
                contracts = JsonNode.Parse(program[group.Arguments[2].Start..group.Arguments[2].End])!.AsObject();
                program = MappingProgram(group.Arguments[0]);
            }
            var expanded = Expand(program);
            ExpressionEvaluator.Validate(expanded);
            metadata[pointer] = contracts;
            return JsonValue.Create("${\n" + FormatProgram(expanded) + "\n}");
        }
        step["input"] = RewriteInput(step["input"], "");
        if (metadata.Count != 0) step["expression_contracts"] = metadata;
        return step;

        static string MappingProgram(Node node) => node switch
        {
            StringLiteral text => text.Value,
            TemplateLiteral { Expressions.Count: 0, Quasis.Count: 1 } template => template.Quasis[0].Value.Cooked!,
            _ => throw new InvalidOperationException("Compiler programs must be constant.")
        };
        static string Expand(string program)
        {
            var syntax = new Parser().ParseExpression(program);
            string Rewrite(Node node)
            {
                if (node is StringLiteral literal)
                    return JsonValue.Create(literal.Value)!.ToJsonString(new System.Text.Json.JsonSerializerOptions(GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.Options)
                        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                if (node is CallExpression { Callee: Identifier { Name: "checkedMapping" }, Arguments.Count: 2 } mapping)
                {
                    var source = Rewrite(mapping.Arguments[1]);
                    var body = Expand(MappingProgram(mapping.Arguments[0]));
                    static bool Address(Node value) => value is Identifier || value is MemberExpression member && Address(member.Object) &&
                        (member.Computed ? member.Property is StringLiteral : member.Property is Identifier);
                    if (Address(mapping.Arguments[1]) && SubstituteSource(body, "(" + source + ")", null) is { } direct)
                        return direct;
                    if (mapping.Arguments[1] is ObjectExpression captured && captured.Properties.All(p => p is Property { Computed: false, Method: false, Shorthand: false }))
                    {
                        var properties = captured.Properties.Cast<Property>().ToArray();
                        IEnumerable<Node> Nodes(Node value) => new[] { value }.Concat(value.ChildNodes.SelectMany(Nodes));
                        var used = Nodes(new Parser().ParseExpression(body)).OfType<Identifier>().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                        var bindings = properties.Select((p, i) =>
                        {
                            var field = p.Key is Identifier id ? id.Name : ((StringLiteral)p.Key).Value;
                            var stem = "binding_" + (Identifier().IsMatch(field) ? field : i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            var name = stem; var suffix = 2;
                            while (!used.Add(name)) name = stem + "_" + (suffix++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            return (Field: field, Name: name);
                        }).ToArray();
                        // Destructure compiler capture envelopes without copying
                        // their referenced observations into a temporary object.
                        var rewritten = SubstituteSource(body, null, bindings.ToDictionary(p => p.Field, p => p.Name, StringComparer.Ordinal));
                        if (rewritten is not null)
                            return "((" + string.Join(',', bindings.Select(p => p.Name)) + ")=>(" + rewritten + "))(" +
                                string.Join(',', properties.Select(p => Rewrite(p.Value))) + ")";
                    }
                    // A lexical parameter evaluates the source exactly once and
                    // cannot collide with callback parameters or literal text.
                    return "((source)=>(" + body + "))(" + source + ")";
                }
                if (node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: "flatMap" } } collection, Arguments.Count: 1 } flatten &&
                    flatten.Arguments[0] is ArrowFunctionExpression { Params.Count: 1, Body: CallExpression { Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "select" } }, Arguments.Count: 3 } selection } &&
                    selection.Arguments[1] is ArrayExpression { Elements.Count: 1 } paths && paths.Elements[0] is ArrayExpression { Elements.Count: 0 } &&
                    selection.Arguments[2] is BooleanLiteral { Value: true } && selection.Arguments[0] is Identifier { Name: "item" })
                    // Only flatMap's existing checked inner-array recipe. The
                    // structural evaluator still validates every inner array.
                    return "(" + Rewrite(collection.Object) + ").flatMap(item=>item)";
                var text = new StringBuilder(); var position = node.Start;
                foreach (var child in node.ChildNodes)
                {
                    // Shorthand properties can expose the same identifier twice.
                    if (child.Start < position) continue;
                    text.Append(program.AsSpan(position, child.Start - position)).Append(Rewrite(child));
                    position = child.End;
                }
                return text.Append(program.AsSpan(position, node.End - position)).ToString();
            }
            return program[..syntax.Start] + Rewrite(syntax) + program[syntax.End..];
        }

        static string? SubstituteSource(string program, string? source, Dictionary<string, string>? fields)
        {
            var syntax = new Parser().ParseExpression(program); var wholeSource = false; var references = 0;
            string Rewrite(Node node, bool shadowed = false)
            {
                if (!shadowed && fields is not null && node is MemberExpression { Object: Identifier { Name: "source" } } member &&
                    (member.Computed ? member.Property is StringLiteral : member.Property is Identifier))
                {
                    var key = member.Property is Identifier id ? id.Name : ((StringLiteral)member.Property).Value;
                    if (fields.TryGetValue(key, out var name)) { references++; return name; }
                }
                if (!shadowed && node is Identifier { Name: "source" })
                { references++; if (source is null) wholeSource = true; else return source; }
                if (node is ArrowFunctionExpression arrow && arrow.Params.OfType<Identifier>().Any(p => p.Name == "source")) shadowed = true;
                var text = new StringBuilder(); var position = node.Start;
                foreach (var child in node.ChildNodes)
                {
                    if (child.Start < position) continue;
                    var literalKey = node is MemberExpression { Computed: false } access && child == access.Property ||
                        node is Property { Computed: false } property && child == property.Key;
                    text.Append(program.AsSpan(position, child.Start - position)).Append(literalKey ? program[child.Start..child.End] : Rewrite(child, shadowed));
                    position = child.End;
                }
                return text.Append(program.AsSpan(position, node.End - position)).ToString();
            }
            var result = program[..syntax.Start] + Rewrite(syntax) + program[syntax.End..];
            return wholeSource || references == 0 ? null : result;
        }
    }

    private static string FormatProgram(string program)
    {
        var tokens = new Tokenizer(program); var result = new StringBuilder(); var position = 0; var indent = 0;
        while (true)
        {
            var token = tokens.GetToken(); if (token.Kind == TokenKind.EOF) break;
            var gap = program[position..token.Start];
            if (!string.IsNullOrWhiteSpace(gap)) result.Append(gap);
            else if (gap.Length > 0 && result.Length > 0 && !char.IsWhiteSpace(result[^1])) result.Append(' ');
            if (token.Kind == TokenKind.Punctuator && token.Value is "}")
            {
                indent = Math.Max(0, indent - 1);
                NewLine();
            }
            if (result.Length > 0 && result[^1] == '\n') result.Append(' ', indent * 2);
            result.Append(program.AsSpan(token.Start, token.End - token.Start)); position = token.End;
            if (token.Kind != TokenKind.Punctuator) continue;
            if (token.Value is "{") indent++;
            if (token.Value is "{" or "," or ";") NewLine();
        }
        return result.ToString().Trim();

        void NewLine()
        {
            while (result.Length > 0 && result[^1] == ' ') result.Length--;
            if (result.Length > 0 && result[^1] != '\n') result.Append('\n');
        }
    }
}
