using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AutomaticCorrectionTests
{
    [Fact]
    public async Task UnchangedCompilingRuntimeDraftStopsWithoutPublishingAnotherReview()
    {
        var runtime = new TestRuntime();
        var baseline = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, baseline.Status);
        var state = new PlanningSession
        {
            IntentVersion = 2, Catalog = baseline.Catalog, Requirements = baseline.Requirements,
            Request = new()
            {
                TenantId = "test", Prompt = baseline.Request.Prompt, Baseline = Clone(baseline.Plan!),
                Options = new() { ["automatic_correction"] = true },
                FailureEvidence = new() { ["error_code"] = "EXPR_TYPE_MISMATCH" }
            }
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Theory]
    [InlineData("new_operation")]
    [InlineData("operation_branch")]
    [InlineData("independent_task")]
    [InlineData("operation_dependency")]
    [InlineData("guard_order")]
    [InlineData("guard_branch")]
    public async Task RegenerationCannotChangeIndependentWorkOrExecutionBoundaries(string defect)
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Preserve literal", Outputs = [new("value", new() { Kind = "string", Text = "original" })] });
        if (defect == "guard_order") runtime.Proposal.Plan.Root.Tasks.Add(new() { Id = "later_read", Operation = runtime.Proposal.Plan.Root.Tasks[0].Operation, Objective = "Read only after the existing assertion" });
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan.Root.Tasks[1].Requires!.Items[1].Port = "ready";
        if (defect == "new_operation") runtime.Proposal.Plan.Root.Tasks.Add(new() { Id = "additional", Operation = runtime.Proposal.Plan.Root.Tasks[0].Operation, Objective = "Unrequested repeat" });
        else if (defect == "operation_branch")
        {
            var operation = runtime.Proposal.Plan.Root.Tasks[0]; runtime.Proposal.Plan.Root.Tasks.RemoveAt(0);
            runtime.Proposal.Plan.Root.Tasks.Insert(0, new() { Id = "route", Kind = "conditional", Objective = "Move the existing operation into a branch", Condition = new() { Kind = "boolean", Boolean = true }, Body = new() { Tasks = [operation] }, Otherwise = new() });
        }
        else if (defect == "operation_dependency") runtime.Proposal.Plan.Root.Tasks[0].DependsOn.Add("unrelated");
        else if (defect == "guard_order")
        {
            var guarded = runtime.Proposal.Plan.Root.Tasks[1];
            runtime.Proposal.Plan.Root.Tasks.RemoveAt(1); runtime.Proposal.Plan.Root.Tasks.Add(guarded);
        }
        else if (defect == "guard_branch")
        {
            var guarded = runtime.Proposal.Plan.Root.Tasks[1];
            runtime.Proposal.Plan.Root.Tasks[1] = new()
            {
                Id = "route", Kind = "conditional", Objective = "Make the existing assertion conditional",
                Condition = new() { Kind = "boolean", Boolean = false },
                Body = new() { Tasks = [guarded], Outputs = [new("message", new() { Kind = "output", Source = guarded.Id, Port = "message" })] },
                Otherwise = new() { Outputs = [new("message", guarded.Outputs[0].Value)] }
            };
            runtime.Proposal.Plan.Root.Outputs[0].Value.Source = "route";
        }
        else runtime.Proposal.Plan.Root.Tasks[^1].Outputs[0].Value.Text = "changed";
        if (defect is "guard_order" or "guard_branch") Assert.Empty(new TaskPlanCompiler().Compile(runtime.Proposal.Plan, state.Catalog!).Diagnostics);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task ChoiceOfTaskRepairsToItsTypedOutputWithoutInventingABusinessChoice()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        var corrected = Clone(runtime.Proposal.Plan!);
        runtime.Proposal.Plan!.Root.Outputs[0].Value.Kind = "choice";
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
        runtime.Proposal.Plan = corrected;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Empty(state.Plan!.Choices); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
    }

    [Fact]
    public async Task MissingProducerRegeneratesDirectlyInsteadOfPatchingOnlyItsConsumer()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        var corrected = Clone(runtime.Proposal.Plan!);
        runtime.Proposal.Plan!.Root.Tasks.Clear();
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan = corrected;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
    }

    [Fact]
    public async Task CommittedCorrectionReceiptUsesItsOriginalAuthorityAtExhaustedCallLimit()
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true; state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = "ready";
        runtime.Respond = (_, _) => throw new InvalidOperationException("Simulated interruption after dispatch; receipt retained separately");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.NotNull(state.PendingCall);
        var issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        var receipt = TestRuntime.PatchResponse(state.PendingCall.Request, state, runtime.Proposal.Plan);
        var receiptRuntime = new ReceiptOnly(runtime, issued, receipt);
        var recovered = PlannerFixture.Clone(state); recovered.Status = PlanningStatus.Generating;
        recovered = await planner.AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, receiptRuntime, PlannerFixture.Ct);
        Assert.True(recovered.Status == PlanningStatus.FinalReview, string.Join(';', recovered.Diagnostics));
        Assert.Equal(2, recovered.ModelCalls); Assert.Equal(1, recovered.ReplanAttempts); Assert.Equal(1, receiptRuntime.Reads);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(issued, JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(recovered.Revision, (await planner.AdvanceAsync(PlannerFixture.Clone(recovered), new() { ExpectedRevision = recovered.Revision }, receiptRuntime, PlannerFixture.Ct)).Revision);
        var otherTenant = PlannerFixture.Clone(state); otherTenant.Status = PlanningStatus.Generating; otherTenant.Request.TenantId = "other";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(otherTenant, new() { ExpectedRevision = otherTenant.Revision }, receiptRuntime, PlannerFixture.Ct));
        Assert.Equal(1, receiptRuntime.Reads);
    }
    [Fact]
    public async Task IncompleteStructuredProposalCanRegenerateWithinTheSameAllowance()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) => runtime.Calls.Count == 1 ? new() { Json = new JsonObject { ["plan"] = new JsonObject() } } : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task RejectedProposalDoesNotLoseAcceptedIntentBeforeRegeneration()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["automatic_correction"] = true;
        state.Requirements = PlannerFixture.Requirements();
        state.Diagnostics = [new("REQUIREMENTS_INPUTS_CHANGED", "/inputs", "The rejected candidate changed the accepted interface.")];
        state.ModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Empty(state.Plan!.Inputs); Assert.Equal("Return a greeting", state.Requirements!.Summary);
    }

    [Theory]
    [InlineData("available", "available},{")]
    [InlineData("prêt", "prêt},{")]
    public async Task ProtectedReferenceUsesNewTargetedAuthorityAndOneCumulativeRepair(string field, string invalid)
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", field, invalid);
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
        var before = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
        runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = field;
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        var context = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!;
        Assert.Equal("/tasks/consume/requires", Assert.Single(context["editablePaths"]!.AsArray())!.ToString());
        Assert.Equal("/tasks/consume/requires", Assert.Single(context["slots"]!.AsArray())!["location"]!.ToString());
        before["root"]!["tasks"]![1]!["requires"]!["items"]![1]!["port"] = field;
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task ProducerDeclarationAndItsConsumerAreCorrectedTogether()
    {
        var (runtime, state) = await ExtractionProducerRevisionTests.Fixture("observe", "adapt", "pages", "rows");
        var corrected = Clone(runtime.Proposal.Plan!);
        runtime.Proposal.Plan!.Root.Tasks[1].Each = new("missing_input", "rows");
        runtime.Proposal.Plan.Root.Tasks[1].ResultType!.Fields[0].Type.Items = new() { Kind = "string" };
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan = corrected;
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        var paths = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!["editablePaths"]!.AsArray().Select(p => p!.ToString());
        Assert.Contains("/tasks/adapt/each", paths); Assert.Contains("/tasks/adapt/resultType", paths);
    }

    [Fact]
    public async Task EmptyPatchEscalatesOnceWithoutResettingCounters()
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
        runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = "ready";
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[2].StructuredOutputSchema!.AsObject()));
        Assert.NotNull(state.Request.Baseline);
    }

    [Fact]
    public async Task RemovingAssertionsCannotBecomeASuccessfulRepairOrRegeneration()
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[1].Requires = new() { Kind = "boolean", Boolean = true };
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(3, runtime.Calls.Count); Assert.Equal(2, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/consume/requires");
        Assert.Equal("and", state.Request.Baseline!.Root.Tasks[1].Requires!.Predicate);
        var recovered = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(state.Revision, recovered.Revision); Assert.Equal(3, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("LLM_BUDGET_EXCEEDED")]
    [InlineData("LLM_BUDGET_UNVERIFIABLE")]
    [InlineData("MODEL_DISPATCH_UNVERIFIABLE")]
    [InlineData("TASK_OPERATION_DENIED")]
    [InlineData("TASK_COMPILER_VALIDATION")]
    [InlineData("UNKNOWN_NEW_ERROR")]
    public async Task NonCompositionFailuresNeverDispatchCorrections(string code)
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        state.Diagnostics = [new(code, "/tasks/consume", "Retained failure")];
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;

    private sealed class ReceiptOnly(TestRuntime runtime, string issued, JsonNode response) : IPlanningRuntime
    {
        internal int Reads;
        public ICapabilityCatalog Capabilities => runtime.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("No discovery during receipt application");
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal("replan", purpose); Reads++; return Task.FromResult(new LLMResponse { Json = response.DeepClone() });
        }
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => runtime.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => runtime.ValidateCatalogAsync(catalog, ct);
    }

    [Fact]
    public async Task ActualRequestIncludesBoundedFailureFactsAndNoRecordedPayload()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.FailureEvidence = new JsonObject { ["error_code"] = "CONTRACT_UNSATISFIED", ["error_message"] = "PRIVATE_MESSAGE",
            ["details"] = new JsonObject { ["failed_step_id"] = "extract", ["last_error"] = new JsonObject
                { ["mapping_result_path"] = "$/status/success", ["mapping_failure_kind"] = "program", ["mapping_failure_reason"] = "ungrounded_scalar",
                    ["mapping_attempts"] = 2, ["sources"] = "PRIVATE_SNAPSHOT", ["script"] = "PRIVATE_SCRIPT" } } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var prompt = Assert.Single(runtime.Calls).Prompt;
        Assert.Contains("executionFailure", prompt); Assert.Contains("$/status/success", prompt); Assert.Contains("ungrounded_scalar", prompt);
        Assert.DoesNotContain("PRIVATE_", prompt);
    }
}
