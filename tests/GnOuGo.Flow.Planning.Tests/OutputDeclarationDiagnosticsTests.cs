using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutputDeclarationDiagnosticsTests
{
    [Theory]
    [InlineData("", "blank")]
    [InlineData("__reserved", "reserved")]
    [InlineData("résumé/},{_技术", "duplicate")]
    public void FreshDiagnosticsIdentifyInvalidOccurrencesWithoutSanitizingNames(string name, string reason)
    {
        var plan = Plan(name, reason == "duplicate");
        var result = new TaskPlanCompiler().Compile(plan, new(), Request());
        var findings = result.Diagnostics.Where(d => d.Code == "TASK_IDENTITY_INVALID").ToArray();
        Assert.Contains(findings, d => d.Location == "/tasks/assemble/outputs/0/name" && d.Rule == reason);
        if (reason == "duplicate") Assert.Contains(findings, d => d.Location == "/tasks/assemble/outputs/1/name" && d.Rule == reason);
        Assert.DoesNotContain(findings, d => d.Message.Contains("private-result", StringComparison.Ordinal));
        Assert.Null(result.Graph);
    }


    [Fact]
    public void NamesAndDiagnosticCountsAreBoundedWithoutExposingValues()
    {
        var plan = Plan("__" + new string('x', 4000), false);
        plan.Root.Tasks[0].Outputs = Enumerable.Range(0, 100).Select(_ => plan.Root.Tasks[0].Outputs[0]).ToList();
        var result = new TaskPlanCompiler().Compile(plan, new(), Request());
        var findings = result.Diagnostics.Where(d => d.Code == "TASK_IDENTITY_INVALID").ToArray();
        Assert.InRange(findings.Length, 1, 17);
        Assert.All(findings, d => Assert.InRange(d.Message.Length, 1, 512));
        Assert.DoesNotContain(findings, d => d.Message.Contains(new string('x', 100), StringComparison.Ordinal));
    }

    [Fact]
    public void ScopeOutputDeclarationsHaveOccurrenceLocations()
    {
        var plan = Plan("value", false);
        plan.Root.Outputs = [new("same", Text()), new("same", Text())];
        var result = new TaskPlanCompiler().Compile(plan, new(), Request());
        Assert.Contains(result.Diagnostics, d => d.Location == "/root/outputs/0/name" && d.Rule == "duplicate");
        Assert.Contains(result.Diagnostics, d => d.Location == "/root/outputs/1/name" && d.Rule == "duplicate");
    }

    [Fact]
    public async Task LiteralNamesProduceIdenticalV15GraphsAndYaml()
    {
        const string name = "résumé/},{_技术";
        var plan = Plan(name, false);
        plan.Root.Outputs = [new(name, new() { Kind = "output", Source = "assemble", Port = name })];
        var historical = new PlanningRequest { Options = new JsonObject { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var fresh = Request(); fresh.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        var catalog = await TaskPlanCompilerTests.Catalog(new TestRuntime().Actual);
        var oldGraph = new TaskPlanCompiler().Compile(plan, catalog, historical);
        var newGraph = new TaskPlanCompiler().Compile(plan, catalog, fresh);
        Assert.Empty(oldGraph.Diagnostics); Assert.Empty(newGraph.Diagnostics);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(oldGraph.Graph, PlanningJsonContext.Default.PlanningGraph),
            System.Text.Json.JsonSerializer.Serialize(newGraph.Graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.Equal(Yaml(oldGraph), Yaml(newGraph));
        string Yaml(TaskCompilation compilation) => new PlanningGraphCompiler().Compile(compilation.Graph!, catalog,
            name: "generated", descriptions: true, fuseBindings: true, consumerBindings: true, directProjections: true, nativeMappings: true,
            readableMappings: true, leanMappings: true, compactProjections: true, inlineConsumers: true, directValues: true,
            checkedControls: true, readableBranches: true, simplifiedLocals: true);
    }

    private static PlanningRequest Request() => new() { Options = new JsonObject { ["planning_diagnostics_version"] = 1 } };
    private static TaskPlan Plan(string name, bool duplicate) => new() { Root = new() { Tasks = [new()
    {
        Id = "assemble", Kind = "value", Objective = "Assemble declared values", Outputs = duplicate
            ? [new(name, Text()), new(name, Text())] : [new(name, Text())]
    }] } };
    private static TaskValue Text() => new() { Kind = "string", Text = "private-result" };
}
