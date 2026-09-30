using Acornima;
using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private static void GuardCleanup(PlanningNode node)
    {
        var required = And(ReadGuard(node.Input), node.Expr is null ? null : ReadGuard(node.Expr));
        if (required is null) return;
        // Some projections are guarded as they are emitted and again with their owning task.
        var existing = node.If;
        var guard = existing is null ? null : existing.Kind switch
        {
            "present" => Presence(existing.Source!),
            "boolean" => existing.Boolean == true ? "true" : "false",
            "expression" => existing.Text,
            _ => throw new InvalidOperationException("A generated cleanup guard must be a literal or presence expression.")
        };
        node.If = new() { Kind = "expression", Text = And(required, guard) };
    }

    private static string? ReadGuard(PlanningValue value)
    {
        // Presence itself is safe for an absent producer; it must not suppress an explicit else branch.
        if (value.Kind == "present") return null;
        if (value.Kind == "output") return Presence(value.Source!);
        if (value.Kind == "expression")
        {
            var text = value.Text ?? "";
            return ExpressionGuard(new Parser().ParseExpression(text), text);
        }
        return value.Members.Select(m => ReadGuard(m.Value)).Concat(value.Items.Select(ReadGuard)).Aggregate((string?)null, And);
    }

    private static string? ExpressionGuard(Node expression, string text)
    {
        // These checks read the result slot, not its payload, and are safe when the slot is absent.
        if (expression is BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality } comparison &&
            (comparison.Left is NullLiteral && Result(comparison.Right) is not null || comparison.Right is NullLiteral && Result(comparison.Left) is not null)) return null;
        if (Result(expression) is { } source) return Presence(source);
        if (expression is LogicalExpression { Operator: Operator.LogicalAnd or Operator.LogicalOr } logical)
        {
            var left = ExpressionGuard(logical.Left, text); var right = ExpressionGuard(logical.Right, text);
            if (right is null) return left;
            var test = text[logical.Left.Start..logical.Left.End];
            // Require the right operand only when runtime evaluation reaches it.
            return And(left, logical.Operator == Operator.LogicalAnd ? "!(" + test + ") || (" + right + ")" : "(" + test + ") || (" + right + ")");
        }
        return expression.ChildNodes.Select(child => ExpressionGuard(child, text)).Aggregate((string?)null, And);
    }

    private static string? Result(Node node) => node is MemberExpression
    {
        Object: MemberExpression { Object: Identifier { Name: "data" }, Property: Identifier { Name: "steps" }, Computed: false }
    } member ? member.Computed ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name : null;

    private static string Presence(string source) => "data.steps[" + Quote(source) + "] != null";
    private static string? And(string? left, string? right) => left is null ? right : right is null || left == right ? left : "(" + left + ") && (" + right + ")";
}
