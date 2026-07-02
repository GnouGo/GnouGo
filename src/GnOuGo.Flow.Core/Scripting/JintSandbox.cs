using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Scripting;

/// <summary>
/// Sandboxed Jint JavaScript execution engine.
/// Restricted: memory/time limits.
/// </summary>
public sealed class JintSandbox
{
    private readonly int _maxStatements;
    private readonly TimeSpan _timeout;
    private readonly long _memoryLimit;

    public JintSandbox(int maxStatements = 10_000, int timeoutMs = 5000, long memoryLimitBytes = 50_000_000)
    {
        _maxStatements = maxStatements;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _memoryLimit = memoryLimitBytes;
    }

    /// <summary>
    /// Execute a WFScript functions block and return the declared functions
    /// as a dictionary that can be called from expressions.
    /// </summary>
    public Dictionary<string, Func<JsonNode?[], JsonNode?>> LoadFunctions(string script, JsonNode? dataContext = null)
    {
        var engine = CreateEngine(dataContext);

        try
        {
            engine.Execute(script);
        }
        catch (JavaScriptException ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.ScriptError, $"Script error: {ex.Message}");
        }
        catch (Jint.Runtime.ExecutionCanceledException)
        {
            throw new WorkflowRuntimeException(ErrorCodes.ScriptError, "Script execution timed out or exceeded statement limit");
        }

        var functions = new Dictionary<string, Func<JsonNode?[], JsonNode?>>();
        var funcNames = ExtractFunctionNames(script);
        foreach (var funcName in funcNames)
        {
            var capturedName = funcName;
            var capturedEngine = engine;
            functions[capturedName] = args =>
            {
                var jsArgs = args.Select(a => JsonToJsValue(capturedEngine, a)).ToArray();
                try
                {
                    var result = capturedEngine.Invoke(capturedName, jsArgs);
                    return JsValueToJson(result);
                }
                catch (JavaScriptException ex2)
                {
                    throw new WorkflowRuntimeException(ErrorCodes.ScriptError, $"Function '{capturedName}' error: {ex2.Message}");
                }
            };
        }

