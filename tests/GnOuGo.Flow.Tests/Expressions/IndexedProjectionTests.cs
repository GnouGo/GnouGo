using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed class IndexedProjectionTests
{
    private static string Expression(string script) => "checkedMapping(" + JsonValue.Create(script).ToJsonString() + ", data.inputs)";

    [Theory]
    [InlineData("é \u2603", "é \u2603")]
    [InlineData("` ${untrusted} \\ \n", "` ${untrusted} \\ \n")]
    public void ConstantTemplateProgramsPreserveLiteralDataAndMissingNullDistinction(string value, string expected)
    {
        var literal = JsonValue.Create(value).ToJsonString();
        var program = "({copied:source.value,label:" + literal + "})";
        var template = "`\n" + program.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("${", "\\${", StringComparison.Ordinal) + "\n`";
        var evaluator = new ExpressionEvaluator();
        var context = new JsonObject { ["inputs"] = new JsonObject { ["value"] = null } };
        var result = evaluator.Evaluate("checkedMapping(" + template + ",data.inputs)", context)!;
        Assert.Null(result["copied"]); Assert.Equal(expected, result["label"]!.GetValue<string>());
        context["inputs"]!.AsObject().Remove("value");
        Assert.Throws<WorkflowRuntimeException>(() => evaluator.Evaluate("checkedMapping(" + template + ",data.inputs)", context));
        Assert.Throws<WorkflowRuntimeException>(() => evaluator.Evaluate("checkedMapping(`source.${data.secret}`,data.inputs)", context));
    }

    [Fact]
    public void CompilerIndexIsStructuralWhileLearnedIndexIsNotAnObservedBusinessValue()
    {
        const string script = "source.rows.map((entry,position)=>({id:position,value:entry}))";
        var source = JsonNode.Parse("{\"rows\":[null,{\"amount\":7922816251426433759354395033.5},null]}")!;
        var result = new ExpressionEvaluator().Evaluate(Expression(script), new JsonObject { ["inputs"] = source.DeepClone() })!.AsArray();
        Assert.Equal(new[] { 0, 1, 2 }, result.Select(r => r!["id"]!.GetValue<int>()));
        Assert.Equal(source["rows"]![1]!.ToJsonString(), result[1]!["value"]!.ToJsonString());
        Assert.Null(result[0]!["value"]); Assert.Null(result[2]!["value"]);
        var error = Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script, source, TestContext.Current.CancellationToken));
        Assert.Equal("program", error.Details!["mapping_failure_kind"]!.ToString());
    }

    [Fact]
    public void ScopedCallbackIndicesAndMissingSiblingsStayChecked()
    {
        var context = JsonNode.Parse("{\"inputs\":{\"rows\":[[\"a\",\"a\"],[],[null]]}}")!;
        var result = new ExpressionEvaluator().Evaluate(Expression("source.rows.map((row,outer)=>({id:outer,values:row.map((value,inner)=>({id:inner,value:value}))}))"), context)!;
        Assert.Equal(2, result[2]!["id"]!.GetValue<int>()); Assert.Equal(0, result[2]!["values"]![0]!["id"]!.GetValue<int>());
        Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().Evaluate(
            Expression("source.rows.map((row,index)=>({valid:index,invalid:row.missing}))"), context));
    }
}
