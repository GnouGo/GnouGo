using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using Xunit;
namespace GnOuGo.Flow.Tests.Expressions;
public sealed class ArtifactCollectionExpressionTests
{
    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("[{\"payload\":\"[{\\\"id\\\":9007199254740993}]\"},{\"payload\":\"[null,2]\"}]", "[{\"id\":9007199254740993},null,2]")]
    public void CollectionPreservesExactRecords(string rows, string expected)
    {
        var expression = ArtifactCollectionExpression.Build("pages", ["payload"]);
        Assert.True(ArtifactCollectionExpression.TryRead(expression, out var loop, out var path)); Assert.Equal("pages", loop); Assert.Equal("payload", Assert.Single(path));
        var value = new ExpressionEvaluator().Evaluate(expression, new JsonObject { ["steps"] = new JsonObject { ["pages"] = new JsonObject { ["results"] = JsonNode.Parse(rows) } } });
        Assert.Equal(expected, value!.GetValue<string>());
    }
    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"payload\":null}]")]
    [InlineData("[{\"payload\":\"{}\"}]")]
    [InlineData("[{\"payload\":\"invalid\"}]")]
    public void MissingOrMalformedOriginalResultsFailClosed(string rows)
        => Assert.ThrowsAny<Exception>(() => new ExpressionEvaluator().Evaluate(ArtifactCollectionExpression.Build("pages", ["payload"]),
            new JsonObject { ["steps"] = new JsonObject { ["pages"] = new JsonObject { ["results"] = JsonNode.Parse(rows) } } }));
    [Theory]
    [InlineData("collect_json_arrays(data.steps.pages.results.map(x => x),['payload'])")]
    [InlineData("collect_json_arrays(data.steps.pages.results,[]) ")]
    [InlineData("collect_json_arrays(data.steps.pages.results,[data.inputs.path])")]
    public void TransformsCannotMasqueradeAsOriginalProjections(string expression)
        => Assert.False(ArtifactCollectionExpression.TryRead(expression, out _, out _));
    [Fact]
    public void PrimitiveCannotBeOverriddenByWorkflowFunctions()
        => Assert.Throws<ArgumentException>(() => new ExpressionEvaluator(new() { [ArtifactCollectionExpression.FunctionName] = _ => JsonValue.Create("fabricated") }));
}
