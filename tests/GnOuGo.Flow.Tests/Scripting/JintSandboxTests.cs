using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Scripting;

public class JintSandboxTests
{
    [Fact]
    public void LoadFunctions_SimpleFunction_IsCallable()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions("function add(a, b) { return a + b; }");
        Assert.True(funcs.ContainsKey("add"));
        var result = funcs["add"](new JsonNode?[] { JsonValue.Create(3), JsonValue.Create(4) });
        Assert.Equal(7, result!.GetValue<int>());
    }

    [Fact]
    public void LoadFunctions_MultipleDeclarations_AllLoaded()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions("function foo() { return 1; }\nfunction bar() { return 2; }");
        Assert.True(funcs.ContainsKey("foo"));
        Assert.True(funcs.ContainsKey("bar"));
    }

    [Fact]
    public void LoadFunctions_WithIfElse_Works()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions(@"
            function classify(x) {
                if (x > 10) {
                    return ""high"";
                } else {
                    return ""low"";
                }
            }
        ");
        var result1 = funcs["classify"](new JsonNode?[] { JsonValue.Create(15) });
        Assert.Equal("high", result1!.GetValue<string>());
        var result2 = funcs["classify"](new JsonNode?[] { JsonValue.Create(5) });
        Assert.Equal("low", result2!.GetValue<string>());
    }

    [Fact]
    public void LoadFunctions_WithLetVariable_Works()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions("function doubled(x) { let y = x * 2; return y; }");
        var result = funcs["doubled"](new JsonNode?[] { JsonValue.Create(21) });
        Assert.Equal(42, result!.GetValue<int>());
    }

    [Fact]
    public void LoadFunctions_AccessesDataContext()
    {
        var sandbox = new JintSandbox();
        var data = new JsonObject { ["value"] = 10 };
        var funcs = sandbox.LoadFunctions("function getVal() { return data.value; }", data);
        var result = funcs["getVal"](Array.Empty<JsonNode?>());
        Assert.Equal(10, result!.GetValue<int>());
    }

    [Fact]
    public void LoadFunctions_CanCallBuiltIns()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions(@"
            function test(s) {
                return contains(s, ""hello"");
            }
        ");
        var result = funcs["test"](new JsonNode?[] { JsonValue.Create("hello world") });
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void LoadFunctions_CanUseUrlConstructor()
    {
        var sandbox = new JintSandbox();
        var funcs = sandbox.LoadFunctions(@"
            function parseGithubRepoUrl(url) {
                const u = new URL(url);
                const parts = u.pathname.replace(/^\/+/, '').split('/');
                return { owner: parts[0], repo: parts[1], hostname: u.hostname };
            }
        ");

        var result = funcs["parseGithubRepoUrl"](
            new JsonNode?[] { JsonValue.Create("https://github.com/AxaFrance/oidc-client") }) as JsonObject;

        Assert.NotNull(result);
        Assert.Equal("github.com", result!["hostname"]!.GetValue<string>());
        Assert.Equal("AxaFrance", result["owner"]!.GetValue<string>());
        Assert.Equal("oidc-client", result["repo"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_SimpleExpression_ReturnsResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("1 + 2");
        Assert.Equal(3, result!.GetValue<int>());
    }

    [Fact]
    public void Execute_WithDataContext_CanAccessData()
    {
        var sandbox = new JintSandbox();
        var data = new JsonObject { ["x"] = 42 };
        var result = sandbox.Execute("data.x", data);
        Assert.Equal(42, result!.GetValue<int>());
    }

    [Fact]
    public void Execute_StringResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("'hello'");
        Assert.Equal("hello", result!.GetValue<string>());
    }

    [Fact]
    public void Execute_NullResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("null");
        Assert.Null(result);
    }

    [Fact]
    public void Execute_BoolResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("true");
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void Execute_ArrayResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("[1, 2, 3]");
        Assert.IsType<JsonArray>(result);
        Assert.Equal(3, ((JsonArray)result).Count);
    }

    [Fact]
    public void Execute_ObjectResult()
    {
        var sandbox = new JintSandbox();
        var result = sandbox.Execute("({a: 1, b: 2})");
        Assert.IsType<JsonObject>(result);
    }

    [Fact]
    public void LoadFunctions_InvalidScript_ThrowsWorkflowRuntimeException()
    {
        var sandbox = new JintSandbox();
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(
            () => sandbox.LoadFunctions("this is not valid javascript !!!"));
    }

    [Fact]
    public void Execute_InvalidScript_ThrowsWorkflowRuntimeException()
    {
        var sandbox = new JintSandbox();
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(
            () => sandbox.Execute("throw new Error('boom')"));
    }

    [Fact]
    public void Constructor_CustomLimits()
    {
        var sandbox = new JintSandbox(maxStatements: 100, timeoutMs: 1000, memoryLimitBytes: 1_000_000);
        var result = sandbox.Execute("1 + 1");
        Assert.Equal(2, result!.GetValue<int>());
    }

    [Fact]
    public void JsonToJsValue_Null_ConvertsBack()
    {
        var engine = new Jint.Engine();
        var jsVal = JintSandbox.JsonToJsValue(engine, null);
        // Converting back should give null
        var backToJson = JintSandbox.JsValueToJson(jsVal);
        Assert.Null(backToJson);
    }

    [Fact]
    public void JsonToJsValue_Decimal_ConvertsToNumber()
    {
        var engine = new Jint.Engine();
        var jsValue = JintSandbox.JsonToJsValue(engine, JsonValue.Create(2.5m));
        var roundTrip = JintSandbox.JsValueToJson(jsValue);

        Assert.Equal(2.5, roundTrip!.GetValue<double>());
    }

    public static TheoryData<JsonNode, double> AdditionalNumericValues => new()
    {
        { JsonValue.Create((byte)8)!, 8 },
        { JsonValue.Create((sbyte)-8)!, -8 },
        { JsonValue.Create((short)-16)!, -16 },
        { JsonValue.Create((ushort)16)!, 16 },
        { JsonValue.Create((uint)32)!, 32 },
        { JsonValue.Create((ulong)64)!, 64 }
    };

    [Theory]
    [MemberData(nameof(AdditionalNumericValues))]
    public void JsonToJsValue_AdditionalNumericTypes_ConvertToNumber(JsonNode value, double expected)
    {
        var engine = new Jint.Engine();
        var jsValue = JintSandbox.JsonToJsValue(engine, value);
        var roundTrip = JintSandbox.JsValueToJson(jsValue);

        Assert.Equal(expected, ExpressionEvaluator.GetNumber(roundTrip));
    }

    [Fact]
    public void JsValueToJson_Null_ReturnsNull()
    {
        // Test via round-trip: null JSON → JsValue → JSON
        var engine = new Jint.Engine();
        engine.SetValue("x", Jint.Native.JsValue.Null);
        var result = JintSandbox.JsValueToJson(engine.GetValue("x"));
        Assert.Null(result);
    }

    [Fact]
    public void JsValueToJson_Undefined_ReturnsNull()
    {
        var engine = new Jint.Engine();
        var result = JintSandbox.JsValueToJson(engine.GetValue("notDefined"));
        Assert.Null(result);
    }
}
