using System.Text.Json.Nodes;
using System.Globalization;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public class BuiltInFunctionsTests
{
    private readonly ExpressionEvaluator _eval = new();

    private JsonNode? Eval(string expr, JsonNode? ctx = null) =>
        _eval.Evaluate(expr, ctx);

    [Fact]
    public void Exists_NonNull_ReturnsTrue()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["x"] = 1 } };
        var result = Eval("exists(data.inputs.x)", ctx);
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void Exists_Null_ReturnsFalse()
    {
        var result = Eval("exists(null)", null);
        Assert.False(result!.GetValue<bool>());
    }

    [Fact]
    public void Coalesce_FirstNonNull()
    {
        var result = Eval("coalesce(null, \"fallback\")", null);
        Assert.Equal("fallback", result!.GetValue<string>());
    }

    [Fact]
    public void Coalesce_AllNull()
    {
        var result = Eval("coalesce(null, null)", null);
        Assert.Null(result);
    }

    [Fact]
    public void Len_String()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello" } };
        var result = Eval("len(data.inputs.s)", ctx);
        Assert.Equal(5, result!.GetValue<int>());
    }

    [Fact]
    public void Len_Array()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["a"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2)) } };
        var result = Eval("len(data.inputs.a)", ctx);
        Assert.Equal(2, result!.GetValue<int>());
    }

    [Fact]
    public void Lower_String()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "HELLO" } };
        var result = Eval("lower(data.inputs.s)", ctx);
        Assert.Equal("hello", result!.GetValue<string>());
    }

    [Fact]
    public void Upper_String()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello" } };
        var result = Eval("upper(data.inputs.s)", ctx);
        Assert.Equal("HELLO", result!.GetValue<string>());
    }

    [Fact]
    public void Trim_String()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "  hi  " } };
        var result = Eval("trim(data.inputs.s)", ctx);
        Assert.Equal("hi", result!.GetValue<string>());
    }

    [Fact]
    public void Contains_Found_ReturnsTrue()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello world" } };
        var result = Eval("contains(data.inputs.s, \"world\")", ctx);
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void Contains_NotFound_ReturnsFalse()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello" } };
        var result = Eval("contains(data.inputs.s, \"xyz\")", ctx);
        Assert.False(result!.GetValue<bool>());
    }

    [Fact]
    public void StartsWith_True()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello world" } };
        var result = Eval("startsWith(data.inputs.s, \"hello\")", ctx);
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void EndsWith_True()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello world" } };
        var result = Eval("endsWith(data.inputs.s, \"world\")", ctx);
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void Replace_ReplacesOccurrence()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello world" } };
        var result = Eval("replace(data.inputs.s, \"world\", \"there\")", ctx);
        Assert.Equal("hello there", result!.GetValue<string>());
    }

    [Fact]
    public void ToNumber_FromString()
    {
        var result = Eval("toNumber(\"42\")", null);
        Assert.Equal(42, result!.GetValue<int>());
    }

    [Theory]
    [InlineData("string(42)")]
    [InlineData("toString(42)")]
    public void String_ConvertsValueToString(string expression)
    {
        var result = Eval(expression, null);
        Assert.Equal("42", result!.GetValue<string>());
    }

    [Fact]
    public void Json_SerializesValue()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["x"] = "hello" } };
        var result = Eval("json(data.inputs.x)", ctx);
        Assert.Contains("hello", result!.GetValue<string>());
    }

    [Fact]
    public void Json_Null_ReturnsNullString()
    {
        var result = Eval("json(null)", null);
        Assert.Equal("null", result!.GetValue<string>());
    }

    // ── pick / omit ─────────────────────────────────────────────────

    [Fact]
    public void Pick_ReturnsOnlyRequestedKeys()
    {
        var ctx = new JsonObject
        {
            ["inputs"] = new JsonObject
            {
                ["obj"] = new JsonObject
                {
                    ["name"] = "demo",
                    ["secret"] = "redacted",
                    ["nested"] = new JsonObject { ["ok"] = true }
                }
            }
        };

        var result = Eval("pick(data.inputs.obj, 'name', 'nested', 'missing')", ctx) as JsonObject;

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("demo", result["name"]!.GetValue<string>());
        Assert.True(result["nested"]!["ok"]!.GetValue<bool>());
        Assert.False(result.ContainsKey("secret"));
    }

    [Fact]
    public void Pick_AcceptsArrayOfKeys()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["obj"] = new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 } } };

        var result = Eval("pick(data.inputs.obj, ['b', 'c'])", ctx) as JsonObject;

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result["b"]!.GetValue<int>());
        Assert.Equal(3, result["c"]!.GetValue<int>());
    }

    [Fact]
    public void Omit_RemovesRequestedKeys()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["obj"] = new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 } } };

        var result = Eval("omit(data.inputs.obj, 'b', 'missing')", ctx) as JsonObject;

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result["a"]!.GetValue<int>());
        Assert.Equal(3, result["c"]!.GetValue<int>());
        Assert.False(result.ContainsKey("b"));
    }

    [Fact]
    public void Omit_AcceptsArrayOfKeys()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["obj"] = new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 } } };

        var result = Eval("omit(data.inputs.obj, ['a', 'c'])", ctx) as JsonObject;

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(2, result["b"]!.GetValue<int>());
    }

    [Fact]
    public void PickAndOmit_NonObjectInput_ReturnEmptyObject()
    {
        Assert.Empty((JsonObject)Eval("pick(null, 'a')")!);
        Assert.Empty((JsonObject)Eval("omit([1, 2], 'a')")!);
    }

    [Fact]
    public void Now_ReturnsIsoTimestamp()
    {
        var result = Eval("now()");

        Assert.True(DateTimeOffset.TryParse(result!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public void FormatDate_IsoString()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["d"] = "2024-06-15T10:30:00Z" } };
        var result = Eval("formatDate(data.inputs.d, \"yyyy-MM-dd\")", ctx);
        Assert.Equal("2024-06-15", result!.GetValue<string>());
    }

    [Fact]
    public void FormatDate_NullInput_ReturnsNull()
    {
        var result = Eval("formatDate(null, \"yyyy\")", null);
        Assert.Null(result);
    }

    [Fact]
    public void FormatDate_CanFormatCurrentTimestampWithTimeParts()
    {
        var result = Eval("formatDate(now(), \"dd_MM_yyyy_HH_mm_ss\")");

        Assert.True(DateTime.TryParseExact(
            result!.GetValue<string>(),
            "dd_MM_yyyy_HH_mm_ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _));
    }

    [Fact]
    public void Base64_EncodesUtf8String()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["text"] = "# Hello\n\n- Café" } };
        var result = Eval("base64(data.inputs.text)", ctx);

        Assert.Equal("IyBIZWxsbwoKLSBDYWbDqQ==", result!.GetValue<string>());
    }

    [Fact]
    public void Base64_NullInput_ReturnsNull()
    {
        var result = Eval("base64(null)", null);
        Assert.Null(result);
    }

    // ── substring ──────────────────────────────────────────────────

    [Fact]
    public void Substring_FromStart()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "/llm list" } };
        var result = Eval("substring(data.inputs.s, 4)", ctx);
        Assert.Equal(" list", result!.GetValue<string>());
    }

    [Fact]
    public void Substring_WithLength()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello world" } };
        var result = Eval("substring(data.inputs.s, 0, 5)", ctx);
        Assert.Equal("hello", result!.GetValue<string>());
    }

    [Fact]
    public void Substring_StartBeyondLength_ReturnsEmpty()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hi" } };
        var result = Eval("substring(data.inputs.s, 10)", ctx);
        Assert.Equal("", result!.GetValue<string>());
    }

    [Fact]
    public void Substring_LlmCommand_ExtractsRemainder()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "/llm" } };
        var result = Eval("substring(data.inputs.s, 4)", ctx);
        Assert.Equal("", result!.GetValue<string>());
    }

    [Fact]
    public void Substring_McpEditCommand_ExtractsArgument()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "edit Github" } };
        var result = Eval("substring(data.inputs.s, 5)", ctx);
        Assert.Equal("Github", result!.GetValue<string>());
    }

    // ── fromJson ───────────────────────────────────────────────────

    [Fact]
    public void FromJson_ParsesObject()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "{\"name\":\"test\"}" } };
        var result = Eval("fromJson(data.inputs.s)", ctx);
        Assert.NotNull(result);
        Assert.Equal("test", result!["name"]!.GetValue<string>());
    }

    [Fact]
    public void FromJson_ParsesArray()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "[1,2,3]" } };
        var result = Eval("fromJson(data.inputs.s)", ctx);
        Assert.NotNull(result);
        Assert.IsType<JsonArray>(result);
        Assert.Equal(3, result!.AsArray().Count);
    }

    [Fact]
    public void FromJson_InvalidJson_ReturnsNull()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "not json" } };
        var result = Eval("fromJson(data.inputs.s)", ctx);
        Assert.Null(result);
    }

    [Fact]
    public void FromJson_NullInput_ReturnsNull()
    {
        var result = Eval("fromJson(null)", null);
        Assert.Null(result);
    }

    // ── length (alias for len) ─────────────────────────────────────

    [Fact]
    public void Length_IsAliasForLen()
    {
        var ctx = new JsonObject { ["inputs"] = new JsonObject { ["s"] = "hello" } };
        var result = Eval("length(data.inputs.s)", ctx);
        Assert.Equal(5, result!.GetValue<int>());
    }
}
