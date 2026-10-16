using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutputCorrectionTests
{
    [Theory]
    [InlineData("extra")]
    [InlineData("résumé_技术")]
    public async Task DiagnosedUnacceptedExportCanBeRemovedWithoutChangingTheAction(string extra)
    {
        var (runtime, state) = await Fixture(extra);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/" + extra);
        var original = JsonSerializer.Serialize(state.Plan!.Root.Tasks[0], PlanningJsonContext.Default.PlanTask);
        runtime.Proposal.Plan!.Root.Outputs.RemoveAll(o => o.Name == extra);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan!.Root.Tasks[0], PlanningJsonContext.Default.PlanTask));
        Assert.Equal(new[] { "result", "audit" }, state.Plan.Root.Outputs.Select(o => o.Name));
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash); PlanningArtifactApproval.Verify(state);
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal(3, progress["version"]!.GetValue<int>());
        Assert.Equal(extra, Assert.Single(progress["outputRemovals"]!.AsArray())!.ToString());
        Assert.Contains("Remove only the diagnosed unaccepted exports", runtime.Calls[1].Prompt);
        Assert.Contains("Preserve all operation arguments verbatim", runtime.Calls[1].Prompt);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(
            TestRuntime.Response(runtime.Calls[1], runtime.Proposal).Json, runtime.Calls[1].StructuredOutputSchema!));
    }

    [Theory]
    [InlineData("instruction")]
    [InlineData("required_output")]
    [InlineData("optional_output")]
    [InlineData("reorder")]
    [InlineData("binding")]
    [InlineData("rename")]
    [InlineData("new_output")]
    [InlineData("duplicate")]
    [InlineData("requirements")]
    public async Task RemovingAnUnacceptedExportGrantsNoOtherAuthority(string change)
    {
        var (runtime, state) = await Fixture();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs.RemoveAll(o => o.Name == "extra");
        switch (change)
        {
            case "instruction": runtime.Proposal.Plan.Root.Tasks[0].Inputs[0].Value.Text += " renamed"; break;
            case "required_output": runtime.Proposal.Plan.Root.Outputs.RemoveAt(0); break;
            case "optional_output": runtime.Proposal.Plan.Root.Outputs.RemoveAt(1); break;
            case "reorder": runtime.Proposal.Plan.Root.Outputs.Reverse(); break;
            case "binding": runtime.Proposal.Plan.Root.Outputs[0] = new("result", new() { Kind = "string", Text = "invented" }); break;
            case "rename": runtime.Proposal.Plan.Root.Outputs[0] = new("replacement", Port()); break;
            case "new_output": runtime.Proposal.Plan.Root.Outputs.Add(new("another", Port())); break;
            case "duplicate": runtime.Proposal.Plan.Root.Outputs.Add(new("result", Port())); break;
            case "requirements":
                runtime.Proposal.Requirements!.Outputs![0].Type.Kind = "any";
                runtime.Respond = (request, _) =>
                {
                    var response = TestRuntime.Response(request, runtime.Proposal);
                    response.Json!["requirements"] = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements);
                    return response;
                };
                break;
        }
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == (change == "requirements" ? "PLANNING_RESPONSE_INVALID" : "REVISION_SCOPE_CHANGED"));
        Assert.Equal("Keep the original operation instructions.", state.Request.Baseline!.Root.Tasks[0].Inputs[0].Value.Text);
        Assert.Equal(new[] { "result", "audit", "extra" }, state.Request.Baseline.Root.Outputs.Select(o => o.Name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HistoricalCorrectionKeepsTheOriginalOutputPreservation(int version)
    {
        var (runtime, state) = await Fixture();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        state.Request.Baseline = PlannerFixture.Clone(state).Plan;
        state.Request.Options["automatic_correction_progress"] = new JsonObject
        {
            ["version"] = version, ["attempts"] = new JsonArray(), ["conditionLocations"] = new JsonArray(),
            ["locations"] = new JsonArray("/outputs/extra")
        };
        runtime.Proposal.Plan!.Root.Outputs.RemoveAll(o => o.Name == "extra");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/root/outputs");
        Assert.Equal(version, state.Request.Options["automatic_correction_progress"]!["version"]!.GetValue<int>());
        Assert.DoesNotContain("Remove only the diagnosed unaccepted exports", runtime.Calls[1].Prompt);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task RepeatedInvalidResponseStopsWithinTheSameAllowance()
    {
        var (runtime, state) = await Fixture();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.Yaml);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOptionalOutputDiffersFromExplicitNull(bool includeNull)
    {
        var (runtime, state) = await Fixture();
        runtime.Proposal.Plan!.Root.Outputs.RemoveAll(o => o.Name == "audit");
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan.Root.Outputs.RemoveAll(o => o.Name == "extra");
        if (includeNull) runtime.Proposal.Plan.Root.Outputs.Add(new("audit", new() { Kind = "null" }));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(includeNull ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        // No authority to add an export, even if its name is accepted and optional.
        if (includeNull) Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task ExportRemovalStillRunsCompleteOutputContractValidation()
    {
        var (runtime, state) = await Fixture();
        runtime.Proposal.Plan!.Root.Outputs[1] = new("audit", new() { Kind = "null" });
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan.Root.Outputs.RemoveAll(o => o.Name == "extra");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/audit");
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
    }

    [Fact]
    public async Task CommittedRemovalReceiptIsReusedWithoutNewAuthorityOrAllowance()
    {
        var (runtime, state) = await Fixture();
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs.RemoveAll(o => o.Name == "extra");
        var completed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, completed.Status);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == "replan"));
        var issued = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var authority = pending.Request.Options["automatic_correction_progress"]!.ToJsonString();
        var receipt = TestRuntime.Response(pending.PendingCall.Request, runtime.Proposal);
        var reads = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose); Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(0, reads++); return receipt;
        };
        var recovered = await planner.AdvanceAsync(PlannerFixture.Clone(pending), new() { ExpectedRevision = pending.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(completed.Yaml, recovered.Yaml);
        Assert.Equal(completed.ComputeArtifactHash(), recovered.ComputeArtifactHash()); Assert.Null(recovered.ApprovedHash);
        Assert.Equal(2, recovered.ModelCalls); Assert.Equal(1, recovered.ReplanAttempts);
        Assert.Equal(authority, pending.Request.Options["automatic_correction_progress"]!.ToJsonString());
        await planner.AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(1, reads);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(recovered,
            new() { ExpectedRevision = recovered.Revision - 1 }, runtime, PlannerFixture.Ct));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("calls")]
    [InlineData("corrections")]
    public async Task RemovalDoesNotBypassPolicyOrBudgets(string boundary)
    {
        var (runtime, state) = await Fixture();
        if (boundary == "disabled") state.Request.Options.Remove("automatic_correction");
        if (boundary == "calls") state.Request.MaxModelCalls = 1;
        if (boundary == "corrections") state.Request.MaxReplanAttempts = 0;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Request.Options["automatic_correction_progress"]); Assert.Null(state.Yaml);
    }

    internal static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(string extra = "extra")
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source", new() { Tools = [new() { Name = "perform", EffectKind = "execute",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"instructions":{"type":"string"}},"required":["instructions"],"additionalProperties":false}""")!,
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")! }] });
        var runtime = new TestRuntime(new WorkflowEngine { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        runtime.Proposal = new()
        {
            Requirements = new() { Summary = "Return the requested observed result", Inputs = [],
                Outcomes = [new("result", "Return the observed text without changing the requested operation")],
                Outputs = [new() { Name = "result", Type = new() { Kind = "string" } }, new() { Name = "audit", Required = false, Type = new() { Kind = "string" } }] },
            Plan = new() { Inputs = [], Root = new()
            {
                Tasks = [new() { Id = "perform", Objective = "Perform the requested operation", Operation = catalog.Capabilities.Single(c => c.Method == "perform").Id,
                    Inputs = [new("instructions", new() { Kind = "string", Text = "Keep the original operation instructions." })] }],
                Outputs = [new("result", Port()), new("audit", Port()), new(extra, Port())]
            } }
        };
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Request.Options["automatic_correction"] = true;
        state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        return (runtime, state);
    }

    private static TaskValue Port() => new() { Kind = "output", Source = "perform", Port = "text" };
}