        return functions;
    }

    /// <summary>
    /// Execute a script snippet and return the result.
    /// </summary>
    public JsonNode? Execute(string script, JsonNode? dataContext = null)
    {
        var engine = CreateEngine(dataContext);

        try
        {
            var result = engine.Evaluate(script);
            return JsValueToJson(result);
        }
        catch (JavaScriptException ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.ScriptError, $"Script error: {ex.Message}");
        }
        catch (Jint.Runtime.ExecutionCanceledException)
        {
            throw new WorkflowRuntimeException(ErrorCodes.ScriptError, "Script execution timed out or exceeded limit");
        }
    }

    private Engine CreateEngine(JsonNode? dataContext)
    {
        var engine = new Engine(options =>
        {
            options.MaxStatements(_maxStatements);
            options.TimeoutInterval(_timeout);
            options.LimitMemory(_memoryLimit);
            options.Strict();
        });

        if (dataContext != null)
        {
            var jsData = JsonToJsValue(engine, dataContext);
            engine.SetValue("data", jsData);
        }

        JintUrlInterop.Install(engine);

        // Register built-in functions via a dispatcher + JS wrappers
        // This avoids Jint delegate signature issues
#pragma warning disable IL2026, IL2111 // Jint delegate interop
        engine.SetValue("__dispatch", new Func<string, string, string>((name, argsJson) =>
        {
            if (!BuiltInFunctions.All.TryGetValue(name, out var func))
                return "null";
            
            var argsArray = System.Text.Json.Nodes.JsonNode.Parse(argsJson) as JsonArray ?? new JsonArray();
            var jsonArgs = new JsonNode?[argsArray.Count];
            for (int i = 0; i < argsArray.Count; i++)
                jsonArgs[i] = argsArray[i];
            var result = func(jsonArgs);
            return result?.ToJsonString() ?? "null";
        }));
#pragma warning restore IL2026, IL2111

        // Define each built-in as a JS function that marshals through JSON
        foreach (var funcName in BuiltInFunctions.All.Keys)
        {
            engine.Execute(
                $"function {funcName}() {{ " +
                $"var a = []; for (var i = 0; i < arguments.length; i++) a.push(arguments[i]); " +
                $"var r = __dispatch('{funcName}', JSON.stringify(a)); " +
                $"return JSON.parse(r); }}"
            );
        }

        // Expose all built-in functions under "functions" namespace
        engine.Execute("var functions = {};");
        foreach (var funcName in BuiltInFunctions.All.Keys)
        {
            engine.Execute($"functions.{funcName} = {funcName};");
        }

        return engine;
    }

    private static List<string> ExtractFunctionNames(string script)
    {
        var names = new List<string>();
        var regex = new System.Text.RegularExpressions.Regex(@"function\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(");
        foreach (System.Text.RegularExpressions.Match match in regex.Matches(script))
        {
            names.Add(match.Groups[1].Value);
        }
        return names;
    }

    /// <summary>
    /// Convert JsonNode to Jint JsValue.
    /// </summary>
    public static JsValue JsonToJsValue(Engine engine, JsonNode? node)
    {
        if (node == null) return JsValue.Null;

        if (node is JsonValue val)
        {
            if (val.TryGetValue(out bool b)) return b ? JsBoolean.True : JsBoolean.False;
            if (val.TryGetValue(out double d)) return new JsNumber(d);
            if (val.TryGetValue(out decimal m)) return new JsNumber((double)m);
            if (val.TryGetValue(out float f)) return new JsNumber(f);
            if (val.TryGetValue(out byte b8)) return new JsNumber(b8);
            if (val.TryGetValue(out sbyte s8)) return new JsNumber(s8);
            if (val.TryGetValue(out short s16)) return new JsNumber(s16);
            if (val.TryGetValue(out ushort u16)) return new JsNumber(u16);
            if (val.TryGetValue(out int i)) return new JsNumber(i);
            if (val.TryGetValue(out uint ui)) return new JsNumber(ui);
            if (val.TryGetValue(out long l)) return new JsNumber(l);
            if (val.TryGetValue(out ulong ul)) return new JsNumber(ul);
            if (val.TryGetValue(out string? s)) return new JsString(s ?? "");
        }

        if (node is JsonObject obj)
        {
            var jsObj = new JsObject(engine);
            foreach (var kv in obj)
            {
                jsObj.Set(kv.Key, JsonToJsValue(engine, kv.Value));
            }
            return jsObj;
        }

        if (node is JsonArray arr)
        {
            var items = arr.Select(item => JsonToJsValue(engine, item)).ToArray();
            return engine.Intrinsics.Array.Construct(items);
        }

        return JsValue.Null;
    }

    /// <summary>
    /// Convert Jint JsValue to JsonNode.
    /// </summary>
    public static JsonNode? JsValueToJson(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined()) return null;
        if (value.IsBoolean()) return JsonValue.Create(value.AsBoolean());
        if (value.IsNumber())
        {
            var d = value.AsNumber();
            // Preserve integer types when possible
            if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue && !double.IsInfinity(d))
                return JsonValue.Create((int)d);
            return JsonValue.Create(d);
        }
        if (value.IsString()) return JsonValue.Create(value.AsString());

        if (value.IsArray())
        {
            var arr = new JsonArray();
            var jsArr = value.AsArray();
            foreach (var item in jsArr)
            {
                arr.Add(JsValueToJson(item));
            }
            return arr;
        }

        if (value.IsObject())
        {
            var obj = new JsonObject();
            var jsObj = value.AsObject();
            foreach (var prop in jsObj.GetOwnProperties())
            {
                var key = prop.Key.ToString();
                if (key != null)
                    obj[key] = JsValueToJson(prop.Value.Value);
            }
            return obj;
        }

        return null;
    }
}


