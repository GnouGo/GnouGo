using Acornima.Ast;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Checks statically named projections on typed parameters and simple aliases.</summary>
internal static class PlanningComputationContracts
{
    internal static IEnumerable<PlanningDiagnostic> Findings(PlanningGraph graph, PlanningPreparation preparation)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
            var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
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
            var messages = new HashSet<string>(StringComparer.Ordinal);
            try { Walk(new Acornima.Parser().ParseExpression(PlanningComputations.Expression(value.Text)), parameters, messages); }
            catch (Exception ex) when (ex is Acornima.ParseErrorException or InvalidOperationException) { /* Syntax/binding validators report these independently. */ }
            foreach (var message in messages) yield return new("COMPUTATION_FIELD_UNDECLARED", location, message, ValidationStage: "dataflow");
        }
        for (var i = 0; i < value.Members.Count; i++)
            foreach (var diagnostic in Values(value.Members[i].Value, location + "/members/" + i + "/value", resolve)) yield return diagnostic;
        for (var i = 0; i < value.Items.Count; i++)
            foreach (var diagnostic in Values(value.Items[i], location + "/items/" + i, resolve)) yield return diagnostic;
    }

    private static void Walk(Node node, Dictionary<string, JsonObject> scope, HashSet<string> messages)
    {
        if (node is BlockStatement) scope = new(scope, StringComparer.Ordinal);
        if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration)
        {
            var parameters = node switch { ArrowFunctionExpression a => a.Params.ToArray(), FunctionExpression f => f.Params.ToArray(), FunctionDeclaration f => f.Params.ToArray(), _ => [] };
            scope = new(scope, StringComparer.Ordinal);
            foreach (var parameter in parameters.OfType<Identifier>()) scope.Remove(parameter.Name);
        }
        if (node is VariableDeclarator { Id: Identifier identifier } variable)
        {
            if (variable.Init is { } initializer) Walk(initializer, scope, messages);
            if (Schema(variable.Init, scope) is { } contract) scope[identifier.Name] = contract;
            else scope.Remove(identifier.Name);
            return;
        }
        if (node is MemberExpression member && Name(member) is { } name && Schema(member.Object, scope) is { } schema &&
            (schema.Count == 0 || schema["type"]?.ToString() == "object" || schema["properties"] is JsonObject) &&
            (schema["properties"] is not JsonObject fields || !fields.ContainsKey(name)) && schema["additionalProperties"] is not JsonObject &&
            name is not ("toString" or "hasOwnProperty" or "valueOf" or "toLocaleString"))
            messages.Add("Property '" + name + "' is not declared by this computation parameter's producer contract. Declared fields: " + string.Join(", ", (schema["properties"] as JsonObject ?? []).Select(p => p.Key)) +
                ". Select an established producer binding or obtain the missing runtime observation; trying alternative field names cannot establish that data.");
        foreach (var child in node.ChildNodes) Walk(child, scope, messages);
    }

    private static JsonObject? Schema(Node? node, Dictionary<string, JsonObject> scope) => node switch
    {
        Identifier identifier => scope.GetValueOrDefault(identifier.Name),
        LogicalExpression { Operator: Acornima.Operator.LogicalOr or Acornima.Operator.NullishCoalescing } expression => Schema(expression.Left, scope),
        MemberExpression member when Name(member) is { } name => Schema(member.Object, scope)?["properties"]?[name] as JsonObject,
        _ => null
    };

    private static string? Name(MemberExpression member) => member.Computed
        ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name;
}
