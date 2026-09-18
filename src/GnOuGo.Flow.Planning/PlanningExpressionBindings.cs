using System.Text.Json;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Rebind executable references without changing quoted text or comments.</summary>
internal static class PlanningExpressionBindings
{
    internal static string Expression(string expression, IReadOnlyDictionary<string, string> nodeIds, IReadOnlyDictionary<string, string>? nodeTypes = null)
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
                FunctionExpression function => function.Params.OfType<Identifier>().Any(p => p.Name == "data"),
                _ => false
            };
            if (!boundData && node is MemberExpression { Object: MemberExpression { Object: Identifier { Name: "data" } } root } member && Name(root) == "steps")
            {
                var name = Name(member) ?? throw new InvalidOperationException("Dynamic producer names require explicit typed references.");
                var target = nodeIds.TryGetValue(name, out var id) ? id : nodeIds.Values.Contains(name, StringComparer.Ordinal) ? name
                    : throw new InvalidOperationException("An expression references an unknown producer: " + name);
                replacements.Add((member.Property.Start, member.Property.End, member.Computed ? JsonSerializer.Serialize(target, PlanningJsonContext.Default.String) : target));
            }
            else if (!boundData && node is MemberExpression nested && Container(nested.Object) is not null && Name(nested) is { } child && LogicalKey(child) is { } logical)
            {
                var target = nodeIds[logical];
                replacements.Add((nested.Property.Start, nested.Property.End, nested.Computed ? JsonSerializer.Serialize(target, PlanningJsonContext.Default.String) : target));
            }
            foreach (var child in node.ChildNodes) Visit(child, boundData);
        }

        string? Container(Node node)
        {
            if (Producer(node) is { } direct && nodeTypes?.GetValueOrDefault(direct) is "sequence" or "switch") return direct;
            if (node is MemberExpression { Object: MemberExpression collection } && Producer(collection.Object) is { } parent)
            {
                var type = nodeTypes?.GetValueOrDefault(parent);
                if (Name(collection) == "branches" && type == "parallel" || Name(collection) == "results" && type is "loop.sequential" or "loop.parallel") return parent;
            }
            return null;
        }
        string? LogicalKey(string name) => nodeIds.ContainsKey(name) ? name : nodeIds.FirstOrDefault(pair => pair.Value == name).Key;
        string? Producer(Node node)
        {
            if (node is not MemberExpression member || Name(member) is not { } name) return null;
            if (member.Object is MemberExpression { Object: Identifier { Name: "data" } } root && Name(root) == "steps") return LogicalKey(name);
            return Container(member.Object) is not null ? LogicalKey(name) : null;
        }
    }

    internal static string Template(string template, IReadOnlyDictionary<string, string> nodeIds, IReadOnlyDictionary<string, string>? nodeTypes = null)
    {
        foreach (var segment in ExpressionSegments.Read(template).Reverse())
        {
            var replacement = "${" + Expression(segment.Expression, nodeIds, nodeTypes) + "}";
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
