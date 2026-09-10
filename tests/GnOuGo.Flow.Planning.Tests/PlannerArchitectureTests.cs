using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlannerArchitectureTests
{
    [Fact]
    public void CoreHasNoGnOuGoDependencyAndPlanningDependsOnlyOnCore()
    {
        Assert.DoesNotContain(typeof(IWorkflowPlanner).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("GnOuGo.", StringComparison.Ordinal));
        Assert.Equal(["GnOuGo.Flow.Core"], typeof(TypedWorkflowPlanner).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("GnOuGo.", StringComparison.Ordinal)).Select(a => a.Name));
        Assert.Single(typeof(TypedWorkflowPlanner).Assembly.GetTypes(), t => !t.IsAbstract && typeof(IWorkflowPlanner).IsAssignableFrom(t));
    }

    [Fact]
    public void PlannerInputExposesOnlyTheSingleRequiredArchitecture()
    {
        var contract = new WorkflowEngine().Registry.GetContracts()["workflow.plan"];
        var properties = contract.InputSchema!["properties"]!.AsObject();
        Assert.Equal(new[] { "capability_preflight", "generator", "intent_clarification", "limits", "llm_budget", "max_concurrency", "max_repairs_per_workflow_gate", "name", "policy", "raw_prompt" }, properties.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Contains("raw_prompt", contract.InputSchema["required"]!.AsArray().Select(v => v!.ToString()));
        Assert.Contains("generator", contract.InputSchema["required"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal(4, new PlanningSnapshot().SchemaVersion);
        Assert.DoesNotContain(typeof(PlanningRequest).GetProperties(), p => p.Name.Contains("Strategy", StringComparison.Ordinal) || p.Name.Contains("Version", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IPlanningRuntime).GetMethods(), m => !m.IsAbstract);
    }

    [Fact]
    public async Task MissingPlannerInjectionFailsBeforeModelDispatch()
    {
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      raw_prompt: Return a greeting
                      generator: { model: test }
            """);
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("injected IWorkflowPlanner", result.Error!.Message);
    }

    [Fact]
    public async Task WorkflowPlanExecutesTheInjectedPlannerAndRequiresBothHumanReviews()
    {
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      raw_prompt: Return a greeting
                      generator: { model: test }
                  - id: execute
                    type: workflow.execute
                    input: { from_step: plan }
                outputs:
                  message: "${data.steps.execute.outputs.message}"
            """);
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(document);
        var human = new Reviews(); var runtime = new TypedPlannerTests.FakeRuntime();
        var engine = new WorkflowEngine { WorkflowPlanner = new TypedWorkflowPlanner(), PlanningRuntimeFactory = new RuntimeFactory(runtime), HumanInputProvider = human };
        var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs!["message"]!.ToString());
        Assert.Equal(["accept_behavior", "approve"], human.Decisions);
        Assert.All(runtime.Requests, r => { Assert.NotNull(r.ClientRequestId); Assert.NotNull(r.StructuredOutputSchema); Assert.True(r.StructuredOutputStrict); });
    }

    internal sealed class RuntimeFactory(IPlanningRuntime? runtime = null) : IPlanningRuntimeFactory
    {
        public Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSnapshot initial, CancellationToken ct)
            => Task.FromResult<IPlanningRuntimeSession>(new Session(initial, runtime ?? new WorkflowPlanningRuntime(context, context.Engine.LLMClient!, (_, _) => Task.CompletedTask)));
        private sealed record Session(PlanningSnapshot Snapshot, IPlanningRuntime Runtime) : IPlanningRuntimeSession
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Reviews : IHumanInputProvider
    {
        public List<string> Decisions { get; } = [];
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            var decision = request.Choices!.Contains("accept_behavior") ? "accept_behavior" : "approve";
            Assert.Contains(decision, request.Choices); Decisions.Add(decision);
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = decision });
        }
    }

    [Fact]
    public void HoleTransportHasNoWorkflowYamlOrAuthoringSourceContract()
    {
        var state = TypedPlannerTests.Session(); state.Preparation = TypedPlannerTests.Preparation(); state.BehaviorPlan = TypedPlannerTests.BehaviorPlan();
        PlanningGraphSkeleton.Create(state); PlanningDataflowResolver.Resolve(state);
        var request = PlanningHoleRequests.Create(state, state.Graph!.Workflows[0], state.Construction.Holes.Where(h => h.Kind == "schema").ToArray());
        Assert.Equal(new[] { "assignments" }, request.Schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("\"workflow\"", request.Schema.ToJsonString());
        Assert.DoesNotContain("\"node\"", request.Schema.ToJsonString());
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, strict: true));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningFixtures.Workflow(TypedPlannerTests.FakeRuntime.ExecutableWorkflow()), request.Schema));
    }
}
