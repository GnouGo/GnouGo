using Acornima.Ast;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolve JavaScript names lexically before attributing use to typed inputs.</summary>
internal static class PlanningComputationScopes
{
    private sealed class Scope(Scope? parent, bool function = false)
    {
        internal readonly Scope? Parent = parent;
        internal readonly bool Function = function;
        internal readonly Dictionary<string, bool> Bindings = new(StringComparer.Ordinal);
        internal bool? Resolve(string name) => Bindings.TryGetValue(name, out var typed) ? typed : Parent?.Resolve(name);
    }

    internal static void Validate(Node expression, IReadOnlyList<string> names)
    {
        var used = Used(expression, names);
        foreach (var name in names.Where(name => !used.Contains(name)))
            throw new InvalidOperationException("Computation parameter '" + name + "' is unused. A declared binding must participate in the computation; it cannot disguise a hard-coded result.");
    }

    internal static HashSet<string> Used(Node expression, IReadOnlyList<string> names)
    {
        var root = new Scope(null, true);
        foreach (var name in new[] { "JSON", "Math", "Object", "Array", "String", "Number", "Boolean", "RegExp", "Set", "Map", "URL", "Error", "TypeError", "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURI", "decodeURI", "encodeURIComponent", "decodeURIComponent", "undefined", "NaN", "Infinity" }) root.Bindings[name] = false;
        foreach (var name in names) root.Bindings[name] = true;
        var scopes = new Dictionary<Node, Scope>();
        var declarations = new HashSet<Node>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        Build(expression, root, null);
        Check(expression, null, true);
        return used;

        void Build(Node node, Scope scope, Node? parent)
        {
            if (node is FunctionDeclaration { Id: { } declared }) Declare(declared, scope);
            if (node is ArrowFunctionExpression or FunctionExpression or FunctionDeclaration)
            {
                scope = new(scope, true);
                if (node is FunctionExpression { Id: { } local }) Declare(local, scope);
                var parameters = node switch { ArrowFunctionExpression a => a.Params.ToArray(), FunctionExpression f => f.Params.ToArray(), FunctionDeclaration f => f.Params.ToArray(), _ => [] };
                foreach (var parameter in parameters) Declare(parameter, scope);
            }
            else if (node is BlockStatement or CatchClause or ForStatement or ForInStatement or ForOfStatement or SwitchStatement)
                scope = new(scope);
            scopes[node] = scope;
            if (node is CatchClause { Param: { } caught }) Declare(caught, scope);
            if (node is VariableDeclarator variable)
            {
                var target = scope;
                if (parent is VariableDeclaration { Kind: VariableDeclarationKind.Var })
                    while (!target.Function && target.Parent is not null) target = target.Parent;
                Declare(variable.Id, target);
            }
            foreach (var child in node.ChildNodes) Build(child, scope, node);
        }

        void Declare(Node pattern, Scope scope)
        {
            switch (pattern)
            {
                case Identifier identifier:
                    scope.Bindings[identifier.Name] = false; declarations.Add(identifier); break;
                case AssignmentPattern assignment: Declare(assignment.Left, scope); break;
                case RestElement rest: Declare(rest.Argument, scope); break;
                case ArrayPattern array:
                    foreach (var element in array.Elements) if (element is not null) Declare(element, scope);
                    break;
                case ObjectPattern obj:
                    foreach (var property in obj.Properties) Declare(property is Property field ? field.Value : property, scope);
                    break;
            }
        }

        void Check(Node node, Node? parent, bool contributes)
        {
            if (node is UnaryExpression { Operator: Acornima.Operator.Void } || node is Identifier && parent is ExpressionStatement) contributes = false;
            if (node is Identifier identifier && !declarations.Contains(node) &&
                !(parent is MemberExpression member && ReferenceEquals(member.Property, node) && !member.Computed) &&
                !(parent is Property property && ReferenceEquals(property.Key, node) && !property.Computed && !property.Shorthand) &&
                !(parent is LabeledStatement or BreakStatement or ContinueStatement))
            {
                var binding = scopes[node].Resolve(identifier.Name);
                if (binding is null && !identifier.Name.StartsWith("u_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Undeclared computation dependency '" + identifier.Name + "'. Pass a typed binding as a named parameter.");
                if (contributes && binding == true) used.Add(identifier.Name);
            }
            foreach (var child in node.ChildNodes) Check(child, node, contributes);
        }
    }
}
