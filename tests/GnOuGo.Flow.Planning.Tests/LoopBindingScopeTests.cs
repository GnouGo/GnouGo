using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LoopBindingScopeTests
{
    [Theory]
    [InlineData("loop.sequential")]
    [InlineData("loop.parallel")]
    public async Task NestedInitializationCanConsumeTheExistingOuterItem(string type)
    {
        var preparation = Preparation();
        if (!preparation.AllowedStepTypes.Contains(type)) preparation.AllowedStepTypes.Add(type);
        var contract = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        preparation.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = "consume", Kind = "tool", InputSchema = contract });
        var inner = new PlanningNode { Key = "inner", Type = type, Input = Obj(("items", new() { Kind = "loop_item", Source = "outer" })), Steps =
            [new() { Key = "consume", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj(("value", new() { Kind = "loop_item", Source = "inner" })))) }] };
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "groups", Required = true, Schema = new() { Type = "array", Items = new() { Type = "array", Items = new() { Type = "string" } } } }],
            Steps = [new() { Key = "outer", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "input", Source = "groups" })), Steps = [inner] }] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = [inner.Key], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        graph = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, preparation));
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "consume", InputSchema = contract }], ToolHandlers = new()
            { ["consume"] = args => { received.Add(args!["value"]!.GetValue<string>()); return new() { Content = JsonValue.Create("done") }; } } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!],
            new JsonObject { ["groups"] = new JsonArray(new JsonArray("alpha", "beta"), new JsonArray("gamma")) }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, received.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("pages", "cursor")]
    [InlineData("pages_renommees", "curseur")]
    public void GenerationAndFieldRepairKeepOwnLoopStateOutOfInitialInputs(string key, string cursor)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        var loop = new PlanningNode { Key = key, Type = "loop.sequential", Steps = [new() { Key = "read", Type = "set", Input = Obj((cursor, Str("next"))) }] };
        workflow.Steps = [loop];
        loop.Input = Obj(("while", new() { Kind = "compute", Text = "index < 2", Members = [new("index", new() { Kind = "loop_index", Source = key })] }),
            ("max_times", new() { Kind = "number", Number = 2 }), ("items", new() { Kind = "array", Items = [Obj((cursor, new() { Kind = "loop_previous", Source = key }))] }));
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = [key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(unit.Candidate, schema, unit));
        unit.Diagnostics = PlanningExecutableValidation.Validate(graph, preparation).Where(d => d.Code == "LOOP_BINDING_SCOPE_INVALID").ToList();
        Assert.Single(unit.Diagnostics);
        var patch = PlanningUnitPatches.Create(graph, unit, schema, preparation);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.Equal("nodes/" + key + "/input/members/2/value/items/0/members/0/value", coordinate);
        var illegal = unit.Candidate["nodes"]![key]!["input"]!["members"]![2]!["value"]!["items"]![0]!["members"]![0]!["value"]!.DeepClone();
        JsonObject Response(JsonNode value) => new() { ["changes"] = new JsonObject { [coordinate] = value }, ["remove"] = new JsonArray() };
        Assert.Throws<InvalidOperationException>(() => patch.Apply(unit.Candidate, Response(illegal)));
        var repaired = patch.Apply(unit.Candidate, Response(new JsonObject { ["kind"] = "null" }));
        Assert.Empty(PlanningConstruction.ShapeFindings(repaired, schema, unit));
        var applied = PlanningConstruction.Apply(graph, unit, repaired, preparation);
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(applied, preparation), d => d.Code == "LOOP_BINDING_SCOPE_INVALID");
        Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]![key]!["input"]!["members"]![0], repaired["nodes"]![key]!["input"]!["members"]![0]));
        // A repair inside while keeps the legal condition-only binding selectable.
        unit.Candidate = repaired; unit.Diagnostics = [new("CONDITION_INVALID", "/workflows/0/steps/0/input/members/0/value", "Repair the condition.")];
        var conditionPatch = PlanningUnitPatches.Create(applied, unit, schema, preparation);
        var conditionKey = Assert.Single(conditionPatch.Context(unit.Candidate)).Key;
        var whileValue = unit.Candidate["nodes"]![key]!["input"]!["members"]![0]!["value"]!.DeepClone();
        Assert.NotNull(conditionPatch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [conditionKey] = whileValue }, ["remove"] = new JsonArray() }));
    }
}
