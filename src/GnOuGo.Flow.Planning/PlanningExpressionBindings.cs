using System.Text.Json;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Rebind executable references without changing quoted text or comments.</summary>
internal static class PlanningExpressionBindings
{
    internal static string Expression(string expression, IReadOnlyDictionary<string, string> nodeIds)
    {
        var replacements = new List<(int Start, int End, string Text)>();
        Visit(new Acornima.Parser().ParseExpression(expression), false);
        foreach (var edit in replacements.OrderByDescending(e => e.Start)) expression = expression[..edit.Start] + edit.Text + expression[edit.End..];
        return expression;

        void Visit(Node node, bool boundData)
        {
            boundData |= node switch
            {
                ArrowFunctionExpression arrow => arrow.Params.OfType<Identifier>().Any(p => p.Name == "data"),
                FunctionExpression function => function.Params.OfType<Identifier>().Any(p => p.Name == "data"), _ => false
            };
            if (!boundData && node is MemberExpression { Object: MemberExpression { Object: Identifier { Name: "data" } } root } member && Name(root) == "steps")
            {
                var name = Name(member) ?? throw new InvalidOperationException("Dynamic producer names require explicit typed references.");
                var target = nodeIds.TryGetValue(name, out var id) ? id : nodeIds.Values.Contains(name, StringComparer.Ordinal) ? name
                    : throw new InvalidOperationException("An expression references an unknown producer: " + name);
                replacements.Add((member.Property.Start, member.Property.End, member.Computed ? JsonSerializer.Serialize(target, PlanningJsonContext.Default.String) : target));
            }
            foreach (var child in node.ChildNodes) Visit(child, boundData);
        }
    }

    internal static string Template(string template, IReadOnlyDictionary<string, string> nodeIds)
    {
        foreach (var segment in ExpressionSegments.Read(template).Reverse())
        {
            var replacement = "${" + Expression(segment.Expression, nodeIds) + "}";
            template = template[..segment.Start] + replacement + template[(segment.Start + segment.Length)..];
        }
        return template;
    }

    private static string? Name(MemberExpression member) => member.Property switch
    {
        Identifier id when !member.Computed => id.Name,
        StringLiteral literal when member.Computed => literal.Value,
        _ => null
    };
}
