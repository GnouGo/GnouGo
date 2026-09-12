using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputedSelectorTests
{
    [Theory]
    [InlineData("items.length > 0", "selected", "skipped")]
    [InlineData("items.length !== 0", "publier", "ignorer")]
    [InlineData("return items.length > 0;", "selected", "skipped")]
    public void BooleanComputationsCannotMatchNamedOutcomes(string expression, string selected, string skipped)
    {
        var graph = Graph(); graph.Workflows[0].Outputs.Clear();
        var node = new PlanningNode { Key = "routing", Type = "switch", Expr = new() { Kind = "compute", Text = expression,
            Members = [new("items", new() { Kind = "array", Items = [Str("entry")] })] }, Cases = [new(selected, null, []), new(skipped, null, [])] };
        graph.Workflows[0].Steps = [node];
        var findings = PlanningGraphValidation.Validate(graph, Preparation()).Where(d => d.Code == "SWITCH_CASE_UNREACHABLE").ToArray();
        Assert.Equal(2, findings.Length); Assert.All(findings, finding => Assert.Equal("/workflows/0/steps/0/expr", finding.Location));
        node.Expr.Text = "items.length > 0 ? " + System.Text.Json.JsonSerializer.Serialize(selected) + " : " + System.Text.Json.JsonSerializer.Serialize(skipped);
        Assert.DoesNotContain(PlanningGraphValidation.Validate(graph, Preparation()), d => d.Code == "SWITCH_CASE_UNREACHABLE");
        Assert.Equal(new[] { selected, skipped }, node.Cases.Select(c => c.Value));
    }

    [Theory]
    [InlineData("!value", "true", "false")]
    [InlineData("(() => { function unrelated() { return 123; } return flag ? 'yes' : 'no'; })()", "yes", "no")]
    [InlineData("flag ? 'yes' : (() => { return 'no'; })()", "yes", "no")]
    public void FiniteResultsFollowReturnsWithoutExecutingCodeOrInspectingUnrelatedHelpers(string expression, string first, string second)
        => Assert.Equal(new[] { first, second }, PlanningComputations.FiniteOutcomes(expression));

    [Theory]
    [InlineData("value")]
    [InlineData("helper(value)")]
    [InlineData("flag ? 'yes' : helper(value)")]
    [InlineData("(() => { if (flag) return 'yes'; return helper(value); })()")]
    [InlineData("(() => { return 123; })()")]
    [InlineData("invalid syntax ! !")]
    [InlineData("(async () => 'yes')()")]
    [InlineData("(function* () { yield 'yes'; return 'no'; })()")]
    public void UnknownResultsRemainUnknown(string expression) => Assert.Null(PlanningComputations.FiniteOutcomes(expression));
}
