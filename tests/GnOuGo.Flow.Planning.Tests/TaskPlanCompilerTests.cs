using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Planning.Examples;
using System.Text.Json;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanCompilerTests
{
    public static IEnumerable<object[]> Scenarios => PlanningBenchmarkCases.Names.Select(n => new object[] { n });
    [Theory, MemberData(nameof(Scenarios))]
    public async Task BusinessScenariosCompileDeterministicallyAndPreserveIndependentOutcomes(string name)
    {
        var sample = new PlanningBenchmarkCases.Environment(name);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = sample.Factory() }, (_, _) => Task.CompletedTask);
        var catalog = await Catalog(runtime);
        var plan = PlanningCorpus.Tasks(name, catalog);
        var first = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(first.Diagnostics);
        Assert.NotNull(first.Graph);
        var second = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Equal(JsonSerializer.Serialize(first.Graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.Serialize(second.Graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.Empty(PlanningGeneratedGraph.Validate(first.Graph, catalog));
        PlanningConfirmationGuards.Apply(first.Graph, catalog);
        var findings = PlanningExecutableValidation.Validate(first.Graph, catalog);
        Assert.True(findings.Count == 0, JsonSerializer.Serialize(findings.ToList(), PlanningJsonContext.Default.ListPlanningDiagnostic));
        var yaml = new PlanningGraphCompiler().Compile(first.Graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(first.Graph)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, "nominal"), PlannerFixture.Ct);
        Assert.True(sample.Verify(result), result.Error?.Message);
        Assert.Empty(sample.Violations);
    }
    [Fact]
    public async Task IterationRejectsAnOversizedCollectionBeforeAnyBodyRuns()
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = PlanningCorpus.Tasks("collections", catalog); plan.Root.Tasks[0].MaxItems = 1;
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Contains("maxItems", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new System.Text.Json.Nodes.JsonObject { ["values"] = new System.Text.Json.Nodes.JsonArray(1, 2) }, PlannerFixture.Ct);
        Assert.False(result.Success); Assert.NotNull(result.Error); Assert.Null(result.Outputs);
    }

    internal static async Task<PlanningCatalog> Catalog(WorkflowPlanningRuntime runtime)
    {
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(PlannerFixture.Ct))
        {
            string? cursor = null;
            do
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, cursor, PlannerFixture.Ct);
                foreach (var capability in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, PlannerFixture.Ct));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return catalog;
    }
}
