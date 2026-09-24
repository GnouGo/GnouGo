using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphRevisionTests
{
    [Fact]
    public async Task ConditionalProducerAvailabilityCanBeRepairedWithoutChangingIndependentStages()
    {
        var runtime = new TestRuntime(); var graph = PlannerFixture.Greeting();
        var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "enabled", Schema = new() { Type = "boolean" } });
        workflow.Steps.Add(new() { Key = "conditional", If = PlanningCorpus.Ref("input", "enabled"), Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(2))) });
        workflow.Steps.Add(new() { Key = "consumer", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "conditional", "value"))) });
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var findings = PlanningExecutableValidation.Validate(graph, catalog);
        Assert.Contains(findings, f => f.Rule == "availability:conditional");
        var scope = PlanningGraphRevisions.Scope(graph, findings);
        Assert.Contains("main/conditional", scope); Assert.Contains("main/consumer", scope); Assert.DoesNotContain("main/greet", scope);
    }
    [Fact]
    public async Task MissingInputDefaultsOpenTheInterfaceWithoutUnfreezingIndependentWork()
    {
        var runtime = new TestRuntime();
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Inputs.Add(new() { Name = "increment", Required = false, Schema = new() { Type = "number", Nullable = true } });
        graph.Workflows[0].Steps.Add(new() { Key = "fallback", Type = "number.default", Input = PlanningCorpus.Obj(("left", PlanningCorpus.Ref("input", "increment")), ("right", PlanningCorpus.Num(2))) });
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var findings = PlanningExecutableValidation.Validate(graph, catalog);
        Assert.Contains(findings, f => f.Code == "BINDING_UNAVAILABLE" && f.Rule == "input:increment");
        var scope = PlanningGraphRevisions.Scope(graph, findings);
        Assert.Contains("main/$interface", scope); Assert.Contains("main/fallback", scope); Assert.DoesNotContain("main/greet", scope);
        var revised = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        revised.Workflows[0].Inputs[0].Default = PlanningCorpus.Num(2);
        Assert.Empty(PlanningGraphRevisions.Validate(graph, revised, scope));
        Assert.Empty(PlanningExecutableValidation.Validate(revised, catalog));
    }

    [Fact]
    public void ApprovalWrapperDoesNotRedirectFindingsToAnUnrelatedWorkflow()
    {
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps.Add(new() { Key = "bad", Type = "mcp.call", CapabilityId = "external" });
        graph.Workflows.Add(new() { Key = "unrelated", Steps = [new() { Key = "keep" }] });
        var executable = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        PlanningConfirmationGuards.Apply(executable, new() { Capabilities = [new() { Id = "external", StepType = "mcp.call", EffectKind = "write" }] });
        var diagnostic = PlanningConfirmationGuards.UserDiagnostic(new("INVALID", "/workflows/1/steps/1/input", "Bad input"), executable, graph);
        Assert.Equal("/workflows/0/steps/1/input", diagnostic.Location);
        var scope = PlanningGraphRevisions.Scope(graph, [diagnostic]);
        Assert.Contains("main/bad", scope); Assert.DoesNotContain("main/greet", scope);
        Assert.DoesNotContain(scope, s => s.StartsWith("unrelated/", StringComparison.Ordinal));
    }

    [Fact]
    public void DependentStagesAndCallersAreInvalidatedWhileUnrelatedStagesStayFrozen()
    {
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps =
            [new() { Key = "call", Type = "workflow.call", Input = PlanningCorpus.Obj(("ref", PlanningCorpus.Ref("workflow", "child"))) },
             new() { Key = "consumer", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "call", "value"))) },
             new() { Key = "untouched", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(42))) }] },
            new() { Key = "child", Steps = [new() { Key = "producer", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(1))) }],
                Outputs = [new() { Name = "value", Schema = new() { Type = "number" }, Value = PlanningCorpus.Ref("output", "producer", "value") }] }] };
        var scope = PlanningGraphRevisions.Scope(graph, [new("INVALID", "/workflows/1/steps/0/input", "Invalid value")]);
        Assert.Contains("main/call", scope); Assert.Contains("main/consumer", scope); Assert.Contains("child/producer", scope); Assert.DoesNotContain("main/untouched", scope);
        var replacement = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        replacement.Workflows[0].Steps[2].Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(43)));
        Assert.NotEmpty(PlanningGraphRevisions.Validate(graph, replacement, scope));
        replacement.Workflows[0].Steps[2].Input = graph.Workflows[0].Steps[2].Input;
        replacement.Workflows[1].Steps[0].Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(2)));
        Assert.Empty(PlanningGraphRevisions.Validate(graph, replacement, scope));
    }
    [Fact]
    public async Task RejectedScopeExpansionCannotBecomeNextRepairBaseline()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Graph!.Workflows[0].Steps.Add(new() { Key = "bad", Type = "number.add", Input = PlanningCorpus.Obj(("left", PlanningCorpus.Text("invalid")), ("right", PlanningCorpus.Num(1))) });
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        Assert.Contains("main/bad", state.RevisionScope); Assert.DoesNotContain("main/greet", state.RevisionScope);
        var original = JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph);
        runtime.Proposal.Graph.Workflows[0].Steps[0].Input = PlanningCorpus.Obj(("message", PlanningCorpus.Text("unapproved change")));
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Contains(state.Diagnostics, d => d.Code != "REVISION_SCOPE_CHANGED");
        Assert.Equal(original, JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph)); Assert.Null(state.Yaml);
    }
    [Fact]
    public void StageIdentityPrefixesDoNotExpandRepairScope()
    {
        var graph = new PlanningGraph { Workflows = [new() { Steps = [new() { Key = "work" }, new() { Key = "work_long" }] }] };
        var scope = PlanningGraphRevisions.Scope(graph, [new("INVALID", "/workflows/0/stages/work_long/input", "Bad input")]);
        Assert.Contains("main/work_long", scope); Assert.DoesNotContain("main/work", scope);
    }
    [Theory]
    [InlineData("compute", "6 * 7")]
    [InlineData("expression", "data.inputs.value.map(x => x * 2)")]
    [InlineData("template", "{{secret}}")]
    public async Task GeneratedGlueCannotHideScriptsInsideInputObjects(string kind, string text)
    {
        var runtime = new TestRuntime(); var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = kind; graph.Workflows[0].Steps[0].Input.Members[0].Value.Text = text;
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, catalog), d => d.Code.StartsWith("GENERATED_", StringComparison.Ordinal));
    }
}
