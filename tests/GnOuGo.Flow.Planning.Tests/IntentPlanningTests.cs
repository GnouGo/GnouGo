using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class IntentPlanningTests
{
    [Fact]
    public async Task ReasoningOnlyOutputLimitStopsWithoutRepairOrAutomaticEscalation()
    {
        var runtime = new TestRuntime { Respond = _ => new()
        {
            CompletionStatus = "output_limit", Text = "",
            Usage = JsonNode.Parse("""{"prompt_tokens":12,"completion_tokens":8192,"completion_tokens_details":{"reasoning_tokens":8192},"total_tokens":8204}""")
        } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("MODEL_OUTPUT_LIMIT", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(8192, Assert.Single(runtime.Calls).MaxTokens);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
        Assert.Null(state.IntentPlan); Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.PendingCall);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(32768)]
    public async Task ExplicitOutputCeilingIsPreservedDuringInterpretation(int ceiling)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Generation.MaxOutputTokens = ceiling;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var request = Assert.Single(runtime.Calls);
        Assert.Equal(ceiling, request.MaxTokens); Assert.Equal("medium", request.Reasoning);
        Assert.True(request.RequireOutputTokenLimit); Assert.Equal(0, state.RepairAttempts);
    }

    [Fact]
    public async Task PendingInterpretationKeepsItsPhaseAfterProviderFailure()
    {
        var runtime = new TestRuntime { Respond = _ => throw new IOException("Provider unavailable") };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal("intent", state.PendingCall!.Purpose);
        state.Status = PlanningStatus.Generating; runtime.Respond = null;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(0, state.RepairAttempts); Assert.Equal(1, state.ModelCalls);
    }

    [Fact]
    public async Task PendingRepairResumesEvenAfterTransportDiagnosticsAreCleared()
    {
        var bad = PlannerFixture.Greeting(); ((CalculateIntentOperation)bad.Operations[0]).Value = new() { Kind = "compute", Text = "unknownBusinessValue" };
        var runtime = new TestRuntime(bad); var state = PlannerFixture.Session(); var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        runtime.Respond = _ => throw new IOException("Provider unavailable");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal("repair", state.PendingCall!.Purpose);
        state.Status = PlanningStatus.Generating; state.Diagnostics.Clear();
        runtime.Respond = null; runtime.Plans.Clear(); runtime.Plans.Enqueue(PlannerFixture.Greeting());
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.RepairAttempts); Assert.Equal(2, state.ModelCalls);
    }

    [Fact]
    public async Task RepairUsesCurrentIntentWithoutRepeatingTheRevisionBaseline()
    {
        var bad = PlannerFixture.Greeting(); ((CalculateIntentOperation)bad.Operations[0]).Value = new() { Kind = "compute", Text = "unknownBusinessValue" };
        var runtime = new TestRuntime(bad); runtime.Plans.Enqueue(PlannerFixture.Greeting());
        var state = PlannerFixture.Session();
        var catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        state.Request.Baseline = PlannerFixture.Greeting("original-only-marker");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Contains("original-only-marker", runtime.Calls[0].Prompt);
        Assert.DoesNotContain("original-only-marker", runtime.Calls[1].Prompt);
        Assert.Contains("COMPUTATION_BINDING_INVALID", runtime.Calls[1].Prompt);
    }

    [Fact]
    public async Task InputBudgetPreflightDoesNotConsumeARepairAttempt()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT" && d.Message.Contains("512"));
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "configure_generation", ExpectedRevision = state.Revision,
            Generation = new() { MaxInputTokensPerRequest = 12_000 } }, runtime, TestContext.Current.CancellationToken);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.RepairAttempts);
    }

    [Fact]
    public async Task UnresolvedHolesAndIndependentContractErrorsAreReportedTogether()
    {
        var plan = PlannerFixture.Greeting(); ((CalculateIntentOperation)plan.Operations[0]).Value = new() { Kind = "compute", Text = "unknownBusinessValue" };
        plan.Outputs[0] = new("message", new() { Kind = "missing" });
        var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state = await PlannerFixture.RunAsync(new TestRuntime(plan), state);
        Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED");
        Assert.Contains(state.Diagnostics, d => d.Code == "COMPUTATION_BINDING_INVALID");
        Assert.Equal(1, state.ModelCalls);
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "REPAIR_NO_PROGRESS");
    }

    [Fact]
    public async Task RepairInputPreflightDoesNotConsumeAnAttempt()
    {
        var plan = PlannerFixture.Greeting(); ((CalculateIntentOperation)plan.Operations[0]).Value = new() { Kind = "compute", Text = "unknownBusinessValue" };
        var runtime = new TestRuntime(plan); runtime.Plans.Enqueue(PlannerFixture.Greeting());
        var planner = new TypedWorkflowPlanner(); var state = PlannerFixture.Session();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT");
        Assert.Contains(state.Diagnostics, d => d.Code == "COMPUTATION_BINDING_INVALID");
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
        state = await planner.AdvanceAsync(state, new() { Kind = "configure_generation", ExpectedRevision = state.Revision,
            Generation = new() { MaxInputTokensPerRequest = 12_000 } }, runtime, TestContext.Current.CancellationToken);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
    }

    [Fact]
    public async Task CompleteWorkflowUsesOneInterpretationCallAndExecutes()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("\n", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Single(runtime.Calls); Assert.Equal(1, runtime.Discoveries); Assert.Null(state.ApprovedHash);
        Assert.All(state.Scenarios, s => Assert.Equal("passed", s.Outcome));
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs!["message"]!.ToString());
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Approved, state.Status); Assert.Single(runtime.Calls);
    }
    [Fact]
    public async Task RepairMayReplaceStructureAndReturnsToFullValidation()
    {
        var bad = PlannerFixture.Greeting(); ((CalculateIntentOperation)bad.Operations[0]).Value = new() { Kind = "compute", Text = "unknownBusinessValue" };
        var runtime = new TestRuntime(bad); runtime.Plans.Enqueue(PlannerFixture.Greeting("Fixed"));
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.RepairAttempts); Assert.Equal(2, state.ModelCalls);
        Assert.Contains("COMPUTATION_BINDING_INVALID", runtime.Calls[1].Prompt); Assert.Contains("Fixed", state.Yaml);
    }
    [Fact]
    public async Task InvalidJsonStopsAfterTwoRepairs()
    {
        var runtime = new TestRuntime { Respond = _ => new() { Text = "not json" } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(3, runtime.Calls.Count); Assert.Equal(2, state.RepairAttempts);
    }
    [Fact]
    public async Task ModelCannotSmuggleHostPolicyInItsResponse()
    {
        var runtime = new TestRuntime { Respond = r =>
        {
            var response = PlanningJsonTransport.Intent(PlannerFixture.Greeting()).AsObject();
            response["policy"] = new JsonObject { ["requireExternalConfirmation"] = false }; return new() { Json = response };
        }};
        var state = await PlannerFixture.RunAsync(runtime); Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID");
    }
    [Fact]
    public async Task BudgetsAndApprovalSurviveRevisionAndRejectStaleCommands()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision - 1, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken));
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Change the greeting" }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, state.ModelCalls); Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml); Assert.NotNull(state.Request.Baseline);
    }
    [Fact]
    public async Task AlteredYamlCannotBeApproved()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); state.Yaml += "\n# altered";
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ChangedCatalogInvalidatesReview()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        runtime.CatalogChanges = [new("CATALOG_CHANGED", "$", "Changed")];
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.ApprovedHash);
    }
    [Fact]
    public async Task ClarificationIsTypedAndRetainsCallBudget()
    {
        var question = PlannerFixture.Greeting(); question.Questions.Add(new("language", "Which language?", new() { Enum = ["English", "French"] }));
        var runtime = new TestRuntime(question); runtime.Plans.Enqueue(PlannerFixture.Greeting()); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<ArgumentException>(() => planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["language"] = 7 } }, runtime, TestContext.Current.CancellationToken));
        state = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["language"] = "French" } }, runtime, TestContext.Current.CancellationToken);
        state = await PlannerFixture.RunAsync(runtime, state); Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls);
    }
    [Fact]
    public void CoreAndPlannerRemainIndependentlyPublishable()
    {
        Assert.DoesNotContain(typeof(IWorkflowPlanner).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("GnOuGo."));
        Assert.Equal(["GnOuGo.Flow.Core"], typeof(TypedWorkflowPlanner).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("GnOuGo.")).Select(a => a.Name));
        Assert.DoesNotContain(typeof(PlanningSession).GetProperties(), p => p.Name.Contains("Proof") || p.Name.Contains("Fingerprint") || p.Name.Contains("Behavior"));
    }
}
