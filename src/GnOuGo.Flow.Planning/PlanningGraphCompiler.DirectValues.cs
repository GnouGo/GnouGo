using System.Text;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    // A local rewrite of the existing checked program. Do not infer equivalence
    // for arbitrary JSON Schemas: unrecognized container constraints stay put.
    private static string DirectCheckedValues(string program, JsonObject contracts, bool lexical = false)
    {
        if (new Parser().ParseExpression(program) is not CallExpression { Callee: ArrowFunctionExpression
            { Body: BlockStatement body } } || body.Body.Last() is not ReturnStatement { Argument: not null } returned) return program;
        var locals = new Dictionary<string, (string Source, string? Field)>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var scalarAddresses = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new StringBuilder("(()=>{");
        var order = 0;
        var declarations = body.Body.SkipLast(1).Select(statement => statement is VariableDeclaration { Declarations.Count: 1 } declaration &&
            declaration.Declarations[0] is { Id: Identifier name, Init: Expression value } ? (Name: name.Name, Value: value) :
            throw new InvalidOperationException("Invalid compiler-owned checked declaration.")).ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal);
        var checks = lexical && contracts.All(c => c.Value?["order"] is not null)
            ? contracts.OrderBy(c => c.Value!["order"]!.GetValue<int>()).Select(c => c.Key).ToArray() : declarations.Keys.ToArray();
        var originalSources = contracts.ToDictionary(c => c.Key, c => c.Value?["source"]?.GetValue<string>() ?? c.Key, StringComparer.Ordinal);
        foreach (var name in checks)
        {
            var contract = contracts[name]!.AsObject();
            var objectOnly = !contract.ContainsKey("source") || contract.ContainsKey("object_only");
            var schema = contract["schema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
            if (!declarations.TryGetValue(name, out var value))
            {
                var alias = locals[originalSources[name]];
                if (alias.Field is { } aliasField)
                {
                    contract["schema"] = Unwrap(schema, aliasField)!.DeepClone();
                    contract["instance_path"] = (contract["instance_path"]?.GetValue<string>() ?? "") + Pointer(aliasField);
                    contract.Remove("object_only");
                }
                contract["source"] = alias.Source; contract["order"] = order++;
                locals[name] = alias;
                continue;
            }
            string? field = null;
            Node selected = value;
            if (value is ObjectExpression { Properties.Count: 1 } obj &&
                obj.Properties[0] is Property { Computed: false, Method: false } property &&
                Key(property.Key) is { } propertyName && Unwrap(schema, propertyName) is { } child &&
                !ObservesIdentity(name, propertyName) && AliasChecksPermit(propertyName))
            {
                field = propertyName; selected = property.Value; schema = child;
            }
            else if (value is Identifier alias && locals.TryGetValue(alias.Name, out var original) && original.Field is { } aliasField &&
                Unwrap(schema, aliasField) is { } aliasSchema && !ObservesIdentity(name, aliasField) && AliasChecksPermit(aliasField))
            {
                field = aliasField; schema = aliasSchema;
            }
            var expression = field is not null && value is Identifier previous && locals.TryGetValue(previous.Name, out var prior)
                ? prior.Source : Write(selected, new(StringComparer.Ordinal));
            if (lexical && Scalar(schema) && new Parser().ParseExpression(expression) is MemberExpression address && Address(address))
            {
                if (scalarAddresses.TryGetValue(expression, out var observed)) expression = observed;
                else scalarAddresses.Add(expression, name);
            }
            var source = new Parser().ParseExpression(expression) is Identifier same && declared.Contains(same.Name) ? same.Name : name;
            if (source != name && !Scalar(schema) && (ObservesIdentity(name, null) || ObservesIdentity(source, null)))
                source = name; // Keep the original declaration's isolated value when identity is observable.
            if (source == name)
            {
                result.Append("const ").Append(name).Append(" = ").Append(expression).Append(';');
                declared.Add(name);
            }
            contract["schema"] = schema.DeepClone();
            contract["order"] = order++;
            contract["source"] = source;
            if (field is not null)
            {
                contract["instance_path"] = (lexical ? contract["instance_path"]?.GetValue<string>() ?? "" : "") + Pointer(field);
                contract.Remove("object_only");
            }
            else if (!lexical || objectOnly) contract["object_only"] = true;
            locals[name] = (source, field);

            bool AliasChecksPermit(string candidate) => !lexical || originalSources.Where(c => c.Key != name && c.Value == name)
                .All(c => contracts[c.Key]?["schema"] is JsonObject constraint && Unwrap(constraint, candidate) is not null);
        }
        result.Append("return ").Append(Write(returned.Argument, new(StringComparer.Ordinal))).Append(";})()");
        return SimplifyReadableProgram(result.ToString(), inlineConsumers: true);

        static string Pointer(string field) => "/" + field.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        static bool Address(Node node) => node is Identifier || node is MemberExpression member && Address(member.Object) &&
            (member.Computed ? member.Property is StringLiteral : member.Property is Identifier);

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
            return Walk(body, false, new(StringComparer.Ordinal));

            bool Walk(Node node, bool observes, HashSet<string> shadowed)
            {
                if (lexical && node is ArrowFunctionExpression arrow)
                {
                    shadowed = new(shadowed, StringComparer.Ordinal);
                    foreach (var parameter in arrow.Params.OfType<Identifier>()) shadowed.Add(parameter.Name);
                }
                if (field is not null && node is MemberExpression { Object: Identifier owner } member && aliases.Contains(owner.Name) &&
                    (member.Computed ? (member.Property as StringLiteral)?.Value : Key(member.Property)) == field) return false;
                if (node is Identifier identifier) return observes && aliases.Contains(identifier.Name) && !shadowed.Contains(identifier.Name);
                observes |= node is CallExpression || node is BinaryExpression { Operator:
                    Operator.Equality or Operator.StrictEquality or Operator.Inequality or Operator.StrictInequality };
                if (node is MemberExpression { Computed: false } access) return Walk(access.Object, observes, shadowed);
                if (node is Property { Computed: false } property) return Walk(property.Value, observes, shadowed);
                return node.ChildNodes.Any(child => Walk(child, observes, shadowed));
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

    // v13 creates new checked scopes after the flat v12 pass. Simplify each
    // compiler-owned block only after its contracts have been collected. Nested
    // scopes keep their checks; eliminated declarations keep ordered aliases.
    private static string DirectLexicalValues(string program, JsonObject contracts)
    {
        string Write(Node node)
        {
            if (node is ReturnStatement { Argument: not null } returned) return "return (" + Write(returned.Argument) + ");";
            var text = new StringBuilder(); var position = node.Start;
            foreach (var child in node.ChildNodes)
            {
                if (child.Start < position) continue;
                text.Append(program.AsSpan(position, child.Start - position)).Append(Write(child)); position = child.End;
            }
            var rewritten = text.Append(program.AsSpan(position, node.End - position)).ToString();
            if (node is not CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression
                { Params.Count: 0, Body: BlockStatement body } } || body.Body.LastOrDefault() is not ReturnStatement) return rewritten;
            rewritten = InlineCheckedDeclarations(rewritten, contracts);
            body = (BlockStatement)((ArrowFunctionExpression)((CallExpression)new Parser().ParseExpression(rewritten)).Callee).Body;
            var names = body.Body.OfType<VariableDeclaration>().SelectMany(d => d.Declarations)
                .Select(d => (d.Id as Identifier)?.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (names.Count == 0 || names.Any(n => !contracts.ContainsKey(n))) return rewritten;
            var local = new JsonObject(contracts.Where(c => names.Contains(c.Value?["source"]?.GetValue<string>() ?? c.Key))
                .Select(c => new KeyValuePair<string, JsonNode?>(c.Key, c.Value?.DeepClone())));
            rewritten = DirectCheckedValues(SimplifyReadableProgram(rewritten, true), local, lexical: true);
            foreach (var (name, entry) in local) contracts[name] = entry?.DeepClone();
            return rewritten;
        }
        var result = Write(new Parser().ParseExpression(program));
        if (contracts.Any(c => c.Value?["source"] is null))
            throw new InvalidOperationException("Compiler-owned lexical checks could not be associated with their declarations.");
        QualifyLexicalContracts(result, contracts);
        if (contracts.All(c => c.Value?["scope"]?.GetValue<string>() == "block0") &&
            new Parser().ParseExpression(result) is CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Body: BlockStatement } })
            foreach (var (_, entry) in contracts) entry!.AsObject().Remove("scope");
        return result;
    }

    // Both regions already share one expression/allowance and have no effects.
    // Splice a closed callee's declarations at its sole evaluation point, keeping
    // its checks before the caller-result check. Conditional IIFEs stay lexical.
    private static string InlineCheckedDeclarations(string program, JsonObject contracts)
    {
        var syntax = (CallExpression)new Parser().ParseExpression(program);
        var body = (BlockStatement)((ArrowFunctionExpression)syntax.Callee).Body;
        var declarations = body.Body.OfType<VariableDeclaration>().Select(d => d.Declarations[0])
            .Where(d => d.Id is Identifier && d.Init is not null).ToDictionary(d => ((Identifier)d.Id).Name, StringComparer.Ordinal);
        if (declarations.Keys.Any(n => !contracts.ContainsKey(n))) return program;
        var checks = contracts.Where(c => declarations.ContainsKey(c.Value?["source"]?.GetValue<string>() ?? c.Key))
            .OrderBy(c => c.Value?["order"]?.GetValue<int>() ?? Array.IndexOf(declarations.Keys.ToArray(), c.Key)).ToArray();
        var result = new StringBuilder("(()=>{"); var order = 0; var changed = false;
        foreach (var (name, entry) in checks)
        {
            if (declarations.TryGetValue(name, out var declaration))
            {
                var value = declaration.Init!;
                if (value is CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Body: BlockStatement inner } } &&
                    inner.Body.LastOrDefault() is ReturnStatement { Argument: not null } returned &&
                    inner.Body.SkipLast(1).All(s => s is VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } d &&
                        d.Declarations[0].Id is Identifier id && contracts.ContainsKey(id.Name)))
                {
                    var names = inner.Body.OfType<VariableDeclaration>().Select(d => ((Identifier)d.Declarations[0].Id).Name).ToHashSet(StringComparer.Ordinal);
                    var innerChecks = contracts.Where(c => names.Contains(c.Value?["source"]?.GetValue<string>() ?? c.Key)).OrderBy(c => c.Value!["order"]!.GetValue<int>()).ToArray();
                    foreach (var (_, check) in innerChecks) check!["order"] = order++;
                    foreach (var statement in inner.Body.SkipLast(1)) result.Append(program[statement.Start..statement.End]);
                    result.Append("const ").Append(name).Append(" = (").Append(program[returned.Argument.Start..returned.Argument.End]).Append(");");
                    changed = true;
                }
                else result.Append("const ").Append(name).Append(" = (").Append(program[value.Start..value.End]).Append(");");
            }
            entry!["order"] = order++;
        }
        var final = ((ReturnStatement)body.Body.Last()).Argument!;
        return changed ? result.Append("return (").Append(program[final.Start..final.End]).Append(");})()").ToString() : program;
    }
}
