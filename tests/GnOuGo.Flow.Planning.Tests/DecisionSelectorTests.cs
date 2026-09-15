using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionSelectorTests
{
    [Theory]
    [InlineData("KNOWN_NON_EMPTY", "KNOWN")]
    [InlineData("CONNU_NON_VIDE", "CONNU")]
    public void DeclaredSelectorOutcomesMustReachAcceptedCases(string actual, string accepted)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        var producer = workflow.Steps[0]; producer.Input = Str(actual);
        producer.OutputSchema = new() { Type = "string", Enum = [actual, "uncertain"] };
        var decision = new PlanningNode { Key = "route", Type = "switch", Expr = new() { Kind = "output", Source = producer.Key },
            Cases = [new(accepted, null, [new() { Key = "selected", Type = "set", Input = Str("selected") }])], Default = [] };
        workflow.Steps.Add(decision);
        var finding = Assert.Single(PlanningGraphValidation.Validate(graph, Preparation()));
        Assert.Equal("SWITCH_CASE_UNREACHABLE", finding.Code); Assert.Equal("/workflows/0/steps/1/expr", finding.Location);
        Assert.Contains(actual, finding.Message); Assert.Contains(accepted, finding.Message);
        Assert.Equal(accepted, decision.Cases[0].Value); Assert.Empty(decision.Default);
        // A schema without a finite enum cannot establish an unreachable case.
        producer.OutputSchema.Enum.Clear(); Assert.Empty(PlanningGraphValidation.Validate(graph, Preparation()));
        producer.OutputSchema.Enum = [accepted, "uncertain"]; producer.Input = Str(accepted);
        Assert.Empty(PlanningGraphValidation.Validate(graph, Preparation()));
    }
}
