using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningInputReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompiledExtractionDistinguishesAuthoredEachFromRuntimeDetection(bool each)
    {
        var input = new TaskType { Kind = "array", Items = new() };
        if (!each) input = new() { Kind = "object", Fields = [new() { Name = "pages", Type = input }] };
        var task = new PlanTask { Id = "observe", Kind = "transform", Mode = "extract", Objective = "Observe supplied facts",
            Inputs = [new("evidence", new() { Kind = "input", Source = "evidence" })], Each = each ? new("evidence", "labels") : null,
            ResultType = new() { Kind = "object", Fields = [new() { Name = "labels", Type = new() { Kind = "array", Items = new() { Kind = "array", Items = new() } } }] } };
        var plan = new TaskPlan { Inputs = [new() { Name = "evidence", Type = input }], Root = new() { Tasks = [task] } };
        var runtime = new TestRuntime();
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile,
            ["mapping_profile"] = TaskPlanCompiler.AdaptiveMappingProfile } };
        var compilation = new TaskPlanCompiler().Compile(plan, await runtime.DiscoverAsync(request, PlannerFixture.Ct), request);
        Assert.Empty(compilation.Diagnostics);
        var result = Assert.Single(PlanningInputReadiness.Assess(compilation, 2, plan));
        Assert.Contains(each ? "Explicit each" : "Runtime collection detection", result.Description);
        Assert.Contains("/tasks/observe/inputs/evidence", result.Description);
        Assert.DoesNotContain("LLM input readiness at /workflows", result.Description);
        Assert.Equal("unverified", result.Outcome);
    }

    [Fact]
    public void RuntimeCollectionDetectionDoesNotClaimIndependentExtractionForNestedCollections()
    {
        var nested = Object(new PlanningMember("observation", Object(new PlanningMember("pages", new() { Kind = "array", Items = [Text("PRIVATE_OBSERVATION")] }))));
        var node = Mapping(nested);
        node.Input.Members.Add(new("infer_each", new() { Kind = "boolean", Boolean = true }));
        node.Input.Members.Add(new("adaptive_each", new() { Kind = "boolean", Boolean = true }));
        var result = Assert.Single(PlanningInputReadiness.Assess(Compilation(node), 2));
        Assert.Equal("unverified", result.Outcome);
        Assert.Contains("Runtime collection detection", result.Description);
        Assert.Contains("whole supplied input", result.Description);
        Assert.DoesNotContain("Independent extraction's complete collection", result.Description);
        Assert.DoesNotContain("PRIVATE_OBSERVATION", result.Description);
        Assert.Contains("/tasks/observe/inputs/observation", result.Description);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshReadinessLocatesAuthoredInputsWithoutChangingMeasurementsOrTheGraph(bool each)
    {
        const string field = "pages.é漢~literal";
        var node = Mapping(Object(new PlanningMember(field, new() { Kind = "array", Items = [Text("private-é-漢字-😀")] })), each);
        if (each) node.Input.Members.Single(m => m.Name == "each").Value.Members[0] = new("input", Text(field));
        var compilation = Compilation(node);
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "observe", Kind = "transform", Mode = "extract", Each = each ? new(field, "records") : null,
            Inputs = [new(field, new() { Kind = "input", Source = field })] }] } };
        var before = JsonSerializer.Serialize(compilation.Graph, PlanningJsonContext.Default.PlanningGraph);
        var result = Assert.Single(PlanningInputReadiness.Assess(compilation, 2, plan));
        Assert.Contains(each ? "Explicit each" : "Whole-input extraction", result.Description);
        Assert.Contains("/tasks/observe/inputs/" + field, result.Description);
        Assert.Contains("UTF-8 bytes", result.Description);
        Assert.Contains("not the complete serialized model request or a token estimate", result.Description);
        Assert.DoesNotContain("private-é", result.Description);
        Assert.Equal("unverified", result.Outcome);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(before, JsonSerializer.Serialize(compilation.Graph, PlanningJsonContext.Default.PlanningGraph));
    }


    [Fact]
    public void InterpretationReadinessUsesAuthoredInputLocationsAndKeepsUnknownEnvelopeUnverified()
    {
        var compilation = Compilation(new() { Key = "extract", Type = "llm.call", Input = Object(new PlanningMember("prompt", Reference("prompt"))) });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "observe", Kind = "transform", Mode = "interpret", Inputs = [new("facts", new() { Kind = "input", Source = "facts" })] }] } };
        var result = Assert.Single(PlanningInputReadiness.Assess(compilation, 2, plan));
        Assert.Contains("/tasks/observe/inputs/facts", result.Description);
        Assert.Contains("runtime-dependent", result.Description);
        Assert.Contains("Input fit requires the complete request", result.Description);
        Assert.Equal("unverified", result.Outcome);
    }

    [Fact]
    public void FreshMappingReviewDoesNotAssertIndependenceFromInferEachOrAdaptiveEach()
    {
        var node = Mapping(Object(new PlanningMember("observation", Reference("source"))));
        node.Input.Members.Add(new("infer_each", new() { Kind = "boolean", Boolean = true }));
        node.Input.Members.Add(new("adaptive_each", new() { Kind = "boolean", Boolean = true }));
        var state = new PlanningSession { Graph = Graph(node), Catalog = new() };
        var retained = Assert.Single(PlanningReviewFormatter.Operations(state));
        Assert.Contains("adaptive independent extraction", retained.Description);
        state.Request.Options["planning_input_review_version"] = 2;
        var current = Assert.Single(PlanningReviewFormatter.Operations(state));
        Assert.Contains("Runtime collection detection", current.Description);
        Assert.DoesNotContain("adaptive independent extraction", current.Description);
        Assert.DoesNotContain("Unambiguous source/target collections use", current.Description);
    }

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
    private static TaskCompilation Compilation(PlanningNode node) => new(Graph(node), [], new Dictionary<string, string> { [node.Key] = "/tasks/observe" });
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
