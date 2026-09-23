using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticRepairCheckpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningSession State(bool large = false)
    {
        var state = SemanticGroundingTests.State();
        state.SemanticPlan!.Actions.Insert(1, new() { Id = "untouched", Kind = "calculate", Purpose = "Preserve unrelated work", Outputs = [new("value", "Stable output")] });
        if (large) foreach (var capability in state.Catalog!.Capabilities) capability.Description = new string('x', 20000);
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(id => new GroundingDecision(id, "matched", page.CapabilityIds.Contains("cap_0") ? [new("cap_0", "Declared behavior")] : [], "Covered"))
                .Select(d => d.Matches.Count == 0 ? d with { Outcome = "none_of_the_above" } : d).ToList()));
        state.Grounding.Selections = state.SemanticPlan.Actions.Select(a => new GroundingSelection(a.Id, ["cap_0"], "Selected")).ToList();
        state.Diagnostics = [new("SEMANTIC_BINDING_BLOCKED", "/actions/collect", "Missing observation"), new("SEMANTIC_BINDING_BLOCKED", "/actions/release", "Missing resource")];
        state.BindingProgress = new() { CurrentActions = ["collect", "release"] };
        return state;
    }

    private static JsonObject Replacement(PlanningSession state)
    {
        var actions = state.SemanticPlan!.Actions.Where(a => a.Id != "untouched").Select(a => JsonSerializer.SerializeToNode(a, PlanningJsonContext.Default.SemanticAction)!).ToArray();
        actions[0]["purpose"] = "Read the external observation before consuming its contents";
        return new() { ["actions"] = new JsonArray(actions), ["questions"] = new JsonArray() };
    }

    [Fact]
    public async Task SiblingRepairKeepsUnrelatedActionsAndCoverageExactly()
    {
        var state = State(); var unrelated = JsonSerializer.Serialize(state.SemanticPlan!.Actions[1], PlanningJsonContext.Default.SemanticAction);
        var runtime = new TestRuntime { Respond = request =>
        {
            Assert.Contains("selectedContracts", request.Prompt); Assert.Contains("acceptedBoundary", request.Prompt);
            return new() { Json = Replacement(state) };
        } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        Assert.Equal(unrelated, JsonSerializer.Serialize(state.SemanticPlan!.Actions.Single(a => a.Id == "untouched"), PlanningJsonContext.Default.SemanticAction));
        Assert.Contains(state.Grounding!.Results.SelectMany(r => r.Decisions), d => d.ActionId == "untouched");
        Assert.All(state.Grounding.Pages.Where(p => !state.Grounding.Results.Any(r => r.PageId == p.Id)), p => Assert.DoesNotContain("untouched", p.ActionIds));
        Assert.Contains(state.Grounding.RetainedSelections!, s => s.ActionId == "untouched");
        Assert.Null(state.PendingRepair); Assert.Single(runtime.Calls);
        Assert.Contains(runtime.Checkpoints, s => s.PendingRepair is not null && s.SemanticPlan!.Actions[0].Purpose == "Read the observation");
    }

    [Fact]
    public async Task SixUsedOfEightKeepsAcceptedStateAndPersistsUnappliedRepair()
    {
        var state = State(large: true); Assert.Equal(2, state.Grounding!.Pages.Count);
        state.ModelCalls = 5;
        var before = SemanticReplanning.InputHash(state);
        var runtime = new TestRuntime { Respond = _ => new() { Json = Replacement(state) } };
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Equal(6, result.ModelCalls);
        Assert.Contains(result.Diagnostics, d => d.Code == "GROUNDING_BUDGET_INSUFFICIENT" && d.Message.Contains("allowance is 2", StringComparison.Ordinal));
        Assert.Equal(before, SemanticReplanning.InputHash(result)); Assert.NotNull(result.PendingRepair); Assert.Null(result.Yaml);
        var restarted = Restore(result);
        Assert.Throws<WorkflowRuntimeException>(() => SemanticReplanning.ApplyAsync(restarted, runtime, Ct).GetAwaiter().GetResult());
        Assert.Single(runtime.Calls); Assert.Equal(6, restarted.ModelCalls);
    }

    [Fact]
    public async Task RestartAfterProposalBeforeInstallDoesNotDispatchAgain()
    {
        var state = State(); var runtime = new TestRuntime { Respond = _ => new() { Json = Replacement(state) } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        var checkpoint = Restore(runtime.Checkpoints.Single(s => s.PendingRepair is not null));
        await SemanticReplanning.ApplyAsync(checkpoint, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(1, checkpoint.ModelCalls); Assert.Equal(1, checkpoint.ReplanAttempts);
        Assert.Equal(SemanticPlanning.Hash(state.SemanticPlan!), SemanticPlanning.Hash(checkpoint.SemanticPlan!));
        Assert.Null(checkpoint.PendingRepair);
    }

    [Fact]
    public async Task ChangedCheckpointScopeIsRejectedWithoutChangingAcceptedWork()
    {
        var state = State(); var runtime = new TestRuntime { Respond = _ => new() { Json = Replacement(state) } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        var checkpoint = Restore(runtime.Checkpoints.Single(s => s.PendingRepair is not null));
        checkpoint.Catalog!.Capabilities[0].Description += " Changed";
        var before = SemanticReplanning.InputHash(checkpoint);
        await Assert.ThrowsAsync<PlanningConflictException>(() => SemanticReplanning.ApplyAsync(checkpoint, runtime, Ct));
        Assert.Equal(before, SemanticReplanning.InputHash(checkpoint)); Assert.Single(runtime.Calls);
    }

    private static PlanningSession Restore(PlanningSession state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;

    [Fact]
    public async Task ReservedLegacyRepairReplaysItsOriginalBroaderScopeAndCallCount()
    {
        var state = State(); state.ModelCalls = 5; state.ReplanAttempts = 1;
        var schema = SemanticPlanning.Schema();
        schema["properties"] = new JsonObject { ["actions"] = PlanningSchemas.Array(PlanningSchemas.Ref("semanticAction")), ["questions"] = PlanningSchemas.Array(PlanningSchemas.Ref("question")) };
        schema["required"] = new JsonArray("actions", "questions");
        var prompt = "Legacy scoped request\n" + new JsonObject { ["targetIds"] = new JsonArray("collect", "untouched", "release") }.ToJsonString();
        state.PendingCall = new() { Id = "reserved", Purpose = "replan", Request = new() { Prompt = prompt, StructuredOutputSchema = schema } };
        var actions = SemanticPlanning.Json(state.SemanticPlan!)["actions"]!.DeepClone(); actions[0]!["purpose"] = "Observe the external prerequisite";
        var runtime = new TestRuntime { Respond = request =>
        {
            Assert.Equal(prompt, request.Prompt); Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema));
            return new() { Json = new JsonObject { ["actions"] = actions, ["questions"] = new JsonArray() } };
        } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(5, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BusinessScopeChangesRequireExplicitConsentAndResumeWithoutRegeneration(bool accept)
    {
        var state = State(); state.Diagnostics = [new("SEMANTIC_BINDING_BLOCKED", "/actions/collect", "Required outcome unsupported")
        { Prerequisite = new("unavailable_outcome", "Only a summarized observation is available", "evidence") }];
        var original = SemanticPlanning.Hash(state.SemanticPlan!);
        var runtime = new TestRuntime { Respond = _ => new() { Json = BusinessRevision(state) } };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Equal(original, SemanticPlanning.Hash(state.SemanticPlan!));
        Assert.Single(state.GetQuestions()); Assert.Null(state.PendingDecision); Assert.NotNull(state.PendingRepair);
        state = await planner.AdvanceAsync(Restore(state), new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["accept_scope_revision"] = accept } }, runtime, Ct);
        state = await planner.AdvanceAsync(Restore(state), new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Single(state.Answers);
        if (accept)
        {
            Assert.Null(state.PendingRepair); Assert.Empty(state.SemanticPlan!.Questions);
            Assert.Equal("Summarized observation", state.SemanticPlan.Actions.Single(a => a.Id == "collect").Outputs[0].Description);
        }
        else { Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(original, SemanticPlanning.Hash(state.SemanticPlan!)); }
    }

    [Fact]
    public async Task AutoStopsWithoutAcceptingReducedBusinessOutcome()
    {
        var state = State(); state.Request.Mode = PlanningMode.Auto;
        state.Diagnostics = [new("NONE_OF_THE_ABOVE", "/actions/collect", "Unsupported required behavior")];
        var original = SemanticPlanning.Hash(state.SemanticPlan!);
        await SemanticReplanning.ApplyAsync(state, new TestRuntime { Respond = _ => new() { Json = BusinessRevision(state) } }, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(original, SemanticPlanning.Hash(state.SemanticPlan!));
        Assert.Empty(state.Answers); Assert.NotNull(state.PendingRepair); Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task TechnicalPrerequisiteCannotTurnIntoBusinessScopeConsent()
    {
        var state = State(); var response = Replacement(state);
        response["questions"] = SemanticPlanning.Json(new() { Questions = [new("repair", "Supply the missing tool input", new() { Type = "string" })] })["questions"]!.DeepClone();
        var error = await Assert.ThrowsAsync<PlanningResponseException>(() => SemanticReplanning.ApplyAsync(state, new TestRuntime { Respond = _ => new() { Json = response } }, Ct));
        Assert.Equal("TECHNICAL_CLARIFICATION_INVALID", Assert.Single(error.Diagnostics).Code); Assert.Null(state.PendingRepair);
    }

    private static JsonObject BusinessRevision(PlanningSession state)
    {
        var action = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.SemanticPlan!.Actions.Single(a => a.Id == "collect"), PlanningJsonContext.Default.SemanticAction), PlanningJsonContext.Default.SemanticAction)!;
        action.Outputs = [new("evidence", "Summarized observation")];
        var proposal = SemanticPlanning.Json(new() { Actions = [action], Questions = [new("summary", "Would a summarized observation satisfy your need?", new() { Type = "boolean" })] });
        return new() { ["actions"] = proposal["actions"]!.DeepClone(), ["questions"] = proposal["questions"]!.DeepClone() };
    }

    [Fact]
    public async Task ValidatedIndependentPrefixSurvivesWhileDependentSuffixIsDiscarded()
    {
        var state = State();
        state.SemanticPlan!.Actions.Single(a => a.Id == "release").After = ["collect"];
        state.Grounding!.SemanticHash = SemanticPlanning.Hash(state.SemanticPlan);
        state.BindingProgress = new() { CompletedActions = ["untouched", "release"], Accepted = new()
        {
            Operations = [new CalculateGroundedOperation { Id = "stable", SemanticAction = "untouched", BusinessOutputs = [new("value", [])], Value = new() { Kind = "number", Number = 1 } },
                new CalculateGroundedOperation { Id = "dependent", SemanticAction = "release", BusinessOutputs = [new("released", [])], Value = new() { Kind = "boolean", Boolean = true } }]
        } };
        var runtime = new TestRuntime { Respond = _ => new() { Json = Replacement(state) } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        Assert.Equal(new[] { "untouched" }, state.BindingProgress!.CompletedActions);
        Assert.Equal("stable", Assert.Single(state.BindingProgress.Accepted.Operations).Id);
        Assert.Empty(state.BindingProgress.CurrentActions); Assert.Null(state.BindingProgress.Candidate);
    }
}
