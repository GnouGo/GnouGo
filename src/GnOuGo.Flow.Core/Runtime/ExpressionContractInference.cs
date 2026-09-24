using Acornima;
using Acornima.Ast;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Infers pure business expressions from declared arguments, without executing examples.</summary>
public static class ExpressionContractInference
{
    public static JsonObject? Infer(string expression, IReadOnlyDictionary<string, JsonObject> arguments)
        => Infer(new Parser().ParseExpression(expression), arguments);

    public static JsonObject? Infer(Node expression, IReadOnlyDictionary<string, JsonObject> arguments)
    {
        var variables = arguments.ToDictionary(p => p.Key, p => FlowTypeDescriptorConverter.FromJsonSchema(p.Value), StringComparer.Ordinal);
        foreach (var name in ComputationInferenceProfile.UntrustedGlobals(expression)) variables[name] = FlowTypeDescriptor.Any;
        var result = Infer(expression, variables);
        return result.IsOpaque ? null : FlowTypeDescriptorConverter.ToRuntimeJsonSchema(result);
    }
    private static FlowTypeDescriptor Infer(Node node, IReadOnlyDictionary<string, FlowTypeDescriptor> variables)
    {
        FlowTypeDescriptor Type(Node child) => Infer(child, variables);
        switch (node)
        {
            case CallExpression { Callee: ArrowFunctionExpression { Params.Count: 0 } function, Arguments.Count: 0 }:
                return Type(function.Body);
            case ReturnStatement statement: return statement.Argument is null ? FlowTypeDescriptor.Null : Type(statement.Argument);
            case BlockStatement block:
                var locals = variables.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var returns = new List<FlowTypeDescriptor>();
                foreach (var statement in block.Body)
                {
                    switch (statement)
                    {
                        case VariableDeclaration declaration:
                            foreach (var variable in declaration.Declarations)
                            {
                                if (variable.Id is not Identifier id || variable.Init is null) return FlowTypeDescriptor.Any;
                                locals[id.Name] = Infer(variable.Init, locals);
                            }
                            break;
                        case ReturnStatement returned:
                            returns.Add(Infer(returned, locals)); return FlowTypeDescriptor.Union(returns);
                        case IfStatement conditional:
                            returns.Add(Infer(conditional.Consequent, locals));
                            if (conditional.Alternate is not null) returns.Add(Infer(conditional.Alternate, locals));
                            break;
                        default: return FlowTypeDescriptor.Any;
                    }
                }
                return FlowTypeDescriptor.Any; // Fall-through does not establish a result contract.
            case Identifier identifier: return variables.GetValueOrDefault(identifier.Name) ?? FlowTypeDescriptor.Any;
            case RegExpLiteral or BigIntLiteral: return FlowTypeDescriptor.Any;
            case Literal literal: return literal.Value switch { null => FlowTypeDescriptor.Null, string text => FlowTypeDescriptor.Enum(text), bool => FlowTypeDescriptor.Boolean, _ => FlowTypeDescriptor.Number };
            case TemplateLiteral: return FlowTypeDescriptor.String;
            case ArrayExpression array: return FlowTypeDescriptor.Array(FlowTypeDescriptor.Union(array.Elements.Where(e => e is not null).Select(e => Type(e!))));
            case ObjectExpression obj:
                var fields = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
                foreach (var property in obj.Properties)
                {
                    if (property is not Property { Computed: false } field || Name(field.Key) is not { } name) return FlowTypeDescriptor.Any;
                    fields[name] = new(Type(field.Value), true);
                }
                return FlowTypeDescriptor.Object(fields);
            case MemberExpression member:
                var owner = Type(member.Object).RemoveNull(); var key = member.Computed ? (member.Property as Literal)?.Value?.ToString() : Name(member.Property);
                if (key == "length" && owner.Kind is FlowTypeKind.Array or FlowTypeKind.String) return FlowTypeDescriptor.Integer;
                if (owner.Kind == FlowTypeKind.Array && member.Computed && (key is not null && uint.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) || key is null && Numeric(Type(member.Property))))
                    return FlowTypeDescriptor.Union([owner.Items!, FlowTypeDescriptor.Null]);
                return key is null ? FlowTypeDescriptor.Any : owner.ResolvePath([key]) ?? FlowTypeDescriptor.Any;
            case ConditionalExpression conditional: return FlowTypeDescriptor.Union([Type(conditional.Consequent), Type(conditional.Alternate)]);
            case LogicalExpression logical:
                var left = Type(logical.Left); var right = Type(logical.Right);
                return logical.Operator == Operator.NullishCoalescing && left.Kind == FlowTypeKind.Null ? right
                    : FlowTypeDescriptor.Union([logical.Operator == Operator.NullishCoalescing ? left.RemoveNull() : left, right]);
            case BinaryExpression binary:
                var a = Type(binary.Left); var b = Type(binary.Right);
                if (binary.Operator is Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual) return FlowTypeDescriptor.Boolean;
                if (binary.Operator == Operator.Addition && (a.Kind == FlowTypeKind.String || b.Kind == FlowTypeKind.String)) return FlowTypeDescriptor.String;
                return Numeric(a) && Numeric(b) ? FlowTypeDescriptor.Number : FlowTypeDescriptor.Any;
            case UnaryExpression unary:
                return unary.Operator switch
                {
                    Operator.LogicalNot => FlowTypeDescriptor.Boolean,
                    Operator.TypeOf => FlowTypeDescriptor.String,
                    Operator.UnaryPlus or Operator.UnaryNegation or Operator.BitwiseNot when Numeric(Type(unary.Argument)) => FlowTypeDescriptor.Number,
                    _ => FlowTypeDescriptor.Any
                };
            case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "map" } } member, Arguments.Count: 1 } call:
                var collection = Type(member.Object);
                if (collection.Kind != FlowTypeKind.Array || call.Arguments[0] is not ArrowFunctionExpression arrow || arrow.Params.Any(p => p is not Identifier)) return FlowTypeDescriptor.Any;
                var scope = variables.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                for (var i = 0; i < arrow.Params.Count; i++) scope[((Identifier)arrow.Params[i]).Name] = i switch
                { 0 => collection.Items!, 1 => FlowTypeDescriptor.Integer, 2 => collection, _ => FlowTypeDescriptor.Any };
                return FlowTypeDescriptor.Array(Infer(arrow.Body, scope));
            case CallExpression { Callee: MemberExpression { Computed: false, Object: Identifier { Name: "JSON" }, Property: Identifier { Name: "stringify" } }, Arguments.Count: 1 } json when !variables.ContainsKey("JSON"):
                // Serialization of a declared JSON container/scalar has a string result. Parsing never creates a field contract.
                return Type(json.Arguments[0]).IsOpaque ? FlowTypeDescriptor.Any : FlowTypeDescriptor.String;
            case CallExpression { Callee: Identifier intrinsic, Arguments.Count: 1 } conversion
                when ComputationInferenceProfile.IsScalarConversion(intrinsic.Name) && !variables.ContainsKey(intrinsic.Name):
                // ECMAScript scalar ToString coercion establishes only the successful return type.
                // Opaque values/containers cannot gain a business contract through coercion.
                return Scalar(Type(conversion.Arguments[0])) ? FlowTypeDescriptor.String : FlowTypeDescriptor.Any;
            case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "match" } } receiver, Arguments.Count: 1 } match
                when match.Arguments[0] is RegExpLiteral && Type(receiver.Object).Kind == FlowTypeKind.String:
                // A literal regex yields strings or null for no match. Optional captures
                // and missing indexes retain the existing null representation for absence.
                return FlowTypeDescriptor.Union([FlowTypeDescriptor.Array(FlowTypeDescriptor.Union([FlowTypeDescriptor.String, FlowTypeDescriptor.Null])), FlowTypeDescriptor.Null]);
            case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier method } receiver }:
                // A null receiver throws rather than producing a successful method result.
                // Optional chaining has its own AST node and remains uninferred here.
                var target = Type(receiver.Object).RemoveNull();
                if (target.Kind == FlowTypeKind.String)
                    return method.Name switch
                    {
                        "trim" or "trimStart" or "trimEnd" or "toLowerCase" or "toUpperCase" or "slice" or "substring" or "replace" or "replaceAll" or "concat" => FlowTypeDescriptor.String,
                        "split" => FlowTypeDescriptor.Array(FlowTypeDescriptor.String),
                        "includes" or "startsWith" or "endsWith" => FlowTypeDescriptor.Boolean,
                        _ => FlowTypeDescriptor.Any
                    };
                if (target.Kind == FlowTypeKind.Array)
                    return method.Name switch
                    {
                        "filter" or "slice" or "toReversed" or "toSorted" => target,
                        "every" or "some" or "includes" => FlowTypeDescriptor.Boolean,
                        "join" => FlowTypeDescriptor.String,
                        _ => FlowTypeDescriptor.Any
                    };
                return FlowTypeDescriptor.Any;
            default: return FlowTypeDescriptor.Any;
        }
    }
    private static string? Name(Node node) => node switch { Identifier identifier => identifier.Name, Literal { Value: string text } => text, _ => null };
    private static bool Numeric(FlowTypeDescriptor type) => type.Kind is FlowTypeKind.Number or FlowTypeKind.Integer;
    private static bool Scalar(FlowTypeDescriptor type) => type.Kind is FlowTypeKind.Null or FlowTypeKind.String or FlowTypeKind.Number or FlowTypeKind.Integer or FlowTypeKind.Boolean ||
        type.Kind == FlowTypeKind.Union && type.Variants.Count > 0 && type.Variants.All(Scalar);
}
