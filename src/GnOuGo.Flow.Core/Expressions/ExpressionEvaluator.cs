using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Acornima.Ast;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Evaluates expressions using the Jint JavaScript engine.
/// Context shape: { inputs: {...}, steps: {...}, env: {...} }
/// </summary>
public sealed partial class ExpressionEvaluator
{
    private const int DefaultMaxStatements = 100_000;
    private const int DefaultTimeoutSeconds = 15;
    private const int DefaultMemoryLimitBytes = 50_000_000;

    private readonly Dictionary<string, Func<JsonNode?[], JsonNode?>> _functions;
    private readonly int _maxStatements;
    private readonly TimeSpan _timeout;
    private readonly int _memoryLimitBytes;

    private static bool Truthy(JsonNode? value) => value is JsonValue scalar ?
        scalar.TryGetValue<bool>(out var boolean) ? boolean : scalar.TryGetValue<string>(out var text) ? text.Length > 0 : GetNumber(scalar) != 0 : value is not null;

    // Only this closed compiler recipe bypasses JS import. Learned programs still
    // use Jint; selection cannot compute values or grant new artifact provenance.
    private bool TrySelectMapping(string script, JsonNode? source, out JsonNode? result, JintSandbox.MappingAllowance? allowance = null,
        Dictionary<string, JsonNode?>? initialBindings = null)
    {
        result = null;
        if (script.Length > 65536) return false;
        var syntax = new Acornima.Parser().ParseExpression(script);
        static string? Key(Property property) => property.Key is Identifier id ? id.Name : (property.Key as StringLiteral)?.Value;
        var readable = initialBindings is not null;
        bool FunctionSupported(ArrowFunctionExpression arrow, HashSet<string> names)
        {
            if (arrow.Async || arrow.Params.Any(p => p is not Identifier) || arrow.Params.Cast<Identifier>().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != arrow.Params.Count) return false;
            var local = new HashSet<string>(names.Concat(arrow.Params.Cast<Identifier>().Select(p => p.Name)), StringComparer.Ordinal);
            if (arrow.Body is not BlockStatement block) return Supported(arrow.Body, local);
            if (block.Body.Count == 0 || block.Body.Last() is not ReturnStatement { Argument: not null } returned) return false;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in block.Body.SkipLast(1))
            {
                if (statement is IfStatement { Alternate: null, Consequent: ThrowStatement { Argument: NewExpression { Callee: Identifier { Name: "Error" }, Arguments.Count: 1 } error } } check &&
                    error.Arguments[0] is StringLiteral && Supported(check.Test, local)) continue;
                if (statement is not VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } declaration ||
                    declaration.Declarations[0] is not { Id: Identifier name, Init: Expression value } || name.Name is "data" or "m" ||
                    !declared.Add(name.Name) || !Supported(value, local)) return false;
                local.Add(name.Name);
            }
            return Supported(returned.Argument, local);
        }
        bool Supported(Node node, HashSet<string> names) => node switch
        {
            Identifier id => names.Contains(id.Name),
            Literal literal => literal.Value is null or string or bool or double,
            MemberExpression member => Supported(member.Object, names) &&
                (member.Computed ? member.Property is StringLiteral || readable && Supported(member.Property, names) : member.Property is Identifier),
            ObjectExpression obj => obj.Properties.All(p => p is Property { Computed: false, Method: false, Kind: PropertyKind.Init } property &&
                Key(property) is not (null or "__proto__" or "constructor" or "prototype") && Supported(property.Value, names)),
            ArrayExpression array => array.Elements.All(v => v is not null && Supported(v, names)),
            CallExpression { Arguments.Count: 3, Callee: MemberExpression { Computed: false,
                Object: Identifier { Name: "m" }, Property: Identifier { Name: "select" } } } call =>
                Supported(call.Arguments[0], names) && call.Arguments[1] is ArrayExpression paths &&
                paths.Elements.All(p => p is ArrayExpression path && path.Elements.All(v => v is StringLiteral)) && call.Arguments[2] is BooleanLiteral,
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Computed: false, Property: Identifier { Name: "map" } } member } call =>
                Supported(member.Object, names) && call.Arguments[0] is ArrowFunctionExpression { Params.Count: 1 or 2, Async: false } arrow &&
                arrow.Params.All(p => p is Identifier) && arrow.Params.Cast<Identifier>().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == arrow.Params.Count &&
                (readable ? FunctionSupported(arrow, names) : Supported(arrow.Body, new HashSet<string>(names.Concat(arrow.Params.Cast<Identifier>().Select(p => p.Name)), StringComparer.Ordinal))),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Computed: false, Property: Identifier { Name: "flatMap" } } member } call when readable =>
                Supported(member.Object, names) && call.Arguments[0] is ArrowFunctionExpression { Params.Count: 1 or 2 } arrow && FunctionSupported(arrow, names),
            CallExpression { Callee: ArrowFunctionExpression arrow } call when readable =>
                arrow.Params.Count == call.Arguments.Count && call.Arguments.All(a => Supported(a, names)) && FunctionSupported(arrow, names),
            CallExpression { Arguments.Count: 3, Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "lookup" }, Computed: false } } call when readable =>
                call.Arguments[2] is StringLiteral && call.Arguments.All(a => Supported(a, names)),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "scalar" }, Computed: false } } call when readable =>
                Supported(call.Arguments[0], names),
            CallExpression { Arguments.Count: 1, Callee: Identifier { Name: "json" } } call when readable => Supported(call.Arguments[0], names),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Object: Identifier { Name: "Number" }, Property: Identifier { Name: "isFinite" }, Computed: false } } call when readable => Supported(call.Arguments[0], names),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Object: Identifier { Name: "JSON" }, Property: Identifier { Name: "stringify" }, Computed: false } } call when readable => Supported(call.Arguments[0], names),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Object: Identifier { Name: "Array" }, Property: Identifier { Name: "isArray" }, Computed: false } } call when readable => Supported(call.Arguments[0], names),
            CallExpression { Arguments.Count: 1, Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "entries" or "fromEntries" }, Computed: false } } call when readable => Supported(call.Arguments[0], names),
            ConditionalExpression conditional when readable => Supported(conditional.Test, names) && Supported(conditional.Consequent, names) && Supported(conditional.Alternate, names),
            UnaryExpression { Operator: Acornima.Operator.LogicalNot or Acornima.Operator.TypeOf or Acornima.Operator.UnaryNegation } unary when readable => Supported(unary.Argument, names),
            BinaryExpression binary when readable && binary.Operator is Acornima.Operator.LogicalAnd or Acornima.Operator.LogicalOr or Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or Acornima.Operator.Equality or Acornima.Operator.Inequality or Acornima.Operator.LessThan or Acornima.Operator.LessThanOrEqual or Acornima.Operator.GreaterThan or Acornima.Operator.GreaterThanOrEqual or Acornima.Operator.Addition or Acornima.Operator.Subtraction or Acornima.Operator.Multiplication or Acornima.Operator.Division or Acornima.Operator.Remainder => Supported(binary.Left, names) && Supported(binary.Right, names),
            _ => false
        };
        if (!Supported(syntax, initialBindings is null ? new(StringComparer.Ordinal) { "source" } : new(initialBindings.Keys, StringComparer.Ordinal))) return false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0; var count = 0;
        void Check()
        {
            allowance?.Check();
            if (++count > Math.Min(_maxStatements, 10000) || watch.Elapsed > TimeSpan.FromMilliseconds(Math.Min(_timeout.TotalMilliseconds, 5000)))
                throw JintSandbox.Unsatisfied("Collection selection exceeded its execution allowance.");
        }
        void Charge(JsonNode? value, int depth = 0)
        {
            var size = 128 + (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? (long)text.Length * 4 : 0);
            bytes += size;
            if (readable && allowance is not null)
            {
                allowance.ImportedBytes += size;
                allowance.NestingDepth = Math.Max(allowance.NestingDepth, depth);
                if (depth > 64) throw allowance.Exhausted("nesting");
                if (bytes > Math.Min(_memoryLimitBytes, 50000000)) throw allowance.Exhausted("materialized_memory", memoryLimit: Math.Min(_memoryLimitBytes, 50000000));
                // Traversal is cumulative work, not another executed syntax
                // node in the nested program's statement window. Keep both
                // the shared allowance and the nested deadline effective.
                allowance.Check();
                if (watch.Elapsed > TimeSpan.FromMilliseconds(Math.Min(_timeout.TotalMilliseconds, 5000)))
                    throw JintSandbox.Unsatisfied("Collection selection exceeded its execution allowance.");
            }
            if (depth > 64 || bytes > Math.Min(_memoryLimitBytes, 50000000)) throw JintSandbox.Unsatisfied("Selected data exceeds its nesting or memory allowance.");
            if (value is JsonArray array) foreach (var item in array) Charge(item, depth + 1);
            if (value is JsonObject obj) foreach (var field in obj) Charge(field.Value, depth + 1);
        }
        JsonNode? Select(JsonNode? item, string[][] selections)
        {
            Check();
            foreach (var path in selections)
            {
                var current = item; var present = true;
                foreach (var part in path)
                {
                    if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var field)) current = field;
                    else if (current is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count && part == index.ToString(System.Globalization.CultureInfo.InvariantCulture)) current = array[index];
                    else { present = false; break; }
                }
                if (present) return current;
            }
            throw JintSandbox.Unsatisfied("No declared selection path was present.");
        }
        JsonNode? Copy(JsonNode? value)
        {
            if (readable) Charge(value);
            return value?.DeepClone();
        }
        JsonNode? Call(ArrowFunctionExpression arrow, IReadOnlyList<JsonNode?> arguments, Dictionary<string, JsonNode?> bindings)
        {
            var local = new Dictionary<string, JsonNode?>(bindings, StringComparer.Ordinal);
            for (var i = 0; i < arrow.Params.Count; i++) local[((Identifier)arrow.Params[i]).Name] = arguments[i];
            if (arrow.Body is not BlockStatement block) return EvaluateSelection(arrow.Body, local);
            foreach (var statement in block.Body.SkipLast(1))
            {
                if (statement is IfStatement check)
                {
                    if (Truthy(EvaluateSelection(check.Test, local)))
                        throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Expression error: " + ((StringLiteral)((NewExpression)((ThrowStatement)check.Consequent).Argument!).Arguments[0]).Value);
                    continue;
                }
                var declaration = ((VariableDeclaration)statement).Declarations[0];
                local[((Identifier)declaration.Id).Name] = EvaluateSelection(declaration.Init!, local);
            }
            return EvaluateSelection(((ReturnStatement)block.Body.Last()).Argument!, local);
        }
        JsonNode? EvaluateSelection(Node node, Dictionary<string, JsonNode?> bindings)
        {
            Check();
            switch (node)
            {
                case Identifier id: return bindings[id.Name];
                case Literal literal: return literal.Value switch { string text => JsonValue.Create(text), bool boolean => JsonValue.Create(boolean), double number => readable ? JsonNode.Parse(script[literal.Start..literal.End]) : JsonValue.Create(number), _ => null };
                case MemberExpression member:
                    // A static property chain is one selection, just like the
                    // former m.select path. Resolve its root once, without
                    // evaluating/importing every intermediate container.
                    var path = new List<string>(); Node root = member;
                    while (readable && root is MemberExpression access && (!access.Computed || access.Property is StringLiteral))
                    {
                        path.Add(access.Property is Identifier part ? part.Name : ((StringLiteral)access.Property).Value);
                        root = access.Object;
                    }
                    if (path.Count != 0)
                    {
                        path.Reverse(); var sourceRecord = EvaluateSelection(root, bindings);
                        if (path[^1] == "length")
                        {
                            var collectionOwner = path.Count == 1 ? sourceRecord : Select(sourceRecord, [path.Take(path.Count - 1).ToArray()]);
                            if (collectionOwner is JsonArray counted) return JsonValue.Create(counted.Count);
                        }
                        return Select(sourceRecord, [path.ToArray()]);
                    }
                    var container = EvaluateSelection(member.Object, bindings);
                    var key = !member.Computed && member.Property is Identifier property ? property.Name : member.Property is StringLiteral memberText ? memberText.Value : GetString(EvaluateSelection(member.Property, bindings));
                    if (container is JsonArray memberArray && key == "length") return JsonValue.Create(memberArray.Count);
                    return Select(container, [[key]]);
                case ObjectExpression obj:
                    return new JsonObject(obj.Properties.Cast<Property>().Select(p => new KeyValuePair<string, JsonNode?>(Key(p)!, Copy(EvaluateSelection(p.Value, bindings)))));
                case ArrayExpression array:
                    return new JsonArray(array.Elements.Select(v => Copy(EvaluateSelection(v!, bindings))).ToArray());
                case CallExpression { Callee: MemberExpression { Property: Identifier { Name: "select" } } } call:
                    var selections = ((ArrayExpression)call.Arguments[1]).Elements.Cast<ArrayExpression>()
                        .Select(p => p.Elements.Cast<StringLiteral>().Select(v => v.Value).ToArray()).ToArray();
                    var sourceValue = EvaluateSelection(call.Arguments[0], bindings);
                    if (!((BooleanLiteral)call.Arguments[2]).Value) return Select(sourceValue, selections);
                    if (sourceValue is not JsonArray items) throw JintSandbox.Unsatisfied("Per-item selection requires an observed array.");
                    return new JsonArray(items.Select(item => Copy(Select(item, selections))).ToArray());
                case CallExpression { Callee: ArrowFunctionExpression calledArrow } call:
                    return Call(calledArrow, call.Arguments.Select(a => EvaluateSelection(a, bindings)).ToArray(), bindings);
                case CallExpression { Callee: Identifier { Name: "json" } } call:
                    return _functions["json"]([EvaluateSelection(call.Arguments[0], bindings)]);
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Number" } } } call:
                    return JsonValue.Create(JsonSchemaInstanceValidator.TryReadNumber(EvaluateSelection(call.Arguments[0], bindings), out var finite) && double.IsFinite(finite));
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "JSON" } } } call:
                    return BuiltInFunctions.All["json"]([EvaluateSelection(call.Arguments[0], bindings)]);
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Array" } } } call:
                    return JsonValue.Create(EvaluateSelection(call.Arguments[0], bindings) is JsonArray);
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "entries" } } } call:
                    if (EvaluateSelection(call.Arguments[0], bindings) is not JsonObject record) throw JintSandbox.Unsatisfied("Container projection requires an object.");
                    return new JsonArray(record.Select(p => (JsonNode)new JsonArray(JsonValue.Create(p.Key), Copy(p.Value))).ToArray());
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "fromEntries" } } } call:
                    if (EvaluateSelection(call.Arguments[0], bindings) is not JsonArray entries) throw JintSandbox.Unsatisfied("Container assembly requires entry pairs.");
                    var assembled = new JsonObject();
                    foreach (var entry in entries)
                    {
                        Check();
                        if (entry is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue name || !name.TryGetValue<string>(out var propertyName))
                            throw JintSandbox.Unsatisfied("Container assembly requires named entry pairs.");
                        assembled[propertyName] = Copy(pair[1]);
                    }
                    return assembled;
                case CallExpression { Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier helper } } call:
                    if (helper.Name == "scalar") return JsonValue.Create(EvaluateSelection(call.Arguments[0], bindings) is not (JsonObject or JsonArray));
                    var operands = new JsonObject();
                    for (var i = 0; i < call.Arguments.Count; i++) operands["operand" + i] = Copy(EvaluateSelection(call.Arguments[i], bindings));
                    // Only bounded operands enter the existing helper sandbox, never data or locals.
                    allowance?.Stop();
                    try
                    {
                        var sandbox = new JintSandbox(Math.Min(_maxStatements, 10000), (int)Math.Min(_timeout.TotalMilliseconds, 5000), Math.Min(_memoryLimitBytes, 50000000));
                        var arguments = Enumerable.Range(0, call.Arguments.Count).Select(i => helper.Name == "lookup" && i == 2 && call.Arguments[i] is StringLiteral field
                            ? JsonValue.Create(field.Value)!.ToJsonString() : "source.operand" + i);
                        return sandbox.ExecuteMappingValue("m." + helper.Name + "(" + string.Join(',', arguments) + ")", operands, new JsonObject(), null, allowance ?? sandbox.CreateMappingAllowance(), default);
                    }
                    finally { allowance?.Start(); }
                case CallExpression { Callee: MemberExpression member } call:
                    if (EvaluateSelection(member.Object, bindings) is not JsonArray collection)
                        throw JintSandbox.Unsatisfied("Collection projection requires an observed array.");
                    var arrow = (ArrowFunctionExpression)call.Arguments[0]; var parameter = ((Identifier)arrow.Params[0]).Name;
                    var local = new Dictionary<string, JsonNode?>(bindings, StringComparer.Ordinal);
                    var result = new JsonArray();
                    for (var index = 0; index < collection.Count; index++)
                    {
                        local[parameter] = collection[index];
                        if (arrow.Params.Count == 2) local[((Identifier)arrow.Params[1]).Name] = JsonValue.Create(index);
                        var projected = readable ? Call(arrow, arrow.Params.Count == 2 ? [collection[index], JsonValue.Create(index)] : [collection[index]], bindings) : EvaluateSelection(arrow.Body, local);
                        if (((Identifier)member.Property).Name == "flatMap")
                        {
                            if (projected is not JsonArray inner) throw JintSandbox.Unsatisfied("Per-item selection requires an observed array.");
                            foreach (var child in inner) result.Add(Copy(child));
                        }
                        else result.Add(Copy(projected));
                    }
                    return result;
                case ConditionalExpression conditional:
                    return EvaluateSelection(Truthy(EvaluateSelection(conditional.Test, bindings)) ? conditional.Consequent : conditional.Alternate, bindings);
                case UnaryExpression unary:
                    var operand = EvaluateSelection(unary.Argument, bindings);
                    if (unary.Operator == Acornima.Operator.TypeOf) return JsonValue.Create(operand is null or JsonObject or JsonArray ? "object" : operand.AsValue().TryGetValue<string>(out _) ? "string" : operand.AsValue().TryGetValue<bool>(out _) ? "boolean" : "number");
                    if (unary.Operator == Acornima.Operator.UnaryNegation) return EvaluateCore("-operand", null, new(StringComparer.Ordinal) { ["operand"] = operand }, allowance);
                    return JsonValue.Create(!Truthy(operand));
                case BinaryExpression binary:
                    // The compiler's completion predicate is a loose null check
                    // on an optional own property. Absence is allowed only here;
                    // ordinary checked selections must still reject it.
                    if (binary.Operator is Acornima.Operator.Equality or Acornima.Operator.Inequality && binary.Right is NullLiteral && binary.Left is MemberExpression presence &&
                        EvaluateSelection(presence.Object, bindings) is JsonObject owner)
                    {
                        var presenceKey = !presence.Computed && presence.Property is Identifier presenceId ? presenceId.Name : (presence.Property as StringLiteral)?.Value;
                        if (presenceKey is not null)
                        {
                            owner.TryGetPropertyValue(presenceKey, out var present);
                            return JsonValue.Create(binary.Operator == Acornima.Operator.Equality ? present is null : present is not null);
                        }
                    }
                    var left = EvaluateSelection(binary.Left, bindings);
                    if (binary.Operator == Acornima.Operator.LogicalAnd && !Truthy(left) || binary.Operator == Acornima.Operator.LogicalOr && Truthy(left)) return left;
                    var right = EvaluateSelection(binary.Right, bindings);
                    if (binary.Operator is Acornima.Operator.LogicalAnd or Acornima.Operator.LogicalOr) return right;
                    if (binary.Operator is Acornima.Operator.Addition or Acornima.Operator.Subtraction or Acornima.Operator.Multiplication or Acornima.Operator.Division or Acornima.Operator.Remainder)
                    {
                        var operation = binary.Operator switch { Acornima.Operator.Addition => "+", Acornima.Operator.Subtraction => "-", Acornima.Operator.Multiplication => "*", Acornima.Operator.Division => "/", _ => "%" };
                        return EvaluateCore("left " + operation + " right", null, new(StringComparer.Ordinal) { ["left"] = left, ["right"] = right }, allowance);
                    }
                    // Use ordinary Number comparisons; copies above retain their JSON representation.
                    var equal = left is JsonObject or JsonArray || right is JsonObject or JsonArray ? ReferenceEquals(left, right) : JsonNode.DeepEquals(left, right);
                    return JsonValue.Create(binary.Operator switch
                    {
                        Acornima.Operator.StrictEquality or Acornima.Operator.Equality => equal,
                        Acornima.Operator.StrictInequality or Acornima.Operator.Inequality => !equal,
                        Acornima.Operator.LessThan => GetNumber(left) < GetNumber(right),
                        Acornima.Operator.LessThanOrEqual => GetNumber(left) <= GetNumber(right),
                        Acornima.Operator.GreaterThan => GetNumber(left) > GetNumber(right),
                        Acornima.Operator.GreaterThanOrEqual => GetNumber(left) >= GetNumber(right),
                        _ => throw JintSandbox.Unsatisfied("Unsupported checked comparison.")
                    });
                default: throw JintSandbox.Unsatisfied("Unsupported structural selection.");
            }
        }
        result = EvaluateSelection(syntax, initialBindings ?? new(StringComparer.Ordinal) { ["source"] = source });
        var beforeOutput = bytes;
        Charge(result);
        if (readable && allowance is not null) allowance.OutputBytes += bytes - beforeOutput;
        result = result?.DeepClone();
        return true;
    }

    public ExpressionEvaluator(Dictionary<string, Func<JsonNode?[], JsonNode?>>? extraFunctions = null)
        : this(extraFunctions, maxStatements: DefaultMaxStatements, timeout: TimeSpan.FromSeconds(DefaultTimeoutSeconds), memoryLimitBytes: DefaultMemoryLimitBytes)
    {
    }

    public ExpressionEvaluator(
        Dictionary<string, Func<JsonNode?[], JsonNode?>>? extraFunctions,
        int maxStatements,
        TimeSpan timeout,
        int memoryLimitBytes = DefaultMemoryLimitBytes)
    {
        _functions = new Dictionary<string, Func<JsonNode?[], JsonNode?>>(BuiltInFunctions.All);
        if (extraFunctions != null)
        {
            foreach (var kv in extraFunctions)
            {
                if (kv.Key is ArtifactCollectionExpression.FunctionName or JintSandbox.MappingFunction) throw new ArgumentException("Reserved expression helpers cannot be overridden.");
                _functions[kv.Key] = kv.Value;
            }
        }

        _maxStatements = Math.Max(1, maxStatements);
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(DefaultTimeoutSeconds) : timeout;
        _memoryLimitBytes = Math.Max(1_000_000, memoryLimitBytes);
    }

    /// <summary>
    /// Evaluate a JavaScript expression string against a JsonNode context.
    /// The context is exposed as the variable "data".
    /// Top-level keys of the context (inputs, steps, env, error, step) are also available directly.
    /// </summary>
    public JsonNode? Evaluate(string expression, JsonNode? context) => EvaluateCore(expression, context);

    private static bool TryMappingProgram(Node node, out string program)
    {
        program = node switch
        {
            StringLiteral literal => literal.Value,
            TemplateLiteral { Expressions.Count: 0, Quasis.Count: 1 } template => template.Quasis[0].Value.Cooked!,
            _ => null!
        };
        return program is not null;
    }

    private JsonNode? EvaluateCore(string expression, JsonNode? context,
        Dictionary<string, JsonNode?>? locals = null, JintSandbox.MappingAllowance? allowance = null)
    {
        // Preserve exact JSON scalar values when wiring data; do not round decimals through JS doubles.
        var syntax = new Acornima.Parser().ParseExpression(expression);
        if (syntax is CallExpression { Callee: Identifier { Name: JintSandbox.MappingFunction }, Arguments.Count: 3 } compiled &&
            TryMappingProgram(compiled.Arguments[0], out var program) && compiled.Arguments[1] is Identifier { Name: "data" })
        {
            if (allowance is not null) throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled binding groups cannot nest.");
            var contracts = JsonNode.Parse(expression[compiled.Arguments[2].Start..compiled.Arguments[2].End]) as JsonObject
                ?? throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled bindings require literal contracts.");
            return EvaluateCompiledBindings(program, context, contracts);
        }
        if (syntax is CallExpression { Callee: Identifier { Name: JintSandbox.MappingFunction }, Arguments.Count: 2 } mapping &&
            TryMappingProgram(mapping.Arguments[0], out var script))
        {
            if (!Structural(mapping.Arguments[1], out var source))
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, "checkedMapping requires a direct structured source binding.");
            if (TrySelectMapping(script, source, out var selected, allowance)) return selected;
            return Mapping(script, source);
        }
        if (Structural(syntax, out var direct)) return direct is null ? null : JsonNode.Parse(direct.ToJsonString());

        bool Structural(Node node, out JsonNode? value)
        {
            value = null;
            allowance?.Check();
            if (node is Identifier local && locals?.TryGetValue(local.Name, out value) == true) return true;
            if (node is CallExpression { Callee: Identifier { Name: JintSandbox.MappingFunction }, Arguments.Count: 2 } nested &&
                TryMappingProgram(nested.Arguments[0], out var nestedScript) && Structural(nested.Arguments[1], out var nestedSource))
            {
                if (TrySelectMapping(nestedScript, nestedSource, out value, allowance)) return true;
                value = Mapping(nestedScript, nestedSource); return true;
            }
            if (node is Identifier { Name: "data" }) { value = context; return true; }
            if (node is Literal literal)
            {
                if (literal.Value is string text) { value = JsonValue.Create(text); return true; }
                if (literal.Value is bool boolean) { value = JsonValue.Create(boolean); return true; }
                if (literal.Value is null) return true;
                if (literal.Value is double) { value = JsonNode.Parse(expression[node.Start..node.End]); return true; }
            }
            if (allowance is not null && node is UnaryExpression { Operator: Acornima.Operator.LogicalNot } negate && Structural(negate.Argument, out var operand))
            { value = JsonValue.Create(!Truthy(operand)); return true; }
            if (allowance is not null && node is BinaryExpression binary && Structural(binary.Left, out var left))
            {
                if (binary.Operator == Acornima.Operator.LogicalAnd && !Truthy(left) || binary.Operator == Acornima.Operator.LogicalOr && Truthy(left))
                { value = left; return true; }
                if (Structural(binary.Right, out var right))
                {
                    if (binary.Operator is Acornima.Operator.LogicalAnd or Acornima.Operator.LogicalOr) { value = right; return true; }
                    if (binary.Operator is Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality)
                    {
                        var equal = left is JsonObject or JsonArray || right is JsonObject or JsonArray ? ReferenceEquals(left, right) : JsonNode.DeepEquals(left, right);
                        value = JsonValue.Create(binary.Operator == Acornima.Operator.StrictEquality ? equal : !equal); return true;
                    }
                }
            }
            if (node is MemberExpression member && Structural(member.Object, out var container))
            {
                var key = member.Property is Identifier id && !member.Computed ? id.Name : (member.Property as Literal)?.Value?.ToString();
                if (key is null && member.Computed && Structural(member.Property, out var selectedKey) && selectedKey is JsonValue scalarKey)
                {
                    if (scalarKey.TryGetValue<string>(out var textKey)) key = textKey;
                    else if (int.TryParse(scalarKey.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var indexKey))
                        key = indexKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                if (key is null) return false;
                if (container is JsonObject obj) { obj.TryGetPropertyValue(key, out value); return true; }
                if (container is JsonArray counted && key == "length") { value = JsonValue.Create(counted.Count); return true; }
                if (container is JsonArray array && int.TryParse(key, out var index) && index >= 0 && index < array.Count) { value = array[index]; return true; }
                return false;
            }
            if (node is ObjectExpression objectExpression)
            {
                var result = new JsonObject();
                foreach (var item in objectExpression.Properties)
                {
                    if (item is not Property { Computed: false, Method: false } property || property.Kind != PropertyKind.Init ||
                        !Structural(property.Value, out var field)) return false;
                    var name = property.Key is Identifier id ? id.Name : (property.Key as Literal)?.Value?.ToString();
                    if (name is null || result.ContainsKey(name)) return false;
                    result.Add(name, field?.DeepClone());
                }
                value = result; return true;
            }
            if (node is ArrayExpression arrayExpression)
            {
                var result = new JsonArray();
                foreach (var item in arrayExpression.Elements)
                    if (item is not null && Structural(item, out var element)) result.Add(element?.DeepClone()); else return false;
                value = result; return true;
            }
            return false;
        }
        JsonNode? Mapping(string script, JsonNode? source)
        {
            var sandbox = new JintSandbox(Math.Min(_maxStatements, 10000), (int)Math.Min(_timeout.TotalMilliseconds, 5000), Math.Min(_memoryLimitBytes, 50000000));
            if (allowance is null) return sandbox.ExecuteMapping(script, source);
            allowance.Stop();
            try { return sandbox.ExecuteMappingValue(script, source, new JsonObject(), null, allowance, default); }
            finally { allowance.Start(); }
        }
        var engine = new Engine(options =>
        {
            options.MaxStatements(_maxStatements);
            if (allowance is not null) options.Constraint(allowance);
            options.TimeoutInterval(_timeout);
            options.LimitMemory(_memoryLimitBytes);
            options.Strict(false);
        });

        // Expose context as "data"
        if (context != null)
        {
            var jsData = JintSandbox.JsonToJsValue(engine, context);
            engine.SetValue("data", jsData);

            // Also expose top-level keys directly for convenience
            if (context is JsonObject obj)
            {
                foreach (var kv in obj)
                    engine.SetValue(kv.Key, JintSandbox.JsonToJsValue(engine, kv.Value));
            }
        }
        else
        {
            engine.SetValue("data", JsValue.Null);
        }

        if (locals is not null)
            foreach (var (name, value) in locals) engine.SetValue(name, JintSandbox.JsonToJsValue(engine, value));
        JintUrlInterop.Install(engine);

        // Register built-in + custom functions
        RegisterFunctions(engine);

        try
        {
            var result = engine.Evaluate(expression);
            return JintSandbox.JsValueToJson(result);
        }
        catch (JavaScriptException ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.EvalError, $"Expression error: {ex.Message}");
        }
        catch (StatementsCountOverflowException ex)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.EvalError,
                $"Expression exceeded the configured statement limit ({_maxStatements}). Increase ExecutionLimits.MaxExpressionStatements or simplify the expression.",
                retryable: false,
                inner: ex);
        }
        catch (ExecutionCanceledException)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.EvalError,
                $"Expression evaluation timed out or exceeded a runtime limit (timeout: {_timeout.TotalSeconds:0.#}s, statements: {_maxStatements}).",
                retryable: false);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Jint receives a statically declared dispatcher delegate and every callable target remains directly referenced; Native AOT smoke tests execute this path.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2111:DynamicallyAccessedMembers",
        Justification = "Jint delegate reflection is restricted to the preserved Func<string, string, string> dispatcher signature.")]
    private void RegisterFunctions(Engine engine)
    {
        engine.SetValue("__dispatch", new Func<string, string, string>((name, argsJson) =>
        {
            if (!_functions.TryGetValue(name, out var func))
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, $"Unknown function: {name}");

            var argsArray = JsonNode.Parse(argsJson) as JsonArray ?? new JsonArray();
            var jsonArgs = new JsonNode?[argsArray.Count];
            for (int i = 0; i < argsArray.Count; i++)
                jsonArgs[i] = argsArray[i]?.DeepClone();
            var result = func(jsonArgs);
            return result?.ToJsonString() ?? "null";
        }));

        foreach (var funcName in _functions.Keys)
        {
            engine.Execute(
                $"function {funcName}() {{ " +
                $"var a = []; for (var i = 0; i < arguments.length; i++) a.push(arguments[i]); " +
                $"var r = __dispatch('{funcName}', JSON.stringify(a)); " +
                $"return JSON.parse(r); }}"
            );
        }

        // Expose all functions also under a "functions" namespace object
        // so that both functions.myFunc(...) and myFunc(...) work.
        engine.Execute("var functions = {};");
        foreach (var funcName in _functions.Keys)
        {
            engine.Execute($"functions.{funcName} = {funcName};");
        }
    }

    /// <summary>
    /// Validate that an expression can be parsed (does not execute it).
    /// Throws ExpressionParseException if invalid.
    /// </summary>
    public static void Validate(string expression)
    {
        try
        {
            // Use Acornima (Jint's parser) to check syntax
            new Acornima.Parser().ParseExpression(expression);
        }
        catch (Exception ex)
        {
            throw new ExpressionParseException($"Invalid expression: {ex.Message}", 0);
        }
    }

    // === Type helpers (kept for backward compatibility) ===

    public static double GetNumber(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out double d)) return d;
            if (val.TryGetValue(out decimal m)) return (double)m;
            if (val.TryGetValue(out float f)) return f;
            if (val.TryGetValue(out byte b8)) return b8;
            if (val.TryGetValue(out sbyte s8)) return s8;
            if (val.TryGetValue(out short s16)) return s16;
            if (val.TryGetValue(out ushort u16)) return u16;
            if (val.TryGetValue(out int i)) return i;
            if (val.TryGetValue(out uint ui)) return ui;
            if (val.TryGetValue(out long l)) return l;
            if (val.TryGetValue(out ulong ul)) return ul;
            if (val.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        if (node == null) return 0;
        throw new WorkflowRuntimeException(ErrorCodes.ExprTypeMismatch, $"Expected number but got: {node.ToJsonString()}");
    }

    public static bool GetBool(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out bool b)) return b;
            // Also accept string "true"/"false" for robustness
            if (val.TryGetValue(out string? s))
            {
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        if (node == null) return false;
        throw new WorkflowRuntimeException(ErrorCodes.ExprTypeMismatch, $"Expected bool but got: {node.ToJsonString()}");
    }

    public static string GetString(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out string? s)) return s ?? "";
        }
        if (node == null) return "";
        return node.ToJsonString();
    }


    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create(m),
        float f => JsonValue.Create(f),
        byte b8 => JsonValue.Create(b8),
        sbyte s8 => JsonValue.Create(s8),
        short s16 => JsonValue.Create(s16),
        ushort u16 => JsonValue.Create(u16),
        int i => JsonValue.Create(i),
        uint ui => JsonValue.Create(ui),
        long l => JsonValue.Create(l),
        ulong ul => JsonValue.Create(ul),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };
}
