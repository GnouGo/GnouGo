using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutputBindingRoutingTests
{
    [Theory]
    [InlineData("report")]
    [InlineData("résumé/},{_技术")]
    public async Task NullablePublicOutputUsesItsExactBindingSlot(string name)
    {
        var (runtime, state) = await Fixture(name);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/" + name);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan!.Root.Outputs[0] = new(name, CommitmentCoverageTests.Output("observe", "verified"));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.True(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        var slots = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!["slots"]!.AsArray();
        Assert.Equal("/root/outputs/" + name, Assert.Single(slots)!["location"]!.ToString());
        Assert.Contains("/outputs/" + name, runtime.Calls[1].Prompt);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal("targeted_revision", state.Request.Options["automatic_correction_progress"]!["action"]!.ToString());
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task DiagnosedOutputDoesNotAuthorizeReinterpretingIndependentStatus()
    {
        var (runtime, state) = await Fixture();
        state.Request.MaxReplanAttempts = 1;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        runtime.Proposal.Plan.Root.Outputs[1] = new("finished", new() { Kind = "boolean", Boolean = true });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.ModelCalls);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal("observe", state.Request.Baseline!.Root.Outputs[1].Value.Source);
        Assert.Contains(state.Diagnostics, d => d.Code is "PLANNING_RESPONSE_INVALID" or "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task HistoricalCorrectionKeepsItsOriginalLocationsAndRegeneration()
    {
        var (runtime, state) = await Fixture();
        state.Revision = 1;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        state.Request.Baseline = PlannerFixture.Clone(state).Plan;
        state.Request.Options["automatic_correction_progress"] = new JsonObject
        {
            ["version"] = 4, ["attempts"] = new JsonArray(), ["outputRemovals"] = new JsonArray(),
            ["conditionLocations"] = new JsonArray(), ["locations"] = new JsonArray("/outputs/report"),
            ["operationAdditions"] = new JsonArray(), ["adaptationTasks"] = new JsonArray(), ["preservedTasks"] = new JsonArray("observe")
        };
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal(4, progress["version"]!.GetValue<int>());
        Assert.Equal("/outputs/report", Assert.Single(progress["locations"]!.AsArray())!.ToString());
        Assert.Equal("regenerate", progress["action"]!.ToString());
    }

    [Fact]
    public async Task PendingTargetedCorrectionKeepsItsIssuedSchemaAndAuthority()
    {
        var (runtime, state) = await Fixture();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        var complete = await PlannerFixture.RunAsync(runtime, state);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null));
        var pendingId = pending.PendingCall!.Id;
        var issued = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var authority = pending.Request.Options["automatic_correction_progress"]!.ToJsonString();
        var response = new LLMResponse { Json = TestRuntime.PatchResponse(pending.PendingCall.Request, pending, runtime.Proposal.Plan) };
        var reads = 0;
        runtime.Respond = (request, _) =>
        { Assert.Equal(0, reads++); Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)); return response; };
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(complete.Yaml, recovered.Yaml);
        Assert.Equal(2, recovered.ModelCalls); Assert.Equal(1, recovered.ReplanAttempts); Assert.Equal(1, reads);
        Assert.Equal(authority, runtime.Checkpoints.First(s => s.PendingCall?.Id == pendingId).Request.Options["automatic_correction_progress"]!.ToJsonString());
    }

    internal static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(string name = "report")
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("records", new() { Tools = [new() { Name = "observe", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"payload":{"type":["string","null"]},"verified":{"type":"string"},"success":{"type":"boolean"}},"required":["payload","verified","success"],"additionalProperties":false}""") }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "observe")).Id;
        runtime.Proposal = new()
        {
            Requirements = new() { Summary = "Return the verified producer text and its original status", Inputs = [],
                Outputs = [new() { Name = name, Type = new() { Kind = "string" } }, new() { Name = "finished", Type = new() { Kind = "boolean" } }],
                Outcomes = [new("report", "Return the verified producer text") { Commitments = [new(operation) { Result = new("verified", name) }] }] },
            Plan = new() { Root = new() { Tasks = [new() { Id = "observe", Objective = "Acquire authoritative text and status", Operation = operation }],
                Outputs = [new(name, CommitmentCoverageTests.Output("observe", "payload")), new("finished", CommitmentCoverageTests.Output("observe", "success"))] } }
        };
        var state = PlannerFixture.Session(); state.IntentVersion = 3; state.Catalog = catalog;
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        return (runtime, state);
    }
}
