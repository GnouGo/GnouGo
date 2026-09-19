using System.Text.Json;
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
    public async Task FrozenIntentExecutesAgainstIndependentObservations(string name)
    {
        var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory() };
        var runtime = new PlanningCorpus.Runtime(name, engine); var planner = new TypedWorkflowPlanner();
        var state = new PlanningSession { Request = new() { TenantId = "benchmark", Prompt = PlanningBenchmarkCases.Prompt(name) } };
        state = await planner.AdvanceAsync(state, new(), runtime, TestContext.Current.CancellationToken);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)); Assert.Equal(1, state.ModelCalls);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var variants = name.StartsWith("review_", StringComparison.Ordinal) ? new[] { "nominal", "failure", "incomplete", "rejected", "head_changed" } : name == "protected_cleanup" ? ["nominal", "failure"] : ["nominal", "alternate"];
        foreach (var variant in variants)
        {
            var sample = new PlanningBenchmarkCases.Environment(name, variant); var runner = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
            var result = await runner.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, variant), TestContext.Current.CancellationToken);
            Assert.True(sample.Verify(result), variant + ": " + result.Error?.Message + "; " + string.Join(",", sample.Effects) + "; " + string.Join(",", sample.Violations));
        }
    }
}
