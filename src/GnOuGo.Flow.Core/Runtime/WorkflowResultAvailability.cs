using Acornima;
using Acornima.Ast;
namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Recognizes conjunctions of availability checks over results guaranteed on successful execution.</summary>
internal static class WorkflowResultAvailability
{
    internal static bool GuardHolds(string? expression, IReadOnlySet<string> guaranteed)
        => RequiredResults(expression) is { Count: > 0 } required && required.All(guaranteed.Contains);

    internal static IReadOnlySet<string> RequiredResults(string? expression)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (expression is null || !expression.StartsWith("${", StringComparison.Ordinal) || !expression.EndsWith('}')) return required;
        try { if (!Holds(new Parser().ParseExpression(expression[2..^1]))) required.Clear(); }
        catch (ParseErrorException) { required.Clear(); }
        return required;

        bool Holds(Node node) => node switch
        {
            LogicalExpression { Operator: Operator.LogicalAnd } and => Holds(and.Left) && Holds(and.Right),
            BinaryExpression { Operator: Operator.Inequality or Operator.StrictInequality } comparison =>
                comparison.Right is NullLiteral && Known(comparison.Left) || comparison.Left is NullLiteral && Known(comparison.Right),
            _ => false
        };
        bool Known(Node node)
        {
            if (node is not MemberExpression { Object: MemberExpression { Object: Identifier { Name: "data" } } steps } result ||
                Name(steps) != "steps" || Name(result) is not { } id) return false;
            required.Add(id); return true;
        }
        static string? Name(MemberExpression member) => member.Computed ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name;
    }
}
