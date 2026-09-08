using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RuntimeContextBindingTests
{
    [Theory]
    [InlineData("observe", "check", "consume", "Keep the supplied instructions")]
    [InlineData("observer", "vérifier", "utiliser", "Conserver les instructions fournies")]
    public async Task ExplicitContextConsumesOriginalResultsWithoutReplacingCallerInstructions(string first, string second, string method, string instructions)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        var schema = JsonNode.Parse("""{"type":"object","properties":{"instructions":{"type":"string"},"contextJson":{"type":"string"}},"required":["instructions"]}""")!.AsObject();
        prep.Capabilities = [new() { Id = "context-consumer", StepType = "mcp.call", Server = "renamed", Method = method, Kind = "tool",
            OperationIds = ["consume"], InputOperationIds = [first, second], InputSchema = schema }];
        workflow.OperationIds = [first, second, "consume"];
        workflow.Inputs = [new() { Name = "instructions", Schema = new() { Type = "string" } }]; workflow.Outputs.Clear();
        var consumer = new PlanningNode { Key = "consume", Type = "mcp.call", CapabilityId = "context-consumer", OperationIds = ["consume"],
            Input = Obj(("request", Obj(("instructions", new() { Kind = "input", Source = "instructions" })))) };
        workflow.Steps = [new() { Key = first, OperationIds = [first], Input = Obj(("ok", new() { Kind = "boolean", Boolean = false })) },
            new() { Key = second, OperationIds = [second], Input = Obj(("status", Str("unknown")), ("reason", Str("quoted \" result"))) }, consumer];
        Assert.Equal(2, PlanningDataflow.OperationInputFindings(graph, prep).Count);
        consumer.Input.Members[0].Value.Members.Add(new("contextJson", new() { Kind = "compute", Text = "return JSON.stringify({first: initial, second: checks});",
            Members = [new("initial", new() { Kind = "output", Source = first }), new("checks", new() { Kind = "output", Source = second })] }));
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        var factory = new InMemoryMcpClientFactory(); JsonNode? received = null;
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = method, InputSchema = schema }], ToolHandlers = new()
            { [method] = args => { received = args?.DeepClone(); return new McpCallResult { Content = JsonValue.Create("observed") }; } } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["instructions"] = instructions }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(instructions, received!["instructions"]!.ToString());
        var context = JsonNode.Parse(received["contextJson"]!.GetValue<string>())!;
        Assert.False(context["first"]!["ok"]!.GetValue<bool>()); Assert.Equal("unknown", context["second"]!["status"]!.ToString());
        Assert.Equal("quoted \" result", context["second"]!["reason"]!.ToString());
    }
}
