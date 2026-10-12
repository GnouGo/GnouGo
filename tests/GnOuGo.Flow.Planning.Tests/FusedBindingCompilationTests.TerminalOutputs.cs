using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData("verbatim", true, null)]
    [InlineData("", false, "first")]
    [InlineData("observed", false, "guard")]
    public async Task TerminalChecksUseOutputBoundaryWithIdenticalValuesAndFirstFailure(string label, bool authorized, string? failed)
    {
        var graph = Graph(); var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var before = compiler.Compile(graph, catalog, "outputs", true, true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "outputs", true, true, true, true, true, true, true, true, true, true, true);
        SaveV13Comparison("output", before, after);
        Assert.Empty(WorkflowParser.Parse(after).Workflows["main"].Steps);
        Assert.Equal(4, WorkflowParser.Parse(after).Workflows["main"].Outputs!["chosen"].ExpressionContracts![""]!.AsObject().Count);
        string? outcome = null;
        foreach (var yaml in new[] { before, after })
        {
            var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows["main"], new JsonObject { ["label"] = label, ["authorized"] = authorized }, PlannerFixture.Ct);
            Assert.Equal(failed is null, result.Success);
            var actual = result.Success ? result.Outputs!.ToJsonString() : result.Error!.Code + result.Error.Message.Split(" [workflow", StringSplitOptions.None)[0] + result.Error.Details!["location"];
            if (outcome is not null) Assert.Equal(outcome, actual);
            outcome = actual;
        }
    }

    [Theory]
    [InlineData("finalizer")]
    [InlineData("multiple_outputs")]
    [InlineData("retry")]
    [InlineData("handler")]
    public async Task OutputInliningDoesNotCrossIndependentBoundaries(string boundary)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        switch (boundary)
        {
            case "finalizer": workflow.Finally.Add(new() { Key = "cleanup", Input = Obj("closed", new() { Kind = "boolean", Boolean = true }) }); break;
            case "multiple_outputs": workflow.Outputs.Add(new() { Name = "other", Value = Ref("first", "value") }); break;
            case "retry": workflow.Steps[^1].Retry = new() { Max = 2 }; break;
            case "handler": workflow.Steps[^1].OnError.Add(new(null, "stop", null, null)); break;
        }
        var yaml = new PlanningGraphCompiler().Compile(graph, await Catalog(), "retained", true, true, true, true, true, true, true, true, true, true, true);
        if (boundary == "finalizer") SaveV13Comparison("retained-finalizer",
            new PlanningGraphCompiler().Compile(graph, await Catalog(), "retained", true, true, true, true, true, true, true, true, true, true), yaml);
        Assert.NotEmpty(WorkflowParser.Parse(yaml).Workflows["main"].Steps);
        Assert.Null(WorkflowParser.Parse(yaml).Workflows["main"].Outputs!["chosen"].ExpressionContracts);
    }
}
