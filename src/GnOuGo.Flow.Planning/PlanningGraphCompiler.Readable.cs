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
            var expanded = Expand(program, contracts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal));
            if (scope.LeanMappings) expanded = SimplifyReadableProgram(expanded, scope.InlineConsumers);
            if (scope.DirectValues && !scope.ReadableBranches && contracts.Count != 0) expanded = DirectCheckedValues(expanded, contracts);
            ExpressionEvaluator.Validate(expanded);
            if (step["__lexical_contracts"] is JsonObject nested && (step["__lexical_pointer"] is null || step["__lexical_pointer"]!.GetValue<string>() == pointer))
            {
                foreach (var (name, entry) in nested) contracts.Add(name, entry?.DeepClone());
                if (!scope.ReadableBranches) QualifyLexicalContracts(expanded, contracts);
            }
            if (scope.ReadableBranches && contracts.Count != 0) expanded = DirectLexicalValues(expanded, contracts, scope.SimplifiedLocals);
            metadata[pointer] = contracts;
            return JsonValue.Create("${\n" + FormatProgram(expanded, scope.CompactProjections, scope.ReadableBranches) + "\n}");
        }
        step["input"] = RewriteInput(step["input"], "");
        if (metadata.Count != 0) step["expression_contracts"] = metadata;
        if (step["__lexical_contracts"] is JsonObject standalone && metadata.Count == 0)
        {
            var text = step["input"]!.GetValue<string>();
            var program = ExpressionSegments.Read(text).Single().Expression;
            var checks = standalone.DeepClone().AsObject();
            if (scope.ReadableBranches) program = DirectLexicalValues(program, checks, scope.SimplifiedLocals);
            else QualifyLexicalContracts(program, checks);
            step["expression_contracts"] = new JsonObject { [""] = checks };
            step["input"] = "${\n" + FormatProgram(program, true, scope.ReadableBranches) + "\n}";
        }
        step.Remove("__lexical_contracts"); step.Remove("__lexical_pointer");
        return step;

        static string MappingProgram(Node node) => node switch
        {
            StringLiteral text => text.Value,
            TemplateLiteral { Expressions.Count: 0, Quasis.Count: 1 } template => template.Quasis[0].Value.Cooked!,
            _ => throw new InvalidOperationException("Compiler programs must be constant.")
        };
        string Expand(string program, HashSet<string> checkedLocals)
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
                    var body = Expand(MappingProgram(mapping.Arguments[0]), checkedLocals);
                    // A replaced call may follow `return`. Whitespace from the
                    // nested program must not introduce automatic semicolon insertion.
                    if (scope.ReadableBranches) body = body.Trim();
                    static bool Address(Node value) => value is Identifier || value is MemberExpression member && Address(member.Object) &&
                        (member.Computed ? member.Property is StringLiteral : member.Property is Identifier);
                    if (Address(mapping.Arguments[1]) && SubstituteSource(body, "(" + source + ")", null) is { } direct)
                        return direct;
                    if (mapping.Arguments[1] is ObjectExpression captured && captured.Properties.All(p => p is Property { Computed: false, Method: false, Shorthand: false }))
                    {
                        var properties = captured.Properties.Cast<Property>().ToArray();
                        // Strict lookup already evaluates its two operands once,
                        // in order. Its capture envelope has no independent check.
                        if (scope.DirectValues && properties.Length == 2 && new Parser().ParseExpression(body) is
                            CallExpression { Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "lookup" } }, Arguments.Count: 3 } lookup &&
                            lookup.Arguments[2] is StringLiteral && Enumerable.Range(0, 2).All(i =>
                                lookup.Arguments[i] is MemberExpression { Object: Identifier { Name: "source" }, Computed: false, Property: Identifier field } &&
                                field.Name == (properties[i].Key is Identifier key ? key.Name : (properties[i].Key as StringLiteral)?.Value)))
                            return "m.lookup(" + string.Join(", ", properties.Select(p => Rewrite(p.Value))) + ", " +
                                body[lookup.Arguments[2].Start..lookup.Arguments[2].End] + ")";
                        if (scope.InlineConsumers && properties.Length == 1)
                        {
                            var argument = properties[0].Value;
                            Node root = argument;
                            while (root is MemberExpression member) root = member.Object;
                            var bodySyntax = new Parser().ParseExpression(body);
                            // Literal operands cannot fail. A single checked
                            // address may move into a sole assembly expression
                            // only when it is that expression's first operand.
                            Node? first = bodySyntax is ObjectExpression { Properties.Count: 1 } obj && obj.Properties[0] is Property property ? property.Value : null;
                            while (first is CallExpression { Callee: MemberExpression method }) first = method.Object;
                            while (first is MemberExpression member) first = member.Object;
                            if (argument is Literal || Address(argument) && root is Identifier local && checkedLocals.Contains(local.Name) && first is Identifier { Name: "source" })
                            {
                                var field = properties[0].Key is Identifier key ? key.Name : ((StringLiteral)properties[0].Key).Value;
                                var directCapture = SubstituteSource(body, null, new(StringComparer.Ordinal) { [field] = "(" + Rewrite(argument) + ")" }, single: true);
                                if (directCapture is not null) return directCapture;
                            }
                        }
                        // Keep the capture boundary: an enclosing branch can
                        // refine a nullable input even when its declared producer
                        // contract remains nullable. Ordered checks still belong
                        // at this independent export boundary.
                        IEnumerable<Node> Nodes(Node value) => new[] { value }.Concat(value.ChildNodes.SelectMany(Nodes));
                        var bodyNodes = Nodes(new Parser().ParseExpression(body)).ToArray();
                        var used = bodyNodes.OfType<Identifier>().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                        if (scope.InlineConsumers) used.UnionWith(BindingNames());
                        var bindings = properties.Select((p, i) =>
                        {
                            var field = p.Key is Identifier id ? id.Name : ((StringLiteral)p.Key).Value;
                            var stem = "binding_" + (Identifier().IsMatch(field) ? field : i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            if (scope.InlineConsumers)
                            {
                                // Use the declared receiving field when a
                                // captured input has only a physical identity.
                                // This affects lexical names, never addressing.
                                var hint = field.Contains("task_", StringComparison.Ordinal) ? bodyNodes.OfType<Property>().FirstOrDefault(property =>
                                {
                                    Node selected = property.Value;
                                    while (selected is MemberExpression member)
                                    {
                                        if (member.Object is Identifier { Name: "source" })
                                            return (member.Computed ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name) == field;
                                        selected = member.Object;
                                    }
                                    return false;
                                })?.Key : null;
                                stem = ProducerName(p.Value, hint is Identifier target ? target.Name : hint is StringLiteral label ? label.Value : field);
                            }
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

        string ProducerName(Node value, string fallback)
        {
            var path = new List<string>();
            while (value is MemberExpression member && (!member.Computed || member.Property is StringLiteral))
            {
                path.Insert(0, member.Property is Identifier property ? property.Name : ((StringLiteral)member.Property).Value);
                value = member.Object;
            }
            if (value is Identifier { Name: "data" } && path.Count >= 2 && path[0] == "steps")
            {
                var producer = scope.NodeIds.FirstOrDefault(p => p.Value == path[1]).Key;
                if (producer is not null && scope.Nodes[producer].CapabilityId is { } capability &&
                    scope.Catalog.Capabilities.Single(c => c.Id == capability).Method is { } method)
                    return FunctionalName(method + " " + string.Join(' ', path.Skip(2).Where(p => p is not ("response" or "outputs" or "value"))));
            }
            if (value is Identifier id && id.Name != "data" && !id.Name.StartsWith("checkedRecord", StringComparison.Ordinal))
                return FunctionalName(id.Name + " " + string.Join(' ', path.Where(p => p != "value")));
            var name = fallback.StartsWith("input_", StringComparison.Ordinal) ? fallback[6..] : fallback.Trim('_');
            return FunctionalName(name == "value" ? "inputValue" : name);
        }

        static string? SubstituteSource(string program, string? source, Dictionary<string, string>? fields, bool single = false)
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
            return wholeSource || references == 0 || single && references != 1 ? null : result;
        }
    }

    // Only compiler-owned expressions enter here. Reuse a checked local when
    // an object literal copies exactly its known fields; keep both declarations
    // and their ordered contracts. Never infer an object's shape from its name.
    private static string SimplifyReadableProgram(string program, bool inlineConsumers = false, bool simplifiedLocals = false)
    {
        var syntax = new Parser().ParseExpression(program);
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        static string? Key(Node key) => key is Identifier id ? id.Name : (key as StringLiteral)?.Value;
        string Write(Node node)
        {
            switch (node)
            {
                case VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } declaration
                    when declaration.Declarations[0] is { Id: Identifier name, Init: Expression value }:
                {
                    var expression = Write(value);
                    if (value is ObjectExpression obj && obj.Properties.All(p => p is Property { Computed: false, Method: false, Shorthand: false }))
                    {
                        var properties = obj.Properties.Cast<Property>().ToArray();
                        fields[name.Name] = properties.Select(p => Key(p.Key)!).ToArray();
                        if (properties.Length > 0 && properties[0].Value is MemberExpression { Object: Identifier source } &&
                            fields.TryGetValue(source.Name, out var original) && original.SequenceEqual(fields[name.Name]) &&
                            properties.All(p => p.Value is MemberExpression { Object: Identifier owner } member && owner.Name == source.Name &&
                                Key(p.Key) == (member.Computed ? (member.Property as StringLiteral)?.Value : Key(member.Property))))
                            expression = source.Name;
                    }
                    else if (inlineConsumers && value is Identifier alias && fields.TryGetValue(alias.Name, out var aliasFields)) fields[name.Name] = aliasFields;
                    return "const " + name.Name + " = " + expression + ";";
                }
                case ReturnStatement { Argument: not null } returned:
                    return "return " + Write(returned.Argument) + ";";
                case MemberExpression { Object: ObjectExpression { Properties.Count: 1 } wrapper } selection
                    when inlineConsumers && wrapper.Properties[0] is Property { Computed: false, Method: false, Shorthand: false } only &&
                        Key(only.Key) == (selection.Computed ? (selection.Property as StringLiteral)?.Value : Key(selection.Property)):
                    // A one-member literal immediately read at the same position
                    // has no independent contract. Checked const declarations
                    // remain untouched, including ones not used by the return.
                    return Operand(only.Value);
                case MemberExpression member:
                    // Existing semantic validation treats a parenthesized
                    // producer address as a checked projection boundary. Keep
                    // that single boundary, removing only redundant nesting.
                    var address = Operand(member.Object);
                    Node root = member.Object;
                    while (root is MemberExpression parent) root = parent.Object;
                    if (program.AsSpan(member.Object.End, member.Property.Start - member.Object.End).Contains(')') &&
                        !(inlineConsumers && root is Identifier local && fields.ContainsKey(local.Name)))
                        address = "(" + address + ")";
                    return address + (member.Computed ? "[" + Write(member.Property) + "]" : "." + Write(member.Property));
                case CallExpression { Arguments.Count: 1, Callee: ArrowFunctionExpression { Params.Count: 1,
                    Body: ObjectExpression { Properties.Count: 1 } wrapped } wrapperFunction } wrapperCall
                    when simplifiedLocals && wrapperFunction.Params[0] is Identifier wrapperParameter &&
                         wrapped.Properties[0] is Property { Computed: false, Method: false, Value: Identifier wrappedValue } wrapperProperty &&
                         wrapperParameter.Name == wrappedValue.Name && Key(wrapperProperty.Key) is { } wrapperKey:
                    // One argument, used once: preserve its evaluation before
                    // construction. Contract-based unwrapping runs separately.
                    return "{" + Quote(wrapperKey) + ":" + Write(wrapperCall.Arguments[0]) + "}";
                case CallExpression { Arguments.Count: 1, Callee: ArrowFunctionExpression
                    { Params.Count: 1, Body: Expression body } immediateFunction } immediate
                    when simplifiedLocals && immediateFunction.Params[0] is Identifier parameter &&
                         immediate.Arguments[0] is Identifier argument && argument.Name == parameter.Name:
                    // The argument is an already evaluated local. The callback
                    // binds it under exactly the same name and adds no checks.
                    return Write(body);
                case CallExpression call:
                    return Operand(call.Callee) + "(" + string.Join(", ", call.Arguments.Select(Write)) + ")";
                case ArrowFunctionExpression arrow:
                    // Keep each callback's names and lexical boundary intact.
                    var saved = fields.ToArray();
                    foreach (var parameter in arrow.Params.OfType<Identifier>()) fields.Remove(parameter.Name);
                    var parameters = string.Join(", ", arrow.Params.Select(Write));
                    var callback = (inlineConsumers && arrow.Params.Count == 1 ? parameters : "(" + parameters + ")") + " => " +
                        (arrow.Body is ObjectExpression ? "(" + Write(arrow.Body) + ")" : Write(arrow.Body));
                    fields.Clear(); foreach (var field in saved) fields.Add(field.Key, field.Value);
                    return callback;
                case ObjectExpression obj:
                    return "{" + string.Join(", ", obj.Properties.Select(Write)) + "}";
                case ArrayExpression array:
                    return "[" + string.Join(", ", array.Elements.Select(e => e is null ? "" : Write(e))) + "]";
                case Property { Computed: false, Method: false, Shorthand: false } property:
                    var key = Key(property.Key);
                    if (property.Value is Identifier identifier && identifier.Name == key) return identifier.Name;
                    return (key is not null && Identifier().IsMatch(key) ? key : Write(property.Key)) + ": " + Write(property.Value);
            }
            var result = new StringBuilder(); var position = node.Start;
            foreach (var child in node.ChildNodes)
            {
                if (child.Start < position) continue;
                result.Append(program.AsSpan(position, child.Start - position)).Append(Write(child));
                position = child.End;
            }
            return result.Append(program.AsSpan(position, node.End - position)).ToString();
        }
        string Operand(Node node) => node is Acornima.Ast.Identifier or MemberExpression or CallExpression or ArrayExpression
            ? Write(node) : "(" + Write(node) + ")";
        return Write(syntax);
    }

    private static string FormatProgram(string program, bool compact, bool lexical = false)
    {
        var propertyCommas = new HashSet<int>();
        var branchBreaks = new HashSet<int>();
        if (compact)
        {
            void Inspect(Node node)
            {
                if (lexical && node is ConditionalExpression conditional)
                {
                    branchBreaks.Add(conditional.Test.End + program.AsSpan(conditional.Test.End, conditional.Consequent.Start - conditional.Test.End).IndexOf('?'));
                    branchBreaks.Add(conditional.Consequent.End + program.AsSpan(conditional.Consequent.End, conditional.Alternate.Start - conditional.Consequent.End).IndexOf(':'));
                }
                if (node is ObjectExpression obj)
                    for (var i = 1; i < obj.Properties.Count; i++)
                    {
                        var start = obj.Properties[i - 1].End;
                        var comma = program.AsSpan(start, obj.Properties[i].Start - start).IndexOf(',');
                        if (comma >= 0) propertyCommas.Add(start + comma);
                    }
                foreach (var child in node.ChildNodes) Inspect(child);
            }
            Inspect(new Parser().ParseExpression(program));
        }
        var tokens = new Tokenizer(program); var result = new StringBuilder(); var position = 0; var indent = 0;
        while (true)
        {
            var token = tokens.GetToken(); if (token.Kind == TokenKind.EOF) break;
            if (branchBreaks.Contains(token.Start)) NewLine();
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
            if (token.Value is "{" or ";" || token.Value is "," && (!compact || propertyCommas.Contains(token.Start))) NewLine();
        }
        return result.ToString().Trim();

        void NewLine()
        {
            while (result.Length > 0 && result[^1] == ' ') result.Length--;
            if (result.Length > 0 && result[^1] != '\n') result.Append('\n');
        }
    }
}
