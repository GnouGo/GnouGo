using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public class StringInterpolatorTests
{
    private readonly StringInterpolator _interpolator = new(new ExpressionEvaluator());

    private static JsonObject MakeCtx(JsonNode? inputs = null) =>
        new() { ["inputs"] = inputs ?? new JsonObject(), ["steps"] = new JsonObject(), ["env"] = new JsonObject() };

    [Fact]
    public void HasExpressions_WithExpression_ReturnsTrue()
    {
        Assert.True(StringInterpolator.HasExpressions("${data.inputs.x}"));
    }

    [Fact]
    public void HasExpressions_WithoutExpression_ReturnsFalse()
    {
        Assert.False(StringInterpolator.HasExpressions("plain text"));
    }

    [Fact]
    public void HasExpressions_Null_ReturnsFalse()
    {
        Assert.False(StringInterpolator.HasExpressions(null));
    }

    [Fact]
    public void Interpolate_SingleExpression_ReturnsTypedResult()
    {
        var ctx = MakeCtx(new JsonObject { ["x"] = 42 });
        var result = _interpolator.Interpolate("${data.inputs.x}", ctx);
        Assert.Equal(42, result!.GetValue<int>());
    }

    [Fact]
    public void Interpolate_SingleExpressionWithDecodedNewlineInStringLiteral_ReturnsString()
    {
        var result = _interpolator.Interpolate("${'row' + '\n'}", MakeCtx());
        Assert.Equal("row\n", result!.GetValue<string>());
    }

    [Fact]
    public void Interpolate_SingleExpressionArray_ReturnsArray()
    {
        var ctx = MakeCtx(new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2)) });
        var result = _interpolator.Interpolate("${data.inputs.items}", ctx);
        Assert.IsType<JsonArray>(result);
    }

    [Fact]
    public void Interpolate_EmbeddedExpression_ReturnsString()
    {
        var ctx = MakeCtx(new JsonObject { ["name"] = "World" });
        var result = _interpolator.Interpolate("Hello ${data.inputs.name}!", ctx);
        Assert.Equal("Hello World!", result!.GetValue<string>());
    }

    [Fact]
    public void Interpolate_MultipleExpressions_ReturnsString()
    {
        var ctx = MakeCtx(new JsonObject { ["a"] = "foo", ["b"] = "bar" });
        var result = _interpolator.Interpolate("${data.inputs.a}-${data.inputs.b}", ctx);
        Assert.Equal("foo-bar", result!.GetValue<string>());
    }

    [Fact]
    public void Interpolate_NullExpression_ReturnsEmptyInString()
    {
        var ctx = MakeCtx();
        var result = _interpolator.Interpolate("Value: ${data.inputs.missing}", ctx);
        Assert.Equal("Value: ", result!.GetValue<string>());
    }

    [Fact]
    public void ResolveDeep_ResolvesNestedObject()
    {
        var ctx = MakeCtx(new JsonObject { ["x"] = 5 });
        var input = new JsonObject
        {
            ["a"] = "${data.inputs.x}",
            ["b"] = "plain"
        };
        var result = _interpolator.ResolveDeep(input, ctx) as JsonObject;
        Assert.NotNull(result);
        Assert.Equal(5, result!["a"]!.GetValue<int>());
        Assert.Equal("plain", result["b"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveDeep_ResolvesArray()
    {
        var ctx = MakeCtx(new JsonObject { ["x"] = "hello" });
        var input = new JsonArray(JsonValue.Create("${data.inputs.x}"));
        var result = _interpolator.ResolveDeep(input, ctx) as JsonArray;
        Assert.NotNull(result);
        Assert.Equal("hello", result![0]!.GetValue<string>());
    }

    [Fact]
    public void ResolveDeep_ClonesWholeExpressionArrayResult()
    {
        var originalItems = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b"));
        var ctx = MakeCtx(new JsonObject { ["items"] = originalItems });
        var input = new JsonObject
        {
            ["args"] = new JsonObject
            {
                ["items"] = "${data.inputs.items}"
            }
        };

        var result = _interpolator.ResolveDeep(input, ctx) as JsonObject;

        Assert.NotNull(result);
        var args = Assert.IsType<JsonObject>(result!["args"]);
        var copiedItems = Assert.IsType<JsonArray>(args["items"]);
        Assert.NotSame(originalItems, copiedItems);
        Assert.Equal("a", copiedItems[0]!.GetValue<string>());
        Assert.Equal("b", copiedItems[1]!.GetValue<string>());
    }

    [Fact]
    public void ResolveDeep_ClonesWholeExpressionObjectResult()
    {
        var originalIssue = new JsonObject
        {
            ["title"] = "Bug",
            ["body"] = "Details"
        };
        var ctx = MakeCtx(new JsonObject { ["issue"] = originalIssue });
        var input = new JsonObject
        {
            ["issue"] = "${data.inputs.issue}"
        };

        var result = _interpolator.ResolveDeep(input, ctx) as JsonObject;

        Assert.NotNull(result);
        var copiedIssue = Assert.IsType<JsonObject>(result!["issue"]);
        Assert.NotSame(originalIssue, copiedIssue);
        Assert.Equal("Bug", copiedIssue["title"]!.GetValue<string>());
        Assert.Equal("Details", copiedIssue["body"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveDeep_Null_ReturnsNull()
    {
        Assert.Null(_interpolator.ResolveDeep(null, null));
    }

    [Fact]
    public void ResolveDeep_NonExpressionValue_ClonesAsIs()
    {
        var input = JsonValue.Create(42);
        var result = _interpolator.ResolveDeep(input, null);
        Assert.Equal(42, result!.GetValue<int>());
    }
}
