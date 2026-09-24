using Acornima;
using Acornima.Ast;
namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Shared proofs over explicit result-presence conditions; accepts raw or wrapped runtime expressions.</summary>
public static class WorkflowResultAvailability
{
    public static bool GuardHolds(string? expression, IReadOnlySet<string> guaranteed)
        => RequiredResults(expression) is { Count: > 0 } required && required.All(guaranteed.Contains);

    /// <summary>Results whose presence is sufficient to make a pure availability conjunction true.</summary>
    public static IReadOnlySet<string> RequiredResults(string? expression)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (Parse(expression) is not { } root || !Holds(root)) required.Clear();
        return required;
        bool Holds(Node node) => node switch
        {
            LogicalExpression { Operator: Operator.LogicalAnd } and => Holds(and.Left) && Holds(and.Right),
            BinaryExpression { Operator: Operator.Inequality or Operator.StrictInequality } comparison =>
                comparison.Right is NullLiteral && Known(comparison.Left) || comparison.Left is NullLiteral && Known(comparison.Right),
            _ => false
        };
        bool Known(Node node) { if (ResultName(node) is not { } name) return false; required.Add(name); return true; }
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
