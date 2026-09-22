using Acornima.Ast;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Checks statically named projections on typed parameters and simple aliases.</summary>
internal static class PlanningComputationContracts
{
    internal static void Validate(string expression, IReadOnlyDictionary<string, JsonObject> parameters)
    {
        var messages = new HashSet<(string Code, string Rule, string Message)>();
        Walk(new Acornima.Parser().ParseExpression(expression), new(parameters, StringComparer.Ordinal), messages);
        if (messages.Count > 0) throw new InvalidOperationException(string.Join("; ", messages.Select(m => m.Message)));
    }
    private static readonly HashSet<string> ArrayMembers = new(StringComparer.Ordinal)
    {
        "length", "at", "concat", "copyWithin", "entries", "every", "fill", "filter", "find", "findIndex", "findLast", "findLastIndex", "flat", "flatMap", "forEach",
        "includes", "indexOf", "join", "keys", "lastIndexOf", "map", "pop", "push", "reduce", "reduceRight", "reverse", "shift", "slice", "some", "sort", "splice",
        "toReversed", "toSorted", "toSpliced", "unshift", "values", "with", "constructor", "toString", "hasOwnProperty", "valueOf", "toLocaleString"
    };
    internal static IEnumerable<PlanningDiagnostic> Findings(PlanningGraph graph, PlanningCatalog catalog)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
            var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, catalog);
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                foreach (var diagnostic in Values(node.Input, path + "/input", resolve)) yield return diagnostic;
                if (node.Expr is { } expr) foreach (var diagnostic in Values(expr, path + "/expr", resolve)) yield return diagnostic;
                if (node.If is { } guard) foreach (var diagnostic in Values(guard, path + "/if", resolve)) yield return diagnostic;
                for (var i = 0; i < node.OnError.Count; i++)
                {
                    if (node.OnError[i].If is { } condition) foreach (var diagnostic in Values(condition, path + "/onError/" + i + "/if", resolve)) yield return diagnostic;
                    if (node.OnError[i].SetOutput is { } fallback) foreach (var diagnostic in Values(fallback, path + "/onError/" + i + "/setOutput", resolve)) yield return diagnostic;
                }
                for (var i = 0; i < node.Cases.Count; i++)
                    if (node.Cases[i].When is { } condition) foreach (var diagnostic in Values(condition, path + "/cases/" + i + "/when", resolve)) yield return diagnostic;
            }
            for (var i = 0; i < workflow.Outputs.Count; i++)
                foreach (var diagnostic in Values(workflow.Outputs[i].Value, root + "/outputs/" + i + "/value", resolve)) yield return diagnostic;
        }
    }

    private static IEnumerable<PlanningDiagnostic> Values(PlanningValue value, string location, Func<PlanningValue, JsonObject> resolve)
    {
        if (value.Kind == "compute")
        {
            var parameters = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var member in value.Members)
                try { parameters[member.Name] = resolve(member.Value); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { }
            var messages = new HashSet<(string Code, string Rule, string Message)>();
            try { Walk(new Acornima.Parser().ParseExpression(PlanningComputations.Expression(value.Text)), parameters, messages); }
            catch (Exception ex) when (ex is Acornima.ParseErrorException or InvalidOperationException) { /* Syntax/binding validators report these independently. */ }
            foreach (var message in messages) yield return new(message.Code, location + "/text", message.Message, ValidationStage: "dataflow", Rule: message.Rule);
        }
        for (var i = 0; i < value.Members.Count; i++)
            foreach (var diagnostic in Values(value.Members[i].Value, location + "/members/" + i + "/value", resolve)) yield return diagnostic;
        for (var i = 0; i < value.Items.Count; i++)
            foreach (var diagnostic in Values(value.Items[i], location + "/items/" + i, resolve)) yield return diagnostic;
    }

    private static void Walk(Node node, Dictionary<string, JsonObject> scope, HashSet<(string Code, string Rule, string Message)> messages)
    {
        if (node is BlockStatement) scope = new(scope, StringComparer.Ordinal);
        if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration)
        {
            var parameters = node switch { ArrowFunctionExpression a => a.Params.ToArray(), FunctionExpression f => f.Params.ToArray(), FunctionDeclaration f => f.Params.ToArray(), _ => [] };
            scope = new(scope, StringComparer.Ordinal);
            foreach (var parameter in parameters.OfType<Identifier>()) scope[parameter.Name] = GroundedTypes.Opaque();
        }
        if (node is VariableDeclarator { Id: Identifier identifier } variable)
        {
            if (variable.Init is { } initializer) Walk(initializer, scope, messages);
            if (Schema(variable.Init, scope) is { } contract) scope[identifier.Name] = contract;
            else scope[identifier.Name] = GroundedTypes.Opaque();
            return;
        }
        if (node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: "map" or "filter" or "every" or "some" } } collection, Arguments.Count: 1 } call &&
            call.Arguments[0] is ArrowFunctionExpression callback && Schema(collection.Object, scope) is { } arrayContract && HasType(arrayContract, "array"))
        {
            Walk(collection, scope, messages);
            var local = new Dictionary<string, JsonObject>(scope, StringComparer.Ordinal);
            for (var i = 0; i < callback.Params.Count; i++)
                if (callback.Params[i] is Identifier parameter) local[parameter.Name] = i switch
                { 0 => Element(arrayContract), 1 => new() { ["type"] = "integer" }, 2 => arrayContract, _ => GroundedTypes.Opaque() };
            Walk(callback.Body, local, messages); return;
        }
        if (node is MemberExpression { Computed: true } dynamicMember && Name(dynamicMember) is null && Schema(dynamicMember.Object, scope) is { } dynamicOwner &&
            (Unknown(dynamicOwner) || HasType(dynamicOwner, "object")))
            messages.Add(("COMPUTATION_FIELD_UNDECLARED", MemberIdentity(dynamicMember), "A dynamic projection cannot establish fields of an opaque or closed object; validate the whole value before projection."));
        if (node is MemberExpression member && Name(member) is { } name && Schema(member.Object, scope) is { } schema &&
            (Unknown(schema) || HasType(schema, "object") || schema["properties"] is JsonObject) &&
            !Declares(schema, name) &&
            name is not ("toString" or "hasOwnProperty" or "valueOf" or "toLocaleString"))
            messages.Add(("COMPUTATION_FIELD_UNDECLARED", MemberIdentity(member), "Property '" + name + "' is not declared by this computation parameter's producer contract. Declared fields: " + string.Join(", ", (schema["properties"] as JsonObject ?? []).Select(p => p.Key)) +
                ". Select an established producer binding or obtain the missing runtime observation; trying alternative field names cannot establish that data."));
        if (node is MemberExpression arrayMember && Name(arrayMember) is { } field && Schema(arrayMember.Object, scope) is { } array && HasType(array, "array") &&
            !ArrayMembers.Contains(field) && !uint.TryParse(field, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            messages.Add(("COMPUTATION_COLLECTION_FIELD_INVALID", MemberIdentity(arrayMember), "Property '" + field + "' belongs to no declared array result. A collection cannot supply an individual item's fields. " +
                "Use a declared element binding in the approved loop. If the requested per-item actions require a missing loop, revise the intent plan; do not silently select the first item or discard items."));
        foreach (var child in node.ChildNodes) Walk(child, scope, messages);
    }

    private static string MemberIdentity(Node node) => node switch
    {
        Identifier identifier => identifier.Name,
        MemberExpression member => MemberIdentity(member.Object) + "/" + PlanningFieldPaths.Escape(Name(member) ?? "[]"),
        _ => node.Type.ToString()
    };

    private static JsonObject? Schema(Node? node, Dictionary<string, JsonObject> scope) => node switch
    {
        Identifier identifier => scope.GetValueOrDefault(identifier.Name),
        ObjectExpression obj when obj.Properties.All(p => p is Property { Computed: false }) => GroundedTypes.Object(obj.Properties.Cast<Property>().Select(p =>
            ((p.Key as Identifier)?.Name ?? (p.Key as StringLiteral)?.Value ?? "", Schema(p.Value, scope) ?? GroundedTypes.Opaque()))),
        ArrayExpression array => new() { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = new JsonArray(array.Elements.Select(e => (JsonNode)(Schema(e, scope) ?? GroundedTypes.Opaque()).DeepClone()).ToArray()) } },
        Literal literal => new() { ["type"] = literal.Value switch { null => "null", string => "string", bool => "boolean", _ => "number" } },
        LogicalExpression { Operator: Acornima.Operator.LogicalOr or Acornima.Operator.NullishCoalescing } expression => Schema(expression.Left, scope),
        MemberExpression member when Schema(member.Object, scope) is { } parent && HasType(parent, "array") &&
            (member.Computed && (member.Property is NumericLiteral || Schema(member.Property, scope) is { } indexContract && (HasType(indexContract, "integer") || HasType(indexContract, "number"))) ||
             Name(member) is { } index && uint.TryParse(index, out _)) => Element(parent),
        MemberExpression member when Name(member) is { } name && Schema(member.Object, scope) is { } parent => Property(parent, name),
        null => null,
        _ => ExpressionContractInference.Infer(node, scope) ?? GroundedTypes.Opaque()
    };

    private static IEnumerable<JsonObject> Variants(JsonObject schema) => new[] { "anyOf", "oneOf" }.SelectMany(key => (schema[key] as JsonArray ?? []).OfType<JsonObject>());
    private static bool Unknown(JsonObject schema) => schema.Count == 0 || GroundedTypes.IsOpaque(schema) || Variants(schema).Any(Unknown);
    private static bool Declares(JsonObject schema, string name) => !Unknown(schema) && (schema["properties"] is JsonObject fields && fields.ContainsKey(name) || schema["additionalProperties"] is JsonObject || Variants(schema).Any(v => Declares(v, name)));
    private static JsonObject Property(JsonObject schema, string name)
    {
        if (Unknown(schema)) return GroundedTypes.Opaque();
        if (schema["properties"]?[name] is JsonObject field) return field;
        if (schema["additionalProperties"] is JsonObject additional) return additional;
        var variants = Variants(schema).ToArray();
        return variants.Length == 0 ? GroundedTypes.Opaque() : new() { ["anyOf"] = new JsonArray(variants.Select(v => (JsonNode)(Declares(v, name) ? Property(v, name).DeepClone() : new JsonObject { ["type"] = "null" })).ToArray()) };
    }
    private static bool HasType(JsonObject schema, string type) => schema["type"]?.ToString() == type || schema["type"] is JsonArray types && types.Any(t => t?.ToString() == type) || Variants(schema).Any(v => HasType(v, type));
    private static JsonObject Element(JsonObject schema)
    {
        if (schema["items"] is JsonObject items) return items;
        var variants = Variants(schema).Where(v => HasType(v, "array")).Select(Element).ToArray();
        if (variants.Length == 0 || variants.Any(Unknown)) return GroundedTypes.Opaque();
        return variants.Length == 1 ? variants[0] : new() { ["anyOf"] = new JsonArray(variants.Select(v => (JsonNode)v.DeepClone()).ToArray()) };
    }

    private static string? Name(MemberExpression member) => member.Computed
        ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name;
}
