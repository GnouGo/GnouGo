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
                Key(property.Key) is { } propertyName && Unwrap(schema, propertyName) is { } child &&
                !ObservesIdentity(name.Name, propertyName))
            {
                field = propertyName; selected = property.Value; schema = child;
            }
            else if (value is Identifier alias && locals.TryGetValue(alias.Name, out var original) && original.Field is { } aliasField &&
                Unwrap(schema, aliasField) is { } aliasSchema && !ObservesIdentity(name.Name, aliasField))
            {
                field = aliasField; schema = aliasSchema;
            }
            var expression = field is not null && value is Identifier previous && locals.TryGetValue(previous.Name, out var prior)
                ? prior.Source : Write(selected, new(StringComparer.Ordinal));
            var source = new Parser().ParseExpression(expression) is Identifier same && declared.Contains(same.Name) ? same.Name : name.Name;
            if (source != name.Name && !Scalar(schema) && (ObservesIdentity(name.Name, null) || ObservesIdentity(source, null)))
                source = name.Name; // Keep the original declaration's isolated value when identity is observable.
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
        static bool Scalar(JsonObject schema)
        {
            static bool ScalarType(JsonNode? type) => type is JsonValue value && value.TryGetValue<string>(out var name) &&
                name is "string" or "number" or "integer" or "boolean" or "null";
            return ScalarType(schema["type"]) || schema["type"] is JsonArray { Count: > 0 } types && types.All(ScalarType);
        }
        bool ObservesIdentity(string name, string? field)
        {
            // Schema equivalence does not prove reference-identity equivalence.
            // Follow declaration aliases, retaining containers observed as whole
            // comparison operands or passed to calls. Selecting their field is safe.
            var aliases = new HashSet<string>(StringComparer.Ordinal) { name };
            bool added;
            do
            {
                added = false;
                foreach (var declaration in body.Body.OfType<VariableDeclaration>())
                    if (declaration.Declarations[0] is { Id: Identifier local, Init: Identifier source } &&
                        (aliases.Contains(local.Name) || aliases.Contains(source.Name)))
                        added |= aliases.Add(local.Name) | aliases.Add(source.Name);
            } while (added);
            return Walk(body, false);

            bool Walk(Node node, bool observes)
            {
                if (field is not null && node is MemberExpression { Object: Identifier owner } member && aliases.Contains(owner.Name) &&
                    (member.Computed ? (member.Property as StringLiteral)?.Value : Key(member.Property)) == field) return false;
                if (node is Identifier identifier) return observes && aliases.Contains(identifier.Name);
                observes |= node is CallExpression || node is BinaryExpression { Operator:
                    Operator.Equality or Operator.StrictEquality or Operator.Inequality or Operator.StrictInequality };
                if (node is MemberExpression { Computed: false } access) return Walk(access.Object, observes);
                if (node is Property { Computed: false } property) return Walk(property.Value, observes);
                return node.ChildNodes.Any(child => Walk(child, observes));
            }
        }
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
