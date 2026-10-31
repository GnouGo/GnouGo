using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AuthoredCompositionCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanYamlKeepsExplicitInferenceAndConditionalExecution(bool allowed)
    {
        // Generation advice must not become a runtime prohibition on authored YAML.
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                inputs:
                  allowed: {type: boolean}
                  report: {type: string}
                steps:
                  - id: deliberate_interpretation
                    type: llm.call
                    if: '${data.inputs.allowed}'
                    input:
                      model: deterministic
                      prompt: '${data.inputs.report}'
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            assessment: {type: string}
                          required: [assessment]
                          additionalProperties: false
                        strict: true
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """));
        var model = new AuthoredDecision();
        var result = await new WorkflowEngine { LLMClient = model }.ExecuteAsync(document.Workflows["main"],
            new JsonObject { ["allowed"] = allowed, ["report"] = "The complete report intentionally supplied by its author." }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(allowed ? 1 : 0, model.Calls);
        Assert.Contains(result.StepResults, r => r.Output?["closed"]?.GetValue<bool>() == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeAssertionsFailSequentiallyButSucceedOnSeparateConditionalPaths(bool available)
    {
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        TaskValue Present() => new() { Kind = "input", Source = "available" };
        TaskValue Absent() => new() { Kind = "predicate", Predicate = "not", Items = [Present()] };
        PlanTask AssertionTask(string id, TaskValue assertion) => new()
        {
            Id = id, Kind = "value", Objective = "Assert on the affected path", Requires = assertion,
            Outputs = [new("checked", new() { Kind = "boolean", Boolean = true })]
        };
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "available", Type = new() { Kind = "boolean" } }],
            Root = new() { Tasks = [AssertionTask("present", Present()), AssertionTask("absent", Absent())] }
        };
        async Task<bool> Run()
        {
            var compiled = new TaskPlanCompiler().Compile(plan, catalog, PlannerFixture.Session().Request);
            Assert.Empty(compiled.Diagnostics);
            var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "assertions",
                descriptions: true, fuseBindings: true, consumerBindings: true, directProjections: true, nativeMappings: true,
                readableMappings: true, leanMappings: true, compactProjections: true, inlineConsumers: true, directValues: true,
                checkedControls: true, readableBranches: true, simplifiedLocals: true);
            var result = await LookupCompilationTests.Execute(yaml, new() { ["available"] = available }, engine);
            if (!result.Success) Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
            return result.Success;
        }
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog, PlannerFixture.Session().Request).Diagnostics, diagnostic => diagnostic.Code == "TASK_GUARD_CONTRADICTION");
        var original = plan.Root.Tasks;
        plan.Root.Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Route optional work explicitly", Condition = Present(),
            Body = new() { Tasks = [original[0]] }, Otherwise = new() { Tasks = [original[1]] } }];
        Assert.True(await Run());
    }

    private sealed class AuthoredDecision : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("The complete report intentionally supplied by its author.", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["assessment"] = "Deterministic fixture decision" } });
        }
    }
}
