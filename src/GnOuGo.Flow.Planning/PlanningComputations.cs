using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningComputations
{
    internal static void Validate(PlanningValue value)
    {
        var names = value.Members.Select(m => m.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !ValidName(n)))
            throw new InvalidOperationException("Computation parameters must have unique JavaScript identifiers.");
        if (string.IsNullOrWhiteSpace(value.Text)) throw new InvalidOperationException("A computation needs an executable expression.");
        var expression = new Acornima.Parser().ParseExpression(Expression(value.Text));
        var used = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        allowed.UnionWith(["JSON", "Math", "Object", "Array", "String", "Number", "Boolean", "RegExp", "Set", "Map", "URL", "Error", "TypeError", "parseInt", "parseFloat", "isNaN", "isFinite", "undefined", "NaN", "Infinity"]);
        Collect(expression);
        Check(expression, null);
        foreach (var name in names.Where(name => !used.Contains(name)))
            throw new InvalidOperationException("Computation parameter '" + name + "' is unused. A declared binding must participate in the computation; it cannot disguise a hard-coded result.");

        void Collect(Node node)
        {
            if (node is VariableDeclarator { Id: Identifier variable }) Declare(variable.Name);
            if (node is FunctionDeclaration declaration)
            {
                if (declaration.Id is { } function) allowed.Add(function.Name);
                foreach (var parameter in declaration.Params.OfType<Identifier>()) Declare(parameter.Name);
            }
            if (node is ArrowFunctionExpression arrow) foreach (var parameter in arrow.Params.OfType<Identifier>()) Declare(parameter.Name);
            if (node is FunctionExpression functionExpression) foreach (var parameter in functionExpression.Params.OfType<Identifier>()) Declare(parameter.Name);
            foreach (var child in node.ChildNodes) Collect(child);
        }
        void Check(Node node, Node? parent)
        {
            if (node is Identifier identifier && !(parent is MemberExpression member && ReferenceEquals(member.Property, node) && !member.Computed) &&
                !(parent is Property property && ReferenceEquals(property.Key, node) && !property.Computed && !property.Shorthand))
            {
                if (!allowed.Contains(identifier.Name) && !identifier.Name.StartsWith("u_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Undeclared computation dependency '" + identifier.Name + "'. Pass a typed binding as a named parameter.");
                if (names.Contains(identifier.Name, StringComparer.Ordinal)) used.Add(identifier.Name);
            }
            foreach (var child in node.ChildNodes) Check(child, node);
        }
        void Declare(string name)
        {
            if (names.Contains(name, StringComparer.Ordinal)) throw new InvalidOperationException("A local declaration cannot shadow typed computation parameter '" + name + "'.");
            allowed.Add(name);
        }
    }

    internal static void ValidateHelpers(string functions) => Validate(new PlanningValue { Kind = "compute", Text = "(() => {\n" + functions + "\nreturn null;\n})()" });

    internal static string Expression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Supply executable JavaScript, not a description of a computation.");
        try { new Acornima.Parser().ParseExpression(text); return text; }
        catch (Acornima.ParseErrorException)
        {
            // A parsed function body has an unambiguous return value. Normalize its boundary,
            // without guessing code or replacing failed syntax with a successful constant.
            var wrapped = "(() => {\n" + text + "\n})()";
            try
            {
                var parsed = new Acornima.Parser().ParseExpression(wrapped);
                var arrow = Descendants(parsed).OfType<ArrowFunctionExpression>().First();
                if (arrow.Body is not BlockStatement block || block.Body.LastOrDefault() is not ReturnStatement)
                    throw new InvalidOperationException("A computation body must end with an explicit return statement.");
                return wrapped;
            }
            catch (Acornima.ParseErrorException ex) { throw new InvalidOperationException("Supply a JavaScript expression or a function body ending with return; prose and pseudocode cannot execute. " + ex.Message, ex); }
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node; foreach (var child in node.ChildNodes) foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static bool ValidName(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
}
