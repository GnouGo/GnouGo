using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;
using Acornima.Ast;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Evaluates expressions using the Jint JavaScript engine.
/// Context shape: { inputs: {...}, steps: {...}, env: {...} }
/// </summary>
public sealed class ExpressionEvaluator
{
    private const int DefaultMaxStatements = 100_000;
    private const int DefaultTimeoutSeconds = 15;
    private const int DefaultMemoryLimitBytes = 50_000_000;

    private readonly Dictionary<string, Func<JsonNode?[], JsonNode?>> _functions;
    private readonly int _maxStatements;
    private readonly TimeSpan _timeout;
    private readonly int _memoryLimitBytes;

    // Only this closed compiler recipe bypasses JS import. Learned programs still
    // use Jint; selection cannot compute values or grant new artifact provenance.
    private bool TrySelectMapping(string script, JsonNode? source, out JsonNode? result)
    {
        result = null;
        if (script.Length > 65536) return false;
        var syntax = new Acornima.Parser().ParseExpression(script);
        static string? Key(Property property) => property.Key is Identifier id ? id.Name : (property.Key as StringLiteral)?.Value;
        static bool Supported(Node node, HashSet<string> names) => node switch
        {
            Identifier id => names.Contains(id.Name),
            Literal literal => literal.Value is null or string or bool or double,
            MemberExpression member => Supported(member.Object, names) &&
                (member.Computed ? member.Property is StringLiteral : member.Property is Identifier),
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
                Supported(arrow.Body, new HashSet<string>(names.Concat(arrow.Params.Cast<Identifier>().Select(p => p.Name)), StringComparer.Ordinal)),
            _ => false
        };
        if (!Supported(syntax, new(StringComparer.Ordinal) { "source" })) return false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0; var count = 0;
        void Check()
        {
            if (++count > Math.Min(_maxStatements, 10000) || watch.Elapsed > TimeSpan.FromMilliseconds(Math.Min(_timeout.TotalMilliseconds, 5000)))
                throw JintSandbox.Unsatisfied("Collection selection exceeded its execution allowance.");
        }
        void Charge(JsonNode? value, int depth = 0)
        {
            bytes += 128 + (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? (long)text.Length * 4 : 0);
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
        JsonNode? Copy(JsonNode? value) => value?.DeepClone();
        JsonNode? EvaluateSelection(Node node, Dictionary<string, JsonNode?> bindings)
        {
            Check();
            switch (node)
            {
                case Identifier id: return bindings[id.Name];
                case Literal literal: return literal.Value switch { string text => JsonValue.Create(text), bool boolean => JsonValue.Create(boolean), double number => JsonValue.Create(number), _ => null };
                case MemberExpression member:
                    var container = EvaluateSelection(member.Object, bindings);
                    var key = member.Property is Identifier property ? property.Name : ((StringLiteral)member.Property).Value;
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
                        result.Add(Copy(EvaluateSelection(arrow.Body, local)));
                    }
                    return result;
                default: throw JintSandbox.Unsatisfied("Unsupported structural selection.");
            }
        }
        result = EvaluateSelection(syntax, new(StringComparer.Ordinal) { ["source"] = source });
        Charge(result); result = result?.DeepClone();
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
    public JsonNode? Evaluate(string expression, JsonNode? context)
    {
        // Preserve exact JSON scalar values when wiring data; do not round decimals through JS doubles.
        var syntax = new Acornima.Parser().ParseExpression(expression);
        if (syntax is CallExpression { Callee: Identifier { Name: JintSandbox.MappingFunction }, Arguments.Count: 2 } mapping &&
            mapping.Arguments[0] is Literal { Value: string script })
        {
            if (!Structural(mapping.Arguments[1], out var source))
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, "checkedMapping requires a direct structured source binding.");
            if (TrySelectMapping(script, source, out var selected)) return selected;
            return new JintSandbox(Math.Min(_maxStatements, 10000), (int)Math.Min(_timeout.TotalMilliseconds, 5000), Math.Min(_memoryLimitBytes, 50000000))
                .ExecuteMapping(script, source);
        }
        if (Structural(syntax, out var direct)) return direct is null ? null : JsonNode.Parse(direct.ToJsonString());

        bool Structural(Node node, out JsonNode? value)
        {
            value = null;
            if (node is CallExpression { Callee: Identifier { Name: JintSandbox.MappingFunction }, Arguments.Count: 2 } nested &&
                nested.Arguments[0] is Literal { Value: string nestedScript } && Structural(nested.Arguments[1], out var nestedSource))
            {
                if (TrySelectMapping(nestedScript, nestedSource, out value)) return true;
                value = new JintSandbox(Math.Min(_maxStatements, 10000), (int)Math.Min(_timeout.TotalMilliseconds, 5000), Math.Min(_memoryLimitBytes, 50000000))
                    .ExecuteMapping(nestedScript, nestedSource); return true;
            }
            if (node is Identifier { Name: "data" }) { value = context; return true; }
            if (node is Literal literal)
            {
                if (literal.Value is string text) { value = JsonValue.Create(text); return true; }
                if (literal.Value is bool boolean) { value = JsonValue.Create(boolean); return true; }
                if (literal.Value is null) return true;
                if (literal.Value is double) { value = JsonNode.Parse(expression[node.Start..node.End]); return true; }
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
        var engine = new Engine(options =>
        {
            options.MaxStatements(_maxStatements);
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
