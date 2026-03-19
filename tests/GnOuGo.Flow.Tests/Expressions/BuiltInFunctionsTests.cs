using System.Text.Json.Nodes;
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
}

