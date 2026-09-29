using Acornima;
using Acornima.Ast;
namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Shared proofs over explicit result-presence conditions; accepts raw or wrapped runtime expressions.</summary>
public static class WorkflowResultAvailability
{
    public static bool GuardHolds(string? expression, IReadOnlySet<string> guaranteed)
        => RequiredResults(expression) is { Count: > 0 } required && required.All(guaranteed.Contains);

    /// <summary>Results whose presence is sufficient to make an availability guard true.</summary>
    public static IReadOnlySet<string> RequiredResults(string? expression)
    {
        return Parse(expression) is { } root ? Required(root) ?? new(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? Required(Node node)
        {
            if (node is BooleanLiteral { Value: true }) return new(StringComparer.Ordinal);
            if (node is LogicalExpression logical)
            {
                var left = Required(logical.Left); var right = Required(logical.Right);
                if (logical.Operator == Operator.LogicalAnd)
                { if (left is null || right is null) return null; left.UnionWith(right); return left; }
                // A known-true operand suffices for OR. This is not a proof that the other
                // operand's business predicate is true or that any payload is valid.
                if (logical.Operator == Operator.LogicalOr)
                {
                    // Choosing the right proof must also establish that evaluating the left
                    // operand cannot dereference an absent result before short-circuiting.
                    var reads = EvaluationReads(logical.Left);
                    if (right is not null && reads is not null) right.UnionWith(reads); else right = null;
                    return left is null ? right : right is null || left.Count <= right.Count ? left : right;
                }
                return null;
            }
            if (node is BinaryExpression { Operator: Operator.Inequality or Operator.StrictInequality } comparison)
            {
                var name = comparison.Right is NullLiteral ? ResultName(comparison.Left) : comparison.Left is NullLiteral ? ResultName(comparison.Right) : null;
                if (name is not null) return new([name], StringComparer.Ordinal);
            }
            return null;
        }
        HashSet<string>? EvaluationReads(Node node)
        {
            if (node is BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality } comparison &&
                (comparison.Left is NullLiteral && ResultName(comparison.Right) is not null || comparison.Right is NullLiteral && ResultName(comparison.Left) is not null)) return new(StringComparer.Ordinal);
            if (node is MemberExpression member)
            {
                if (member.Computed && member.Property is not (StringLiteral or NumericLiteral)) return null;
                return ResultName(member.Object) is { } source ? new([source], StringComparer.Ordinal) : EvaluationReads(member.Object);
            }
            if (node is Identifier identifier) return identifier.Name == "data" ? new(StringComparer.Ordinal) : null;
            if (node is not Literal &&
                node is not UnaryExpression { Operator: Operator.LogicalNot } &&
                node is not BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality or
                    Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual or Operator.LogicalAnd or Operator.LogicalOr }) return null;
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in node.ChildNodes)
            { var reads = EvaluationReads(child); if (reads is null) return null; result.UnionWith(reads); }
            return result;
        }
    }

    /// <summary>Whether a true condition necessarily excludes both null and missing values for this result.</summary>
    public static bool ProvesPresence(string? expression, string result)
    {
        return Parse(expression) is { } root && Proves(root);
        bool Proves(Node node) => node switch
        {
            LogicalExpression { Operator: Operator.LogicalAnd } and => Proves(and.Left) || Proves(and.Right),
            LogicalExpression { Operator: Operator.LogicalOr } or => Proves(or.Left) && Proves(or.Right),
            // Strict !== null permits undefined and therefore does not prove presence.
            BinaryExpression { Operator: Operator.Inequality } comparison =>
                comparison.Left is NullLiteral && ResultName(comparison.Right) == result ||
                comparison.Right is NullLiteral && ResultName(comparison.Left) == result,
            _ => false
        };
    }

    private static Node? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}')) expression = expression[2..^1];
        try { return new Parser().ParseExpression(expression); }
        catch (ParseErrorException) { return null; }
    }
    private static string? ResultName(Node node)
        => node is MemberExpression { Object: MemberExpression { Object: Identifier { Name: "data" } } steps } result &&
            MemberName(steps) == "steps" ? MemberName(result) : null;
    private static string? MemberName(MemberExpression member)
        => member.Computed ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name;
}
