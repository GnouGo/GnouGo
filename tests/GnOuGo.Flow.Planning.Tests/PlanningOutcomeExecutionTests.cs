using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningOutcomeExecutionTests
{
    [Theory]
    [InlineData("nominal")]
    [InlineData("alternate")]
    [InlineData("failure")]
    [InlineData("denied")]
    public async Task GeneratedWorkflowPerformsObservedFileEffectsAndAlwaysCleansUp(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = Path.Combine(Path.GetTempPath(), "gnougo-outcomes-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(parent, "work"); var effects = new List<string>();
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        var path = JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"}},"required":["location"],"additionalProperties":false}""")!;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("local_adapter", new() { Tools = [
            new() { Name = "alpha", InputSchema = empty, OutputSchema = path, EffectKind = "write" },
            new() { Name = "beta", InputSchema = path, OutputSchema = empty, EffectKind = "execute" },
            new() { Name = "gamma", InputSchema = path, OutputSchema = empty, EffectKind = "lifecycle" }], ToolHandlers = new()
        {
            ["alpha"] = _ =>
            {
                Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "seed.txt"), "exact seeded contents");
                effects.Add("created"); return new() { Content = new JsonObject { ["location"] = directory } };
            },
            ["beta"] = input =>
            {
                Assert.Equal(directory, input!["location"]!.ToString());
                Assert.Equal("exact seeded contents", File.ReadAllText(Path.Combine(directory, "seed.txt")));
                File.Copy(Path.Combine(directory, "seed.txt"), Path.Combine(directory, "observed.txt"));
                Assert.Equal("exact seeded contents", File.ReadAllText(Path.Combine(directory, "observed.txt")));
                effects.Add("copied-and-verified");
                if (variant == "failure") throw new IOException("Deterministic failure after a partial effect");
                return new() { Content = new JsonObject() };
            },
            ["gamma"] = input =>
            {
                Assert.Equal(directory, input!["location"]!.ToString()); Directory.Delete(directory, true);
                effects.Add("deleted"); return new() { Content = new JsonObject() };
            }
        } });
        try
        {
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(variant != "denied") };
            var runtime = new TestRuntime(engine); var state = PlannerFixture.Session(); state.OutcomeVersion = 1; state.IntentVersion = 1;
            state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
            PlanTask Op(string id, string method, bool consumes) => new() { Id = id, Objective = "Perform declared local work",
                Operation = TaskOperations.Describe(state.Catalog.Capabilities.Single(c => c.Method == method)).Id,
                Inputs = consumes ? [new("location", PlanningCorpus.Business("output", "acquire", "location"))] : [] };
            var plan = new TaskPlan
            {
                Inputs = [new() { Name = "enabled", Type = new() { Kind = "boolean" } }],
                Root = new() { Tasks = [Op("acquire", "alpha", false), new() { Id = "conditional", Kind = "conditional", Objective = "Run optional work",
                    Condition = PlanningCorpus.Business("input", "enabled"), Body = new() { Tasks = [Op("work", "beta", true)] }, Otherwise = new() }],
                    Always = [Op("cleanup", "gamma", true)], Outputs = [new("result", PlanningCorpus.String("completed"))] }
            };
            runtime.Proposal = new() { Plan = plan, Requirements = new() { Summary = "Create, conditionally use and always remove a workspace", Inputs = plan.Inputs,
                Outcomes = [PlanningOutcomeTests.Outcome("create"), PlanningOutcomeTests.Outcome("use", "execute", conditional: true), PlanningOutcomeTests.Outcome("clean", "lifecycle", always: true)] },
                OutcomeBindings = [new("create", ["acquire"], []), new("use", ["work"], []), new("clean", ["cleanup"], [])] };
            state = await PlannerFixture.RunAsync(runtime, state);
            Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
            PlanningArtifactApproval.Verify(state);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["enabled"] = variant != "alternate" }, ct);
            Assert.Equal(variant is "nominal" or "alternate", result.Success);
            Assert.False(Directory.Exists(directory));
            Assert.Equal(variant == "denied" ? [] : variant == "alternate" ? ["created", "deleted"] : new[] { "created", "copied-and-verified", "deleted" }, effects);
            Assert.Single(runtime.Calls); // Execution never uses a model in this fixture.
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }
}
