using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphContractTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("collections")]
    public async Task RegisteredTransformsCompileWithoutScriptInference(string name)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        var graph = PlanningCorpus.Graph(name, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        new PlanningGraphCompiler().Compile(graph, catalog);
    }
}
