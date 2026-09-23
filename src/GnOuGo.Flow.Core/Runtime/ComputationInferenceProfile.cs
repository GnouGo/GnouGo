using Acornima.Ast;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>The statically modeled subset of executable ECMAScript, not a runtime allowlist.</summary>
public static class ComputationInferenceProfile
{
    private static readonly string[] ScalarConversions = ["String", "encodeURI", "decodeURI", "encodeURIComponent", "decodeURIComponent"];

    public static string Guidance => "Inferred conversions: " + string.Join(", ", ScalarConversions) +
        ": direct unshadowed/unreassigned calls, one known JSON scalar (nullable unions allowed), return string on success. " +
        "Coercion proves no presence/business validity; URI calls may throw. Literal-regex match has nullable string captures; replace/chains preserve strings. " +
        "Executable JS exceeds inference: numeric conversions/unknown calls/unsupported control flow need whole-result validation before projection, not field renaming.";

    internal static bool IsScalarConversion(string name) => ScalarConversions.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Conservatively excludes names bound or written anywhere in an expression. This includes
    /// hoisted/TDZ declarations and nested bindings; absence of a name in a sequential type map
    /// alone is not proof of the intrinsic's identity. Member writes may target a global alias.
    /// </summary>
    public static IReadOnlySet<string> UntrustedGlobals(Node expression)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Binding(Node pattern)
        {
            if (pattern is Identifier identifier && (IsScalarConversion(identifier.Name) || identifier.Name == "JSON")) names.Add(identifier.Name);
            foreach (var child in pattern.ChildNodes) Binding(child);
        }
        void Visit(Node node)
        {
            switch (node)
            {
                case VariableDeclarator variable: Binding(variable.Id); break;
                case FunctionDeclaration function:
                    if (function.Id is { } id) Binding(id);
                    foreach (var parameter in function.Params) Binding(parameter);
                    break;
                case FunctionExpression function:
                    if (function.Id is { } expressionId) Binding(expressionId);
                    foreach (var parameter in function.Params) Binding(parameter);
                    break;
                case ArrowFunctionExpression function:
                    foreach (var parameter in function.Params) Binding(parameter);
                    break;
                case AssignmentExpression assignment: Write(assignment.Left); break;
                case UpdateExpression update: Write(update.Argument); break;
                case CatchClause { Param: { } parameter }: Binding(parameter); break;
            }
            foreach (var child in node.ChildNodes) Visit(child);
        }
        void Write(Node target)
        {
            Binding(target);
            if (target is MemberExpression)
            {
                names.UnionWith(ScalarConversions);
                names.Add("JSON");
            }
            foreach (var child in target.ChildNodes) Write(child);
        }
        Visit(expression);
        return names;
    }
}
