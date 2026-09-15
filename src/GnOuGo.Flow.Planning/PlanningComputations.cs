using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningComputations
{
    // An over-approximation of possible switch labels, using JavaScript syntax only.
    // Unknown computations stay unknown; no example execution proves their type.
    internal static IReadOnlyList<string>? FiniteOutcomes(string? text)
    {
        try { return Values(new Acornima.Parser().ParseExpression(Expression(text)), 0); }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or InvalidOperationException) { return null; }
        static IReadOnlyList<string>? Values(Node? node, int depth)
        {
            if (depth > 64) return null;
            return node switch
            {
                Literal { Value: string value } => [value],
                Literal { Value: bool value } => [value ? "true" : "false"],
                UnaryExpression { Operator: Acornima.Operator.LogicalNot } => ["true", "false"],
                BinaryExpression
                {
                    Operator: Acornima.Operator.Equality or Acornima.Operator.Inequality or Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or
                    Acornima.Operator.LessThan or Acornima.Operator.LessThanOrEqual or Acornima.Operator.GreaterThan or Acornima.Operator.GreaterThanOrEqual or Acornima.Operator.In or Acornima.Operator.InstanceOf
                } => ["true", "false"],
                ConditionalExpression conditional => Union([conditional.Consequent, conditional.Alternate], depth),
                CallExpression { Callee: ArrowFunctionExpression { Async: false } arrow } => Returns(arrow.Body, depth),
                CallExpression { Callee: FunctionExpression { Async: false, Generator: false } function } => Returns(function.Body, depth),
                _ => null
            };
        }
        static IReadOnlyList<string>? Union(IEnumerable<Node?> nodes, int depth)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (Values(node, depth + 1) is not { } values) return null;
                result.UnionWith(values);
            }
            return result.ToArray();
        }
        static IReadOnlyList<string>? Returns(Node body, int depth)
        {
            if (body is not BlockStatement) return Values(body, depth + 1);
            IEnumerable<Node?> Results(Node node)
            {
                if (node is ReturnStatement result) { yield return result.Argument; yield break; }
                if (node is FunctionDeclaration or FunctionExpression or ArrowFunctionExpression) yield break;
                foreach (var child in node.ChildNodes) foreach (var value in Results(child)) yield return value;
            }
            return Union(Results(body), depth + 1);
        }
    }

    // Reject provably invalid result branches; unknown dynamic types remain subject
    // to the strict runtime boolean guard. Nested helper return types are unrelated.
    internal static bool HasNonBooleanResult(string? text)
    {
        try { return InvalidResult(new Acornima.Parser().ParseExpression(Expression(text))); }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or InvalidOperationException) { return false; }
        static bool InvalidResult(Node? node) => node switch
        {
            Literal literal => literal.Value is not bool,
            Identifier { Name: "undefined" } => true,
            ConditionalExpression conditional => InvalidResult(conditional.Consequent) || InvalidResult(conditional.Alternate),
            CallExpression { Callee: ArrowFunctionExpression arrow } => Returns(arrow.Body),
            CallExpression { Callee: FunctionExpression function } => Returns(function.Body),
            ObjectExpression or ArrayExpression or TemplateLiteral => true,
            _ => false
        };
        static bool Returns(Node node)
        {
            if (node is ReturnStatement result) return result.Argument is null || InvalidResult(result.Argument);
            if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration) return false;
            if (node is not BlockStatement && node is not Statement) return InvalidResult(node);
            return node.ChildNodes.Any(Returns);
        }
    }

    // An explicit null result branch cannot satisfy a non-nullable destination.
    // Inspect the returned expression, never unrelated nested callback bodies.
    internal static bool HasNullResult(string? text)
    {
        try { return NullResult(new Acornima.Parser().ParseExpression(Expression(text))); }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or InvalidOperationException) { return false; }
        static bool NullResult(Node? node) => node switch
        {
            Literal { Value: null } => true,
            ConditionalExpression conditional => NullResult(conditional.Consequent) || NullResult(conditional.Alternate),
            CallExpression { Callee: ArrowFunctionExpression { Async: false } arrow } => Returns(arrow.Body),
            CallExpression { Callee: FunctionExpression { Async: false, Generator: false } function } => Returns(function.Body),
            _ => false
        };
        static bool Returns(Node node)
        {
            if (node is ReturnStatement result) return NullResult(result.Argument);
            if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration) return false;
            if (node is not BlockStatement && node is not Statement) return NullResult(node);
            return node.ChildNodes.Any(Returns);
        }
    }

    internal static void Validate(PlanningValue value)
    {
        var names = value.Members.Select(m => m.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !ValidName(n)))
            throw new InvalidOperationException("Computation parameters must have unique JavaScript identifiers.");
        if (string.IsNullOrWhiteSpace(value.Text)) throw new InvalidOperationException("A computation needs an executable expression.");
        var expression = new Acornima.Parser().ParseExpression(Expression(value.Text));
        PlanningComputationScopes.Validate(expression, names);
    }

    internal static void ValidateHelpers(string functions) => Validate(new PlanningValue { Kind = "compute", Text = "(() => {\n" + functions + "\nreturn null;\n})()" });

    internal static string Expression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Supply executable JavaScript, not a description of a computation.");
        try { new Acornima.Parser().ParseExpression(text); return text; }
        catch (Acornima.ParseErrorException)
        {
            // A parsed function body has an unambiguous return value. Normalize its boundary,
            // without guessing code or replacing failed syntax with a successful constant.
            var wrapped = "(() => {\n" + text + "\n})()";
            try
            {
                var parsed = new Acornima.Parser().ParseExpression(wrapped);
                var arrow = Descendants(parsed).OfType<ArrowFunctionExpression>().First();
                if (arrow.Body is not BlockStatement block || block.Body.LastOrDefault() is not ReturnStatement)
                    throw new InvalidOperationException("A computation body must end with an explicit return statement.");
                return wrapped;
            }
            catch (Acornima.ParseErrorException ex) { throw new InvalidOperationException("Supply a JavaScript expression or a function body ending with return; prose and pseudocode cannot execute. " + ex.Message, ex); }
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node; foreach (var child in node.ChildNodes) foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static bool ValidName(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
}
