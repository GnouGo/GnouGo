using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RetainedOutputRevisionTests
{
    [Theory]
    [InlineData("normalize", "rows", "store", "release")]
    [InlineData("assemble", "résultats", "persist", "dispose")]
    public async Task TargetedContractCorrectionThenExplicitDeterministicRevisionPreserveBusinessWork(
        string producer, string port, string writer, string cleanup)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "TargetedRevisions", "nullable-records.json")))!.AsObject();
        Rename(fixture);
        var proposal = new PlanningProposal
        {
            Plan = fixture["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan),
            Requirements = fixture["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)
        };
        var events = new List<string>(); JsonNode? written = null;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("independent", new()
        {
            Tools = [new() { Name = writer, EffectKind = "write", InputSchema = new JsonObject
                { ["type"] = "object", ["required"] = new JsonArray("records"),
                    ["properties"] = new JsonObject { ["records"] = TaskPlanCompiler.TypeSchema(proposal.Plan!.Root.Tasks[0].ResultType!.Fields[0].Type) } },
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"receipt":{"type":"string"}},"required":["receipt"]}""") },
                new() { Name = cleanup, EffectKind = "lifecycle", InputSchema = new JsonObject { ["type"] = "object" },
                    OutputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new()
            {
                [writer] = input => { events.Add(writer); written = input!["records"]!.DeepClone(); return new() { Content = new JsonObject { ["receipt"] = "observed completion" } }; },
                [cleanup] = _ => { events.Add(cleanup); return new() { Content = new JsonObject() }; }
            }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new TestRuntime(engine) { Proposal = proposal };
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); catalog.Policy.RequireExternalConfirmation = false;
        string Operation(string name) => TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == name)).Id;
        proposal.Plan!.Root.Tasks.Add(new() { Id = writer, Kind = "operation", Objective = "Persist actual records",
            Operation = Operation(writer), Inputs = [new("records", new() { Kind = "output", Source = producer, Port = port })] });
        proposal.Plan.Root.Always.Add(new() { Id = cleanup, Kind = "operation", Objective = "Release after processing", Operation = Operation(cleanup) });
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Request.Policy.RequireExternalConfirmation = false;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/" + port);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Empty(events);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var baseline = Copy(state.Plan!); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            EditablePaths = ["/tasks/" + producer + "/resultType"],
            Text = "Only make the output array's record objects non-null. Preserve nullable fields, bounds and all business work." }, runtime, PlannerFixture.Ct);
        Assert.Equal(Plan(baseline), Plan(state.Plan!)); Assert.Equal(1, state.ModelCalls);
        var corrected = Copy(baseline);
        corrected.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Nullable = false;
        runtime.Proposal.Plan = corrected;
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(Plan(corrected), Plan(state.Plan!)); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Null(state.ApprovedHash); PlanningArtifactApproval.Verify(state);

        var hash = state.ComputeArtifactHash();
        var deterministic = Copy(corrected);
        deterministic.Root.Tasks[0] = fixture["correctedNormalization"]!.Deserialize(PlanningJsonContext.Default.PlanTask)!;
        Assert.Contains(TaskPlanRevisions.Validate(corrected, deterministic, ["/tasks/" + producer + "/resultType"], catalog,
            ["/tasks/" + producer + "/resultType"]), d => d.Code == "REVISION_SCOPE_CHANGED");
        // Structural edits use the existing explicit revision, never wider patch authority.
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = state.Revision, ArtifactHash = hash,
            Text = "Replace only normalization with direct typed data bindings. Preserve accepted requirements, persistence and cleanup." }, runtime, PlannerFixture.Ct);
        Assert.Equal(Plan(corrected), Plan(state.Request.Baseline!)); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        runtime.Proposal.Plan = deterministic;
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(3, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(Plan(new() { Root = new() { Tasks = corrected.Root.Tasks.Skip(1).ToList(), Always = corrected.Root.Always } }),
            Plan(new() { Root = new() { Tasks = state.Plan!.Root.Tasks.Skip(1).ToList(), Always = state.Plan.Root.Always } }));
        Assert.NotEqual(hash, state.ComputeArtifactHash());
        Assert.DoesNotContain("llm.call", state.Yaml); Assert.DoesNotContain("mapping.dynamic", state.Yaml);
        state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision,
            ArtifactHash = state.ComputeArtifactHash(), ReviewedRequirementIds = ["records", "persist", "release"] }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Approved, state.Status);
        var records = JsonNode.Parse("""[{"identity":4,"label":null,"detail":"observed"},{"identity":1,"label":"duplicate","detail":null},{"identity":1,"label":"duplicate","detail":null}]""");
        var result = await LookupCompilationTests.Execute(state.Yaml!, new() { ["observed"] = records!.DeepClone() }, engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(records, result.Outputs![port])); Assert.True(JsonNode.DeepEquals(records, written));
        Assert.Equal(new[] { writer, cleanup }, events);
        events.Clear(); written = null;
        var invalid = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => LookupCompilationTests.Execute(
            state.Yaml!, new() { ["observed"] = new JsonArray((JsonNode?)null) }, engine));
        Assert.Contains("expected object", invalid.Message); Assert.Empty(events); Assert.Null(written);

        void Rename(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "id", "source" }) if (obj[key]?.ToString() == "normalize") obj[key] = producer;
                foreach (var key in new[] { "name", "port" }) if (obj[key]?.ToString() == "rows") obj[key] = port;
                foreach (var (_, child) in obj) Rename(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Rename(child);
        }
    }

    private static TaskPlan Copy(TaskPlan plan) => JsonSerializer.Deserialize(Plan(plan), PlanningJsonContext.Default.TaskPlan)!;
    private static string Plan(TaskPlan plan) => JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
}
