using System.Text.Json.Nodes;
using Acornima.Ast;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>Conservative finite-result proof. No code is executed and no context is assumed.</summary>
public static class ClosedExpressionValues
{
    public static bool AreWithin(string interpolation, IReadOnlyList<JsonNode> allowed)
    {
        try
        {
            var segments = ExpressionSegments.Read(interpolation);
            if (segments.Count != 1 || segments[0].Start != 0 || segments[0].Length != interpolation.Length) return false;
            var values = Results(new Acornima.Parser().ParseExpression(segments[0].Expression));
            return values is { Count: > 0 } && values.All(value => allowed.Any(item => JsonNode.DeepEquals(item, value)));
        }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or ExpressionParseException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static List<JsonNode?>? Results(Node expression)
    {
        if (expression is Literal literal)
            return literal.Value switch { null => [null], string text => [JsonValue.Create(text)], bool boolean => [JsonValue.Create(boolean)], double number when double.IsFinite(number) => [JsonValue.Create(number)], _ => null };
        if (expression is ConditionalExpression conditional)
            return Combine(Results(conditional.Consequent), Results(conditional.Alternate));
        if (expression is CallExpression { Callee: ArrowFunctionExpression { Async: false } arrow })
        {
            if (arrow.Body is not BlockStatement body) return Results(arrow.Body);
            // A final return rules out fall-through. Every other return in this function
            // must also be closed; nested functions do not return from this invocation.
            if (body.Body.LastOrDefault() is not ReturnStatement) return null;
            List<JsonNode?>? values = [];
            foreach (var statement in Returns(body)) values = Combine(values, statement.Argument is null ? [null] : Results(statement.Argument));
            return values;
        }
        return null;
    }

    private static List<JsonNode?>? Combine(List<JsonNode?>? first, List<JsonNode?>? second) => first is null || second is null ? null : first.Concat(second).ToList();

    private static IEnumerable<ReturnStatement> Returns(Node node)
    {
        if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration) yield break;
        if (node is ReturnStatement statement) { yield return statement; yield break; }
        foreach (var child in node.ChildNodes) foreach (var returned in Returns(child)) yield return returned;
    }
}
