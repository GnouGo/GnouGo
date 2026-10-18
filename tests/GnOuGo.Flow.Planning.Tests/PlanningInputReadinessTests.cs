using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningInputReadinessTests
{
    [Fact]
    public void DeterministicAssemblyDoesNotAcquireAnInferenceWarning()
    {
        var graph = Graph(new() { Key = "copy", Type = "set", Input = Text("observed") });
        Assert.Empty(PlanningInputReadiness.Assess(graph));
    }

    [Fact]
    public void RuntimePromptSizeRemainsExplicitlyUnverified()
    {
        var graph = Graph(new() { Key = "decide", Type = "llm.call", Input = Object(new PlanningMember("prompt", Reference("observation"))) });
        var result = Assert.Single(PlanningInputReadiness.Assess(graph));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains("runtime-dependent", result.Description);
        Assert.Contains("/workflows/0/steps/0", result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LiteralInputReportsExactJsonBytesWithoutClaimingCompleteRequestAdmission()
    {
        const string secret = "private-é-漢字-😀";
        var input = Object(new PlanningMember("prompt", Text(secret)));
        var bytes = Encoding.UTF8.GetByteCount(new JsonObject { ["prompt"] = secret }.ToJsonString());
        var result = Assert.Single(PlanningInputReadiness.Assess(Graph(new() { Key = "decide", Type = "llm.call", Input = input })));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains(bytes + " UTF-8 bytes", result.Description);
        Assert.Contains("not the complete serialized model request", result.Description);
        Assert.DoesNotContain(secret, result.Description);
        Assert.DoesNotContain("estimated_tokens", result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LargeIndependentCollectionIsNotTreatedAsOneModelRequest()
    {
        var source = new PlanningValue { Kind = "array", Items = Enumerable.Range(0, 200).Select(_ => Text(new string('x', 1000))).ToList() };
        var result = Assert.Single(PlanningInputReadiness.Assess(Graph(Mapping(Object(new PlanningMember("pages", source)), each: true))));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains("complete collection size is not a single request size", result.Description);
        Assert.Contains("shared context and complete examples", result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void IndependentSharedContextRemainsRuntimeDependent()
    {
        var sources = Object(new PlanningMember("pages", new() { Kind = "array" }), new("context", Reference("facts")));
        var result = Assert.Single(PlanningInputReadiness.Assess(Graph(Mapping(sources, each: true))));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains("runtime-dependent", result.Description);
        Assert.Contains("shared context and complete examples", result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void WholeExtractionReportsKnownSourceBytesWithoutPayloadOrInferredAllowance()
    {
        var sources = Object(new PlanningMember("evidence", Text(new string('z', 100_000))));
        var result = Assert.Single(PlanningInputReadiness.Assess(Graph(Mapping(sources))));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains("Source JSON", result.Description);
        Assert.Contains("runtime model settings and authoritative allowance", result.Description);
        Assert.DoesNotContain(new string('z', 50), result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NestedAndCleanupConsumersAreIncludedWithoutRewritingTheGraph()
    {
        var graph = Graph(new() { Key = "route", Type = "switch", Cases = [new(null, null,
            [new() { Key = "inside", Type = "llm.call", Input = Object(new PlanningMember("prompt", Reference("facts"))) }])] });
        graph.Workflows[0].Finally.Add(Mapping(Object(new PlanningMember("value", Reference("saved")))));
        var before = JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph);
        var results = PlanningInputReadiness.Assess(graph);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Description.Contains("/steps/0/cases/0/steps/0", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Description.Contains("/finally/0", StringComparison.Ordinal));
        Assert.All(results, r => Assert.Equal("unverified", r.Outcome));
        Assert.Equal(before, JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph));
    }

    private static PlanningGraph Graph(PlanningNode node) => new() { Workflows = [new() { Steps = [node] }] };
    private static PlanningValue Text(string value) => new() { Kind = "string", Text = value };
    private static PlanningValue Reference(string source) => new() { Kind = "output", Source = source };
    private static PlanningValue Object(params PlanningMember[] members) => new() { Kind = "object", Members = [.. members] };
    private static PlanningNode Mapping(PlanningValue sources, bool each = false)
    {
        var input = new PlanningValue { Kind = "dynamic_mapping", Members = [new("sources", sources), new("objective", Text("Observe supplied evidence"))] };
        if (each) input.Members.Add(new("each", Object(new PlanningMember("input", Text("pages")), new("output", Text("records")))));
        return new() { Key = "extract", Type = "set", Input = input };
    }
}
