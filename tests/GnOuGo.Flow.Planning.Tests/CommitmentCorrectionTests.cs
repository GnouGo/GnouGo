using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CommitmentCorrectionTests
{
    [Fact]
    public async Task DisconnectedExportUsesOneOrdinaryPatchWithoutStructuralAuthority()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        var correct = Clone(runtime.Proposal.Plan!);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("invented"));
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Calls.Count == 1 ? runtime.Proposal : new() { Plan = correct });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.True(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal("5", progress["version"]!.ToString()); Assert.Equal("patch", progress["action"]!.ToString());
        Assert.Empty(progress["operationAdditions"]!.AsArray());
        Assert.Equal("verified", Assert.Single(state.ValidationResults, r => r.Id == "coverage:deliver:0:result").Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCommittedOperationUsesOneCapturedStructuralAddition(bool simulated)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        var correct = Clone(runtime.Proposal.Plan!); var operation = correct.Root.Tasks[0].Operation;
        runtime.Proposal.Plan!.Root.Tasks.Clear();
        if (simulated)
        {
            runtime.Proposal.Plan.Root.Tasks.Add(new() { Id = "simulated", Kind = "transform", Mode = "interpret", Objective = "Pretend to perform external work",
                Inputs = [new("intent", CommitmentCoverageTests.Text("supplied instructions"))], ResultType = new() { Kind = "object", Fields = [new() { Name = "location", Type = new() { Kind = "string" } }] } });
            runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Output("simulated", "location"));
        }
        else runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("not performed"));
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Calls.Count == 1 ? runtime.Proposal : new() { Plan = correct });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal("5", progress["version"]!.ToString()); Assert.Equal("regenerate", progress["action"]!.ToString());
        Assert.Equal(operation, Assert.Single(progress["operationAdditions"]!.AsArray())!["operation"]!.ToString());
        Assert.Contains("authorize adding only", runtime.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(state.Plan!.Root.Tasks, t => t.Kind == "transform");
        Assert.Single(state.Catalog!.Capabilities, c => c.Method == "perform");
        var calls = runtime.Calls.Count;
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(calls, runtime.Calls.Count); Assert.Equal(state.ComputeArtifactHash(), recovered.ComputeArtifactHash());
    }

    [Theory]
    [InlineData("extra_operation")]
    [InlineData("extra_occurrence")]
    [InlineData("unrelated_value")]
    [InlineData("unrelated_output")]
    [InlineData("weaken_guard")]
    public async Task StructuralAdditionNeverGrantsUnrelatedChanges(string defect)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        var desired = Clone(runtime.Proposal.Plan!);
        var stable = new PlanTask { Id = "independent", Kind = "value", Objective = "Retain this checked value", Outputs = [new("text", CommitmentCoverageTests.Text("exact"))],
            Requires = new() { Kind = "predicate", Predicate = "equal", Items = [CommitmentCoverageTests.Text("a"), CommitmentCoverageTests.Text("a")] } };
        runtime.Proposal.Plan!.Root.Tasks = [stable];
        runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("missing"));
        desired.Root.Tasks.Insert(0, Clone(runtime.Proposal.Plan).Root.Tasks[0]);
        runtime.Proposal.Requirements!.Outputs!.Add(new() { Name = "stable", Type = new() { Kind = "string" } });
        runtime.Proposal.Plan.Root.Outputs.Add(new("stable", CommitmentCoverageTests.Output("independent", "text")));
        desired.Root.Outputs.Add(new("stable", CommitmentCoverageTests.Output("independent", "text")));
        if (defect.StartsWith("extra_", StringComparison.Ordinal)) desired.Root.Tasks.Add(new() { Id = "unrequested", Objective = "Additional effect",
            Operation = defect == "extra_operation" ? "unknown_operation" : desired.Root.Tasks[1].Operation });
        if (defect == "unrelated_value") desired.Root.Tasks[0].Outputs[0].Value.Text = "changed";
        if (defect == "unrelated_output") desired.Root.Outputs[1] = new("stable", CommitmentCoverageTests.Text("changed"));
        if (defect == "weaken_guard") desired.Root.Tasks[0].Requires = new() { Kind = "boolean", Boolean = true };
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count == 1) return TestRuntime.Response(request, runtime.Proposal);
            if (defect == "extra_operation")
            {
                var allowedShape = Clone(desired); allowedShape.Root.Tasks.RemoveAt(allowedShape.Root.Tasks.Count - 1);
                var response = TestRuntime.Response(request, new() { Plan = allowedShape });
                response.Json!["plan"]!["root"]!["tasks"]!.AsArray().Add(JsonSerializer.SerializeToNode(desired.Root.Tasks[^1], PlanningJsonContext.Default.PlanTask));
                return response;
            }
            return TestRuntime.Response(request, new() { Plan = desired });
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code is "REVISION_SCOPE_CHANGED" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal("exact", state.Request.Baseline!.Root.Tasks[0].Outputs[0].Value.Text);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("exhausted")]
    [InlineData("historical")]
    public async Task NoProgressAndHistoricalAuthoritiesRemainBounded(string variant)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Root.Tasks.Clear(); runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("absent"));
        if (variant == "exhausted") state.Request.MaxModelCalls = 1;
        if (variant == "historical") state.IntentVersion = 2;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(variant == "historical" ? PlanningStatus.FinalReview : PlanningStatus.Stopped, state.Status);
        Assert.Equal(variant == "same" ? 2 : 1, state.ModelCalls);
        Assert.Equal(variant == "same" ? 1 : 0, state.ReplanAttempts);
        if (variant == "same") Assert.Contains(state.Diagnostics, d => d.Code is "REPLAN_NO_PROGRESS" or PlanningCommitments.MissingOperation);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task CommittedStructuralResponseRetainsCapturedAdditionsAtTheExhaustedLimit()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true; state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        var corrected = Clone(runtime.Proposal.Plan!);
        runtime.Proposal.Plan!.Root.Tasks.Clear(); runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("absent"));
        var planner = new HybridWorkflowPlanner(); state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Respond = (_, _) => throw new InvalidOperationException("Interrupted after the committed response");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.NotNull(state.PendingCall); Assert.Equal(2, state.ModelCalls);
        var issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        var response = TestRuntime.Response(state.PendingCall.Request, new() { Plan = corrected }).Json!;
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var receipt = new ReceiptOnly(runtime, issued, response);
        var resumed = PlannerFixture.Clone(state); resumed.Status = PlanningStatus.Generating;
        resumed = await planner.AdvanceAsync(resumed, new() { ExpectedRevision = resumed.Revision }, receipt, PlannerFixture.Ct);
        Assert.True(resumed.Status == PlanningStatus.FinalReview, string.Join(';', resumed.Diagnostics));
        Assert.Equal(2, resumed.ModelCalls); Assert.Equal(1, resumed.ReplanAttempts); Assert.Equal(1, receipt.Reads);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        resumed = await planner.AdvanceAsync(PlannerFixture.Clone(resumed), new() { ExpectedRevision = resumed.Revision }, receipt, PlannerFixture.Ct);
        Assert.Equal(1, receipt.Reads); Assert.Null(resumed.ApprovedHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPlacementOrDeniedPermissionDoesNotAuthorizeAnotherEffect(bool denied)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        var operation = runtime.Proposal.Plan!.Root.Tasks[0];
        runtime.Proposal.Plan.Root.Tasks.Clear();
        runtime.Proposal.Plan.Root.Outputs[0] = new("filePath", CommitmentCoverageTests.Text("not performed"));
        if (denied)
        {
            var capability = state.Catalog!.Capabilities.Single(c => TaskOperations.Describe(c).Id == operation.Operation);
            state.Catalog.Policy.DeniedCapabilityIds.Add(capability.Id);
            state.Request.Policy.DeniedCapabilityIds.Add(capability.Id);
        }
        else runtime.Proposal.Plan.Groups.Add(new() { Id = "unused", Body = new() { Tasks = [operation], Outputs = [new("location", CommitmentCoverageTests.Output(operation.Id, "location"))] } });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Message.Contains("no corrective call", StringComparison.Ordinal));
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
    }

    private sealed class ReceiptOnly(TestRuntime runtime, string issued, JsonNode response) : IPlanningRuntime
    {
        internal int Reads;
        public ICapabilityCatalog Capabilities => runtime.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("No discovery during recovery");
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        { Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)); Reads++; return Task.FromResult(new LLMResponse { Json = response.DeepClone() }); }
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => runtime.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => runtime.ValidateCatalogAsync(catalog, ct);
    }

    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
}
