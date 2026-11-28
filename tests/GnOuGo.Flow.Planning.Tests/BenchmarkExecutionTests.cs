using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class BenchmarkExecutionTests
{
    public static TheoryData<string> Cases => new(PlanningBenchmarkCases.Names);
    [Theory, MemberData(nameof(Cases))]
    public async Task FrozenTaskPlanExecutesAgainstIndependentObservations(string name)
    {
        var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory() };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var compilation = new TaskPlanCompiler().Compile(PlanningCorpus.Tasks(name, catalog), catalog);
        Assert.Empty(compilation.Diagnostics);
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var variants = name.StartsWith("review_", StringComparison.Ordinal) ? new[] { "nominal", "failure", "incomplete", "rejected", "head_changed" } : name == "protected_cleanup" ? ["nominal", "failure"] : ["nominal", "alternate"];
        foreach (var variant in variants)
        {
            var sample = new PlanningBenchmarkCases.Environment(name, variant); var runner = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
            var result = await runner.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, variant), TestContext.Current.CancellationToken);
            Assert.True(sample.Verify(result), variant + ": " + result.Error?.Message + "; " + string.Join(",", sample.Effects) + "; " + string.Join(",", sample.Violations));
        }
    }
}
