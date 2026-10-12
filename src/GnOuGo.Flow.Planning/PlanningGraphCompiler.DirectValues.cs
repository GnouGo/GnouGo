using System.Text;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    // A local rewrite of the existing checked program. Do not infer equivalence
    // for arbitrary JSON Schemas: unrecognized container constraints stay put.
    private static string DirectCheckedValues(string program, JsonObject contracts)
    {
        if (new Parser().ParseExpression(program) is not CallExpression { Callee: ArrowFunctionExpression
            { Body: BlockStatement body } } || body.Body.Last() is not ReturnStatement { Argument: not null } returned) return program;
        var locals = new Dictionary<string, (string Source, string? Field)>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var result = new StringBuilder("(()=>{");
        var order = 0;
        foreach (var statement in body.Body.SkipLast(1))
        {
            if (statement is not VariableDeclaration { Declarations.Count: 1 } declaration ||
                declaration.Declarations[0] is not { Id: Identifier name, Init: Expression value })
                throw new InvalidOperationException("Invalid compiler-owned checked declaration.");
            var contract = contracts[name.Name]!.AsObject();
            var schema = contract["schema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
            string? field = null;
            Node selected = value;
            if (value is ObjectExpression { Properties.Count: 1 } obj &&
                obj.Properties[0] is Property { Computed: false, Method: false } property &&
                Key(property.Key) is { } propertyName && Unwrap(schema, propertyName) is { } child)
            {
                field = propertyName; selected = property.Value; schema = child;
            }
            else if (value is Identifier alias && locals.TryGetValue(alias.Name, out var original) && original.Field is { } aliasField &&
                Unwrap(schema, aliasField) is { } aliasSchema)
            {
                field = aliasField; schema = aliasSchema;
            }
            var expression = field is not null && value is Identifier previous && locals.TryGetValue(previous.Name, out var prior)
                ? prior.Source : Write(selected, new(StringComparer.Ordinal));
            var source = new Parser().ParseExpression(expression) is Identifier same && declared.Contains(same.Name) ? same.Name : name.Name;
            if (source == name.Name)
            {
                result.Append("const ").Append(name.Name).Append(" = ").Append(expression).Append(';');
                declared.Add(name.Name);
            }
            contract["schema"] = schema.DeepClone();
            contract["order"] = order++;
            contract["source"] = source;
            if (field is not null) contract["instance_path"] = "/" + field.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            else contract["object_only"] = true;
            locals[name.Name] = (source, field);
        }
        result.Append("return ").Append(Write(returned.Argument, new(StringComparer.Ordinal))).Append(";})()");
        return SimplifyReadableProgram(result.ToString(), inlineConsumers: true);

        static string? Key(Node node) => node is Identifier key ? key.Name : (node as StringLiteral)?.Value;
        static JsonObject? Unwrap(JsonObject schema, string field)
        {
            if (schema["type"]?.ToString() != "object" ||
                schema.Any(p => p.Key is not ("type" or "properties" or "required" or "additionalProperties" or "title" or "description")) ||
                schema["properties"] is not JsonObject properties || properties.Count != 1 || properties[field] is not JsonObject child ||
                schema["required"] is JsonArray required && required.Any(v => v?.ToString() != field) ||
                schema["additionalProperties"] is JsonObject) return null;
            bool References(JsonNode? node) => node switch
            {
                JsonObject obj => obj.Any(p => p.Key is "$ref" or "$id" || References(p.Value)),
                JsonArray array => array.Any(References), _ => false
            };
            return References(child) ? null : child;
        }
        string Write(Node node, HashSet<string> shadowed)
        {
            if (node is MemberExpression { Object: Identifier owner } member && !shadowed.Contains(owner.Name) &&
                locals.TryGetValue(owner.Name, out var selected) && selected.Field is { } field &&
                (member.Computed ? (member.Property as StringLiteral)?.Value : Key(member.Property)) == field) return selected.Source;
            if (node is Identifier identifier && !shadowed.Contains(identifier.Name) && locals.TryGetValue(identifier.Name, out var local))
                return local.Field is null ? local.Source : "({" + JsonValue.Create(local.Field)!.ToJsonString() + ":" + local.Source + "})";
            if (node is ArrowFunctionExpression arrow)
            {
                shadowed = new(shadowed, StringComparer.Ordinal);
                foreach (var parameter in arrow.Params.OfType<Identifier>()) shadowed.Add(parameter.Name);
            }
            if (node is Property { Shorthand: true, Value: Identifier id } property && !shadowed.Contains(id.Name) && locals.ContainsKey(id.Name))
                return program[property.Key.Start..property.Key.End] + ":" + Write(id, shadowed);
            var text = new StringBuilder(); var position = node.Start;
            foreach (var child in node.ChildNodes)
            {
                if (child.Start < position) continue;
                var literalKey = node is MemberExpression { Computed: false } access && child == access.Property ||
                    node is Property { Computed: false } prop && child == prop.Key;
                text.Append(program.AsSpan(position, child.Start - position)).Append(literalKey ? program[child.Start..child.End] : Write(child, shadowed));
                position = child.End;
            }
            return text.Append(program.AsSpan(position, node.End - position)).ToString();
        }
    }
}
