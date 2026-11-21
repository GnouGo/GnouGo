using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArrayBindingContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedArraysCopyProjectedContractsAndPreserveRepeatedValues(bool incompatible)
    {
        var calls = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new()
        {
            Tools = [new() { Name = "consume", InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}}},"required":["rows"],"additionalProperties":false}
                """)!.AsObject(), OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }],
            ToolHandlers = new() { ["consume"] = input =>
            {
                calls++;
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[["B","A","B"]]"""), input!["rows"]));
                return new() { Content = new JsonObject() };
            } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "producer", Kind = "value", Objective = "Retain exact values", Outputs = [
                new("first", new() { Kind = "string", Text = "A" }),
                new("second", incompatible ? new() { Kind = "number", Number = 7 } : new() { Kind = "string", Text = "B" })] },
            new() { Id = "consumer", Kind = "operation", Objective = "Consume the ordered rows", Operation = catalog.Capabilities.Single(c => c.Method == "consume").Id,
                Inputs = [new("rows", new() { Kind = "array", Items = [new() { Kind = "array", Items = [Reference("second"), Reference("first"), Reference("second")] }] })] }
        ] } };
        var request = new PlanningRequest { Policy = new() { RequireExternalConfirmation = false }, Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var originalPlan = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var originalCatalog = JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        if (incompatible)
        {
            Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.Code == "TASK_INPUT_TYPE");
            Assert.Null(compiled.Graph);
        }
        else
        {
            Assert.Empty(compiled.Diagnostics);
            var graph = compiled.Graph!;
            Assert.Empty(PlanningGeneratedGraph.Validate(graph, catalog, compactBindings: true));
            PlanningConfirmationGuards.Apply(graph, catalog);
            Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
            var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "rows", true, true, true, true, true, true, true, true, true, true, true, true, true);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
        }
        Assert.Equal(incompatible ? 0 : 1, calls);
        Assert.Equal(originalPlan, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(originalCatalog, JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));

        static TaskValue Reference(string port) => new() { Kind = "output", Source = "producer", Port = port };
    }
}
