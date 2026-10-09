using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FlattenCompilationTests
{
    internal static TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
    private static TaskType ArrayOf(TaskType item) => new() { Kind = "array", Items = item };
    private static TaskPlan Plan(TaskType element) => new()
    {
        Inputs = [new() { Name = "batches", Type = ArrayOf(ArrayOf(element)) }],
        Root = new() { Tasks = [new() { Id = "combine", Kind = "value", Objective = "Concatenate the declared groups once",
            Outputs = [new("rows", Flatten(new() { Kind = "input", Source = "batches" }))] }],
            Outputs = [new("rows", ProductTransformationPlan.Ref("combine", "rows"))] }
    };
    private static async Task<PlanningCatalog> Catalog(WorkflowEngine? engine = null) =>
        await new WorkflowPlanningRuntime(engine ?? new(), (_, _) => Task.CompletedTask).DiscoverAsync(
            new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);

    [Theory]
    [InlineData("[[],[\"a\",null,\"a\"],[],[\"b\"]]", "[\"a\",null,\"a\",\"b\"]", false)]
    [InlineData("[]", "[]", false)]
    [InlineData("[[[\"a\",null],[]],[[\"a\"]]]", "[[\"a\",null],[],[\"a\"]]", true)]
    public async Task OneLevelIsStructuredUntilLoweringAndPreservesAllValues(string source, string expected, bool nested)
    {
        var type = new TaskType { Kind = "string", Nullable = true };
        var catalog = await Catalog(); var plan = Plan(nested ? ArrayOf(type) : type);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, true); Assert.Empty(compiled.Diagnostics);
        var before = JsonSerializer.Serialize(compiled.Graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.Contains("flatten", before); Assert.DoesNotContain("flatMap", before); Assert.DoesNotContain("expression", before);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        Assert.Equal(before, JsonSerializer.Serialize(compiled.Graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.DoesNotContain("mapping.dynamic", yaml); Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("loop.", yaml);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var input = JsonNode.Parse(source); var original = input!.ToJsonString();
        var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["batches"] = input }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result.Outputs!["rows"]));
        Assert.Equal(original, input.ToJsonString());
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("nullable_outer")]
    [InlineData("nullable_inner")]
    [InlineData("opaque")]
    [InlineData("arity")]
    [InlineData("extra_field")]
    public async Task UnsupportedOperandsFailBeforeLowering(string variant)
    {
        var plan = Plan(new() { Kind = "string" });
        var value = plan.Root.Tasks[0].Outputs[0].Value;
        switch (variant)
        {
            case "scalar": plan.Inputs[0].Type = new() { Kind = "string" }; break;
            case "nullable_outer": plan.Inputs[0].Type.Nullable = true; break;
            case "nullable_inner": plan.Inputs[0].Type.Items!.Nullable = true; break;
            case "opaque": plan.Inputs[0].Type.Items!.Items = new() { Kind = "any" }; break;
            case "arity": value.Items.Clear(); break;
            case "extra_field": value.Port = "guessed"; break;
        }
        var compiled = new TaskPlanCompiler().Compile(plan, await Catalog());
        Assert.Null(compiled.Graph); Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_FLATTEN_INVALID");
    }

    [Theory]
    [InlineData("[[\"valid\"]]", true)]
    [InlineData("[[\"valid\",\"too-many\"]]", false)]
    [InlineData("[null]", false)]
    [InlineData("[\"scalar\"]", false)]
    [InlineData("[[3]]", false)]
    public async Task SourceConstraintsAreCheckedBeforeConcatenation(string observed, bool succeeds)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "read_batches", EffectKind = "read",
            InputSchema = new JsonObject { ["type"] = "object" }, OutputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"batches":{"type":"array","items":{"type":"array","maxItems":1,"items":{"type":"string"}}}},"required":["batches"]}
            """) }], ToolHandlers = new() { ["read_batches"] = _ => new() { Content = new JsonObject { ["batches"] = JsonNode.Parse(observed) } } } });
        var engine = new WorkflowEngine { McpClientFactory = factory }; var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        catalog.Policy.RequireExternalConfirmation = false;
        var plan = Plan(new()); plan.Inputs.Clear();
        plan.Root.Tasks[0].Outputs[0].Value.Items = [ProductTransformationPlan.Ref("observe", "batches")];
        plan.Root.Tasks.Insert(0, new() { Id = "observe", Kind = "operation", Objective = "Read declared batches",
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "read_batches")).Id });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(succeeds, result.Success); if (!succeeds) Assert.Null(result.Outputs);
    }

    [Theory]
    [InlineData("[[\"a\"]]", true)]
    [InlineData("[[\"a\"],[\"b\"]]", false)]
    public async Task DownstreamCollectionConstraintIsEnforcedBeforeExternalDispatch(string input, bool allowed)
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("independent", new() { Tools = [new() { Name = "accept_rows", EffectKind = "write",
            InputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"array\",\"maxItems\":1,\"items\":{\"type\":\"string\"}}},\"required\":[\"rows\"]}"),
            OutputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}") }], ToolHandlers = new() {
            ["accept_rows"] = _ => { calls++; return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask)); catalog.Policy.RequireExternalConfirmation = false;
        var plan = Plan(new() { Kind = "string" });
        plan.Root.Tasks.Add(new() { Id = "accept", Kind = "operation", Objective = "Consume the constrained collection",
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "accept_rows")).Id,
            Inputs = [new("rows", ProductTransformationPlan.Ref("combine", "rows"))] });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, true, true); Assert.Empty(compiled.Diagnostics);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["batches"] = JsonNode.Parse(input) }, PlannerFixture.Ct);
        Assert.Equal(allowed, result.Success); Assert.Equal(allowed ? 1 : 0, calls); if (!allowed) Assert.Null(result.Outputs);
    }

    [Theory]
    [InlineData(0, "f513063eb84a5c3271332812d5d1f422f8168bb5d96a3004fdf89024f29e9adf")]
    [InlineData(1, "087df2e339e040aef41ae2b54f7c42c378d83da941f2508556c2890519f224cf")]
    [InlineData(2, "1bf4f54fb6f5eab9b3c905f26c377463343ca826eb37822076d1f2fb5b2870b7")]
    public async Task ExistingPlanRetainsBaselineYamlByteForByte(int profile, string expectedHash)
    {
        // Hashes independently collected from clean 93d1ffa8, not this compiler.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConsumerInputs", "historical-plan.json"));
        var plan = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.TaskPlan)!;
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, profile > 0, profile > 1);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BranchesCaptureAndExportExplicitFlattenedValues(bool selected)
    {
        var plan = Plan(new() { Kind = "string", Nullable = true });
        plan.Inputs.Add(new() { Name = "enabled", Type = new() { Kind = "boolean" } });
        TaskScope Branch(string id) => new() { Tasks = [new() { Id = id, Kind = "value", Objective = "Retain exact candidates",
            Outputs = [new("rows", Flatten(new() { Kind = "input", Source = "batches" }))] }], Outputs = [new("rows", ProductTransformationPlan.Ref(id, "rows"))] };
        plan.Root.Tasks = [new() { Id = "combine", Kind = "conditional", Objective = "Select an explicit branch interface",
            Condition = new() { Kind = "input", Source = "enabled" }, Body = Branch("left"), Otherwise = Branch("right") }];
        var catalog = await Catalog(); var compiled = new TaskPlanCompiler().Compile(plan, catalog, true, true);
        Assert.Empty(compiled.Diagnostics);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject {
            ["enabled"] = selected, ["batches"] = JsonNode.Parse("[[\"a\",null],[],[\"a\"]]") }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("[\"a\",null,\"a\"]", result.Outputs!["rows"]!.ToJsonString());
    }

    [Fact]
    public void ExplicitStaticElementsKeepTheirLineageWithoutCreatingCollectionOwnership()
    {
        var original = new PlanningValue { Kind = "output", Source = "producer", Path = ["handle"] };
        var flattened = new PlanningValue { Kind = "flatten", Items = [new() { Kind = "array", Items = [new() { Kind = "array", Items = [original, original] }] }] };
        Assert.Same(original, PlanningValueProvenance.Select(flattened, ["1"]));
        Assert.Null(PlanningValueProvenance.Select(flattened, ["2"]));
        Assert.False(PlanningValueProvenance.Proves(new(), flattened, new(), (_, _) => true));
    }

    [Fact]
    public async Task FreshSchemaReachesReviewInOneCallAndApprovalChangesAfterRevision()
    {
        var plan = Plan(new() { Kind = "string" });
        var runtime = new TestRuntime { Proposal = new() { Plan = plan, Requirements = new() { Summary = "Join every group once",
            Inputs = plan.Inputs, Outputs = [new() { Name = "rows", Type = ArrayOf(new()) }], Outcomes = [new("rows", "Join all groups")] } } };
        var session = await PlannerFixture.RunAsync(runtime); Assert.Equal(PlanningStatus.FinalReview, session.Status);
        Assert.Single(runtime.Calls); Assert.Equal(0, session.ReplanAttempts); Assert.Contains("flatten", runtime.Calls[0].StructuredOutputSchema!.ToJsonString());
        var schema = runtime.Calls[0].StructuredOutputSchema!.ToJsonString(); var hash = session.ComputeArtifactHash();
        session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = session.Revision, ArtifactHash = hash, Text = "Revise the explicit grouping while retaining every row." }, runtime, PlannerFixture.Ct);
        Assert.Null(session.Yaml); Assert.Equal(schema, runtime.Calls[0].StructuredOutputSchema!.ToJsonString());
        Assert.Equal("compact-bindings-v5", session.Request.Options["compilation_profile"]!.ToString());
    }
}
