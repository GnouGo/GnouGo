using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class StructuredContinuationTests
{
    [Theory]
    [InlineData("pages", "observe", 3, false)]
    [InlineData("étapes", "observer", 8, false)]
    [InlineData("pages", "observe", 3, true)]
    public async Task ComputedStructuredFallbackRetainsPreviousResults_AndInvalidResultsBlockEffects(string loopKey, string method, int start, bool invalid)
    {
        var prep = Preparation(); var writes = 0; var cleanups = 0;
        foreach (var id in new[] { method, "write", "cleanup" }) prep.Capabilities.Add(new()
        { Id = id, StepType = "mcp.call", Server = "renamed", Method = id, Kind = "tool", InputSchema = new() { ["type"] = "object" } });
        PlanningValue Previous() => new() { Kind = "loop_previous", Source = loopKey, Path = ["source", "json"] };
        var value = new PlanningValue { Kind = "compute", Text = invalid ? "return String(previous == null ? start : previous.page + 1);" : "return previous == null ? start : previous.page + 1;",
            Members = [new("previous", Previous()), new("start", new() { Kind = "input", Source = "start" })] };
        var producer = new PlanningNode { Key = "source", Type = "mcp.call", CapabilityId = method, Input = Obj(),
            StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "page", Required = true, Schema = new() { Type = "integer" } }] }),
            OnError = [new(null, "continue", Obj(("json", Obj(("page", value)))), null)] };
        var loop = new PlanningNode { Key = loopKey, Type = "loop.sequential", Input = Obj(("times", new() { Kind = "number", Number = 2 })), Steps = [producer] };
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "start", Required = true, Schema = new() { Type = "integer" } }],
            Steps = [loop, new() { Key = "write", Type = "mcp.call", CapabilityId = "write", Input = Obj() }],
            Finally = [new() { Key = "cleanup", Type = "mcp.call", CapabilityId = "cleanup", Input = Obj() }] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        Assert.Empty(PlanningGraphValidation.Validate(graph, prep));
        var unit = new PlanningConstructionUnit { Kind = "implementation", WorkflowKey = workflow.Key, NodeKeys = [producer.Key], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        var schema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(candidate, schema));
        var flat = candidate.DeepClone().AsObject();
        var output = flat["nodes"]![producer.Key]!["onError"]![0]!["setOutput"]!;
        flat["nodes"]![producer.Key]!["onError"]![0]!["setOutput"] = output["members"]![0]!["value"]!.DeepClone();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(flat, schema));
        graph = PlanningConstruction.Apply(graph, unit, candidate, prep);
        producer = graph.Workflows[0].Steps[0].Steps[0];
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, producer.Key).Values,
            b => b.Value.Kind == "loop_previous" && b.Value.Path.SequenceEqual(new[] { "source", "json" }) && b.Availability == "nullable");
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = prep.Capabilities.Select(c => new McpToolInfo { Name = c.Method!, InputSchema = c.InputSchema }).ToList(),
            ToolHandlers = new() { [method] = _ => throw new InvalidOperationException("Injected source failure"),
                ["write"] = _ => { writes++; return new(); }, ["cleanup"] = _ => { cleanups++; return new(); } } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!],
            new JsonObject { ["start"] = start }, TestContext.Current.CancellationToken);
        Assert.Equal(!invalid, result.Success); Assert.Equal(invalid ? 0 : 1, writes); Assert.Equal(1, cleanups);
        if (invalid) Assert.Equal("STRUCTURED_FALLBACK_INVALID", result.Error?.Code);
        else
        {
            var pages = result.StepResults[0].Output!["results"]!.AsArray();
            Assert.Equal(2, pages.Count);
            Assert.Contains((start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), pages[1]!.ToJsonString());
        }
        producer.OnError = [new(null, "continue", Obj(("json", Obj(("page", Str("wrong"))))), null)];
        Assert.Contains(PlanningGraphValidation.Validate(graph, prep), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
    }

    [Theory]
    [InlineData("records", "value", false)]
    [InlineData("éléments", "valeur", false)]
    [InlineData("records", "value", true)]
    public void NestedAlternativePathsRequireTheFieldInEveryPossibleResult(string container, string field, bool missing)
    {
        var prep = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        JsonObject Object(string name) => new() { ["type"] = "object", ["properties"] = new JsonObject { [name] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(name) };
        prep.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", OutputSchema = new()
            { ["type"] = "object", ["properties"] = new JsonObject { [container] = new JsonObject
                { ["anyOf"] = new JsonArray(Object(field), Object(missing ? "absent" : field)) } }, ["required"] = new JsonArray(container) } });
        workflow.Steps.Insert(0, new() { Key = "source", Type = "mcp.call", CapabilityId = "source" });
        var reference = new PlanningValue { Kind = "output", Source = "source", Path = [container, field] };
        if (missing) Assert.Throws<InvalidOperationException>(() => PlanningGraphValidation.ResolveValueContract(graph, workflow, reference, prep));
        else Assert.Equal(2, PlanningGraphValidation.ResolveValueContract(graph, workflow, reference, prep)["anyOf"]!.AsArray().Count);
    }
}
