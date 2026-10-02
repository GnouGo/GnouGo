using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArtifactProjectionTests
{
    [Theory]
    [InlineData("field")]
    [InlineData("whole")]
    [InlineData("each")]
    public async Task CheckedIdentityPreservingOperationsKeepProvenance(string type)
    {
        var (_, catalog) = await TaskArtifactBindingTests.Fixture();
        var original = PlanningCorpus.Ref("output", "origin", "handle");
        var array = type == "each";
        var projection = new PlanningNode { Key = "select", Type = "value.project", Input = type switch
        {
            "whole" => PlanningCorpus.Obj(("value", original), ("paths", new() { Kind = "array", Items = [Path()] })),
            "each" => PlanningCorpus.Obj(("value", new() { Kind = "array", Items = [PlanningCorpus.Obj(("kept", original))] }), ("paths", new() { Kind = "array", Items = [Path("kept")] }), ("each", new() { Kind = "boolean", Boolean = true })),
            _ => PlanningCorpus.Obj(("value", PlanningCorpus.Obj(("kept", original))), ("paths", new() { Kind = "array", Items = [Path("kept")] }))
        }, OutputSchema = new() { Contract = JsonNode.Parse(array
            ? """{"type":"object","required":["value"],"properties":{"value":{"type":"array","items":{"type":"string"}}}}"""
            : """{"type":"object","required":["value"],"properties":{"value":{"type":"string"}}}""")!.AsObject() } };
        var selected = PlanningCorpus.Ref("output", "select", array ? ["value", "0"] : ["value"]);
        var graph = new PlanningGraph { Workflows = [new() { Steps = [
            new() { Key = "origin", Type = "mcp.call", CapabilityId = "allocate", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) },
            projection,
            new() { Key = "use", Type = "mcp.call", CapabilityId = "inspect", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(("location", selected)))) }
        ] }] };
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        projection.OnError = [new(null, "continue", PlanningCorpus.Text("invented"), null)];
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "ARTIFACT_BINDING_INVALID");
        projection.OnError.Clear(); projection.If = new() { Kind = "boolean", Boolean = false };
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
        projection.If = null;
        original.Path = ["display"];
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "ARTIFACT_BINDING_INVALID");
    }

    [Fact]
    public async Task EveryProjectionAlternativeMustProveItsOriginAndCyclesFailClosed()
    {
        var (_, catalog) = await TaskArtifactBindingTests.Fixture();
        var projection = new PlanningNode { Key = "select", Type = "value.project", Input = PlanningCorpus.Obj(
            ("value", PlanningCorpus.Obj(("first", PlanningCorpus.Ref("output", "origin", "handle")), ("second", PlanningCorpus.Text("invented")))),
            ("paths", new() { Kind = "array", Items = [Path("first"), Path("second")] })),
            OutputSchema = new() { Contract = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string"}}}""")!.AsObject() } };
        var graph = new PlanningGraph { Workflows = [new() { Steps = [
            new() { Key = "origin", Type = "mcp.call", CapabilityId = "allocate", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) }, projection
        ] }] };
        bool Proves() => PlanningArtifactBindings.Proves(graph.Workflows[0], PlanningCorpus.Ref("output", "select", "value"), "resource", catalog, graph, []);
        Assert.False(Proves());
        PlanningGraphValidation.Member(projection.Input, "value")!.Members[1] = new("second", PlanningCorpus.Ref("output", "origin", "handle"));
        Assert.True(Proves());
        PlanningGraphValidation.Member(projection.Input, "value")!.Members[0] = new("first", PlanningCorpus.Ref("output", "select", "value"));
        Assert.False(Proves());
        projection.OutputSchema = null; Assert.False(Proves());
    }

    private static PlanningValue Path(params string[] names) => new() { Kind = "array", Items = names.Select(PlanningCorpus.Text).ToList() };
}
