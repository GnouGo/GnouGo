using Acornima.Ast;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>Only an explicitly true runtime confirmation authorizes its effect.</summary>
public static class ConfirmationDecisionExpression
{
    public static string Build(string reference, string effect, string noEffect) =>
        "(" + reference + " === true ? " + JsonValue.Create(effect).ToJsonString() + " : " + JsonValue.Create(noEffect).ToJsonString() + ")";

    public static bool TryRead(string expression, string effect, string noEffect, out string reference)
    {
        reference = "";
        try
        {
            if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}')) expression = expression[2..^1];
            var tree = new Acornima.Parser().ParseExpression(expression);
            if (tree is not ConditionalExpression { Test: BinaryExpression test, Consequent: Literal yes, Alternate: Literal no }
                || test.Operator != Acornima.Operator.StrictEquality || test.Right is not Literal { Value: true }
                || !Equals(yes.Value, effect) || !Equals(no.Value, noEffect)) return false;
            var path = Path(test.Left);
            if (path is null || !path.StartsWith("data.", StringComparison.Ordinal)) return false;
            reference = path; return true;
        }
        catch (Acornima.ParseErrorException) { return false; }
    }

    private static string? Path(Node node) => node switch
    {
        Identifier id => id.Name,
        MemberExpression { Computed: false, Optional: false, Property: Identifier property } member when Path(member.Object) is { } parent => parent + "." + property.Name,
        MemberExpression { Computed: true, Optional: false, Property: Literal { Value: string key } } member when Path(member.Object) is { } parent && !key.Contains('.') => parent + "." + key,
        _ => null
    };
}
