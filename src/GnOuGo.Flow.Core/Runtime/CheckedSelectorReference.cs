using System.Text.Json.Nodes;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Finite-value proof for a direct reference to a runtime-checked producer.</summary>
internal static class CheckedSelectorReference
{
    internal static bool IsWithin(string interpolation, IReadOnlyList<JsonNode> allowed, WorkflowSymbolTable symbols)
    {
        try
        {
            var segments = ExpressionSegments.Read(interpolation);
            if (segments.Count != 1 || segments[0].Start != 0 || segments[0].Length != interpolation.Length) return false;
            Node expression = new Acornima.Parser().ParseExpression(segments[0].Expression);
            var path = new List<string>();
            while (expression is MemberExpression member)
            {
                var name = member.Property switch { Identifier id when !member.Computed => id.Name, StringLiteral literal when member.Computed => literal.Value, _ => null };
                if (name is null) return false;
                path.Insert(0, name); expression = member.Object;
            }
            if (expression is not Identifier { Name: "data" } || path.Count < 3 || path[0] != "steps" || !symbols.TryGetCheckedStepOutput(path[1], out var descriptor)) return false;
            return Proven(descriptor, path.Skip(2).ToArray());
        }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or ExpressionParseException or InvalidOperationException or ArgumentException) { return false; }

        bool Proven(FlowTypeDescriptor type, IReadOnlyList<string> path)
        {
            if (type.Kind == FlowTypeKind.Union) return type.Variants.Count > 0 && type.Variants.All(v => Proven(v, path));
            if (path.Count > 0) return type.Kind == FlowTypeKind.Object && type.Properties.TryGetValue(path[0], out var property) && property.Required && Proven(property.Type, path.Skip(1).ToArray());
            return type.Kind == FlowTypeKind.String && type.EnumValues.Count > 0 && type.EnumValues.All(v => allowed.Any(a => JsonNode.DeepEquals(a, JsonValue.Create(v))));
        }
    }
}
