using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed class ClosedExpressionValuesTests
{
    [Theory]
    [InlineData("${input ? 'allow' : 'deny'}", true)]
    [InlineData("${((input) => (() => { const selected = input.ok; return selected ? 'allow' : 'deny'; })())(data.inputs)}", true)]
    [InlineData("${(() => { if (data.inputs.flag) return 'allow'; return 'deny'; })()}", true)]
    [InlineData("${(() => { if (data.inputs.flag) return 'other'; return 'deny'; })()}", false)]
    [InlineData("${(() => { if (data.inputs.flag) return 'allow'; })()}", false)]
    [InlineData("${data.steps.result.choice}", false)]
    [InlineData("prefix ${input ? 'allow' : 'deny'}", false)]
    [InlineData("${input ? 'allow' : 'unknown'}", false)]
    [InlineData("${(async () => 'allow')()}", false)]
    public void FiniteResultsMustAllSatisfyTheDeclaredContract(string expression, bool valid)
        => Assert.Equal(valid, ClosedExpressionValues.AreWithin(expression, [JsonValue.Create("allow")!, JsonValue.Create("deny")!]));
}
