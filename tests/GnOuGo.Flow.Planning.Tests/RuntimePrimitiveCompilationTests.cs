using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RuntimePrimitiveCompilationTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("true")]
    public async Task StrictConfirmationGuardsWorkAndCleanup(string answer)
    {
        var sample = new PlanningBenchmarkCases.Environment("protected_cleanup");
        var factory = sample.Factory(); var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new Human(JsonNode.Parse(answer)) };
        // human.input intentionally normalizes UI answers. Exercise the gate's strict
        // executor-output boundary separately without changing that public behavior.
        if (answer is "1" or "\"true\"") engine.Registry.Register(new UnnormalizedConfirmation(JsonNode.Parse(answer)));
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(PlannerFixture.Ct))
            foreach (var item in (await runtime.Capabilities.ListAsync(source.Id, null, PlannerFixture.Ct)).Capabilities)
                catalog.Capabilities.Add((await runtime.Capabilities.ResolveAsync(item, PlannerFixture.Ct))!);
        var graph = PlanningCorpus.Graph("protected_cleanup", catalog); PlanningConfirmationGuards.Apply(graph, catalog);
        var gate = graph.Workflows[0].Steps[1]; Assert.Equal("set", gate.Type);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(graph)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(answer == "true", result.Success);
        Assert.Equal(answer == "true" ? new[] { "write", "cleanup" } : [], sample.Effects);
        if (answer != "true") Assert.Equal(ErrorCodes.InputValidation, result.Error?.Code);
        gate.OutputSchema!.Contract!["properties"]!["value"]!["enum"] = new JsonArray(true, false);
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "CONFIRMATION_REQUIRED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeachUsesOneCheckedPrimitiveAndRejectsExcessItems(bool parallel)
    {
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = new TaskPlan { Inputs = [new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }], Root = new()
        {
            Tasks = [new() { Id = "collect", Kind = "foreach", Objective = "Collect every item", Items = PlanningCorpus.Business("input", "values"), MaxItems = 2, Parallel = parallel,
                Body = new() { Outputs = [new("items", PlanningCorpus.Business("item"))] } }],
            Outputs = [new("items", PlanningCorpus.Business("output", "collect", "items"))]
        } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var nodes = compiled.Graph!.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps)).ToArray();
        Assert.DoesNotContain(nodes, n => n.Type is "array.project" or "value.validate" or "decision.evaluate" or "assert.non_null");
        Assert.Contains(nodes, n => n.Type == "value.project" && PlanningGraphValidation.Member(n.Input, "each")?.Boolean == true);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var values in new[] { new JsonArray(), new JsonArray("a", "a"), new JsonArray("a", "b", "c") })
        {
            var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["values"] = values }, PlannerFixture.Ct);
            Assert.Equal(values.Count <= 2, result.Success);
            if (result.Success) Assert.True(JsonNode.DeepEquals(values, result.Outputs!["items"]));
            else Assert.DoesNotContain(result.StepResults, s => s.StepType is "loop.parallel" or "loop.sequential");
        }
    }

    [Fact]
    public async Task PerItemProjectionRejectsDynamicModesAndInventedFields()
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var project = new PlanningNode { Key = "check", Type = "value.project", Input = PlanningCorpus.Obj(
            ("value", new() { Kind = "array", Items = [PlanningCorpus.Obj(("declared", PlanningCorpus.Text("x")))] }),
            ("each", new() { Kind = "boolean", Boolean = true }),
            ("paths", new() { Kind = "array", Items = [new() { Kind = "array", Items = [PlanningCorpus.Text("invented")] }] })),
            OutputSchema = new() { Contract = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"array","items":{"type":"string"}}}}""")!.AsObject() } };
        var graph = new PlanningGraph { Workflows = [new() { Steps = [project] }] };
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "PROJECTION_CONTRACT_INVALID");
        PlanningGraphValidation.Member(project.Input, "paths")!.Items[0].Items[0].Text = "declared";
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        project.Input.Members[1] = new("each", new() { Kind = "expression", Text = "true" });
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "PROJECTION_CONTRACT_INVALID");
    }

    private sealed class Human(JsonNode? answer) : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer?.DeepClone() }); }

    private sealed class UnnormalizedConfirmation(JsonNode? answer) : IStepExecutor
    {
        public string StepType => "human.input";
        public StepRecovery Recovery => StepRecovery.ReplaySafe;
        public StepContract Contract => BuiltInStepContracts.Get(StepType)!;
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer?.DeepClone() });
    }
}
