using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    [Fact]
    public void ContainerAliasProjectionDoesNotRepeatedlyMaterializeFreshParents()
    {
        const string program = "(result => Object.fromEntries(Object.entries(result).map(entry => entry[0] === 'physical' ? ['logical', entry[1]] : entry)))(data.steps.result)";
        var payload = new JsonObject { ["text"] = new string('x', 1_300_000), ["exact"] = JsonNode.Parse("[9007199254740993.00,null,1.23000000000000000000001]") };
        var context = new JsonObject { ["steps"] = new JsonObject { ["result"] = new JsonObject {
            ["physical"] = payload, ["untouched"] = payload.DeepClone() } } };
        var before = context.ToJsonString();
        var allowance = new JintSandbox.MappingAllowance(1_000_000, TimeSpan.FromSeconds(15), 1_000_000_000);
        var result = new ExpressionEvaluator(null, 1_000_000, TimeSpan.FromSeconds(15), 1_000_000_000)
            .EvaluateCheckedExpression(program, context, new(), allowance)!.AsObject();
        Assert.Equal(before, context.ToJsonString());
        Assert.False(result.ContainsKey("physical"));
        Assert.True(JsonNode.DeepEquals(payload, result["logical"]));
        Assert.True(JsonNode.DeepEquals(payload, result["untouched"]));
        Assert.InRange(allowance.Snapshot()["materialized_bytes"]!.GetValue<long>(), 1, 50_000_000);
        result["logical"]!["text"] = "changed";
        Assert.Equal(before, context.ToJsonString());
        Assert.Equal(1_300_000, result["untouched"]!["text"]!.GetValue<string>().Length);
    }

    [Fact]
    public void FreshLocalAliasesAreCopiedAtEachAdditionalUse()
    {
        var context = JsonNode.Parse("""{"inputs":{"values":[9007199254740993.00,null,1.23000000000000000000001]}}""")!;
        var result = new ExpressionEvaluator().EvaluateCheckedExpression(
            "(()=>{const shared={values:data.inputs.values};return {first:shared,second:shared,nested:[shared]};})()",
            context, new())!;
        Assert.Equal(context["inputs"]!["values"]!.ToJsonString(), result["first"]!["values"]!.ToJsonString());
        result["first"]!["values"]![0] = false;
        Assert.Equal("9007199254740993.00", result["second"]!["values"]![0]!.ToJsonString());
        Assert.Equal("9007199254740993.00", result["nested"]![0]!["values"]![0]!.ToJsonString());
        Assert.Equal("9007199254740993.00", context["inputs"]!["values"]![0]!.ToJsonString());
    }

    [Fact]
    public void MovingFreshContainersStillChecksTheAssembledOutputDepth()
    {
        var program = "data.value";
        for (var depth = 0; depth < 66; depth++) program = "{nested:" + program + "}";
        var error = Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator()
            .EvaluateCheckedExpression("(" + program + ")", new JsonObject { ["value"] = true }, new()));
        Assert.Equal("nesting", error.Details!["exhausted_resource"]!.ToString());
    }

    [Fact]
    public void RepeatedCopiesStillExhaustTheSameNestedCeiling()
    {
        var data = new JsonObject { ["payload"] = new string('x', 1_200_000) };
        var program = "[" + string.Join(',', Enumerable.Repeat("data.payload", 12)) + "]";
        var error = Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator(null, 1_000_000,
            TimeSpan.FromSeconds(15), 1_000_000_000).EvaluateCheckedExpression(program, data, new()));
        Assert.Equal("materialized_memory", error.Details!["exhausted_resource"]!.ToString());
        Assert.Equal(50_000_000, error.Details["sandbox"]!["memory_limit_bytes"]!.GetValue<long>());
    }
}
