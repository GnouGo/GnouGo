using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class AuthoritativeGraphTests
{
    [Fact]
    public async Task ExampleDataCannotEstablishOpaqueProducerProperties()
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", Server = "test", Method = "observe", Kind = "tool", EffectKind = "read",
            InputSchema = new() { ["type"] = "object" }, ExampleResponse = new JsonObject { ["value"] = 42 } });
        var graph = new PlanningGraph { Workflows = [new() { Steps = [new() { Key = "source", Type = "mcp.call", CapabilityId = "opaque", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "number" }, Value = PlanningCorpus.Ref("output", "source", "value") }] }] };
        Assert.NotEmpty(PlanningExecutableValidation.Validate(graph, catalog));
        graph.Workflows[0].Steps.Add(new() { Key = "validate", Type = "value.validate", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "source"))),
            OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Schema = new() { Type = "object", Properties = [new() { Name = "value", Schema = new() { Type = "number" } }] } }] } });
        graph.Workflows[0].Outputs[0].Value = PlanningCorpus.Ref("output", "validate", "value", "value");
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
    }
    [Fact]
    public async Task RequiredArtifactCannotBeReplacedWithAnInventedIdentifier()
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var shape = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""")!.AsObject();
        catalog.Capabilities = [new() { Id = "create", StepType = "mcp.call", Server = "source", Kind = "tool", Method = "create", EffectKind = "read", InputSchema = new() { ["type"] = "object" }, OutputSchema = shape,
                ArtifactContract = new(1, [new("resource", "/id", "materialize")], []) },
            new() { Id = "consume", StepType = "mcp.call", Server = "source", Kind = "tool", Method = "consume", EffectKind = "read", InputSchema = shape, OutputSchema = shape,
                ArtifactContract = new(1, [], [new("resource", "/id", true)]) }];
        var graph = new PlanningGraph { Workflows = [new() { Steps = [new() { Key = "create", Type = "mcp.call", CapabilityId = "create", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) },
            new() { Key = "consume", Type = "mcp.call", CapabilityId = "consume", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(("id", PlanningCorpus.Text("invented"))))) }] }] };
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "ARTIFACT_BINDING_INVALID");
        graph.Workflows[0].Steps[1].Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(("id", PlanningCorpus.Ref("output", "create", "id")))));
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
    }
}
