using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;

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
                _functions[kv.Key] = kv.Value;
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

    private void RegisterFunctions(Engine engine)
    {
#pragma warning disable IL2026, IL2111 // Jint delegate interop
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
#pragma warning restore IL2026, IL2111

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
            if (val.TryGetValue(out int i)) return i;
            if (val.TryGetValue(out long l)) return l;
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
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };
}

