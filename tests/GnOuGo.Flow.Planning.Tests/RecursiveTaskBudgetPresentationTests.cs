using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecursiveTaskBudgetPresentationTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public async Task RenderedDraftAndReplacementKeepCapturedTotalThroughCompactionAndRestart(int maximum, bool repair)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Limits.MaxTasksPerDraft = maximum;
        state.Request.Options["construction_limits"]!["maxTasksPerDraft"] = maximum;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["targetInputTokens"] = 512;
        if (repair) state.Phase = PlanningPhase.Replanning;
        var step = JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);

        var request = PlanningConstruction.DraftRequest(state, repair);
        var restored = BusinessReviewProtocolTests.Clone(state);
        var repeated = PlanningConstruction.DraftRequest(restored, repair);
        Assert.Equal(request.Prompt, repeated.Prompt);
        Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, repeated.StructuredOutputSchema));
        var context = BusinessProjectionDeclarationsTests.Context(request);
        Assert.Equal(maximum, context["maxTasksPerDraft"]?.GetValue<int>());
        Assert.Contains("one total", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("control tasks", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("always", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("otherwise", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("branches", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("group bodies", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("continueInScope", request.Prompt, StringComparison.Ordinal);
        Assert.True(PlanningRequestMeasurements.Measure(request, repair ? "replan" : PlanningConstruction.DraftPurpose, 32_000).EstimatedInputTokens > 512);
        Assert.Equal(step, JsonSerializer.Serialize(restored.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(requirements, JsonSerializer.Serialize(restored.Requirements, PlanningJsonContext.Default.PlanningRequirements));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocallyValidArraysCannotHideTwentyFiveTasksAcrossNestedBranchesAndCleanup(bool inGroup)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = inGroup ? "group" : "sequence";
        var control = new PlanTask
        {
            Id = "block", Kind = "sequence", Objective = "Compute related local results with cleanup.",
            Body = new()
            {
                Tasks = Enumerable.Range(0, 5).Select(i => Value("outer_" + i)).Concat(Enumerable.Range(0, 3).Select(i => new PlanTask
                {
                    Id = "choose_" + i, Kind = "conditional", Objective = "Compute this requested alternative.",
                    Condition = new() { Kind = "boolean", Boolean = true },
                    Body = new() { Tasks = Enumerable.Range(0, 4).Select(j => Value("branch_" + i + "_" + j)).ToList() },
                    Otherwise = new() { Tasks = [Value("other_" + i)] }
                })).ToList(),
                Always = [Value("cleanup")]
            }
        };
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], CompleteBlock = false };
        if (inGroup) extension.Groups = [new() { Id = "worker", Body = new() { Tasks = [control] } }];
        else extension.Tasks = [control];
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var raw = JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
        var response = new JsonObject { ["extension"] = JsonNode.Parse(raw) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningCorpus.Transport(response, schema, schema)!, schema));
        var accepted = PlanningConstruction.Hash(state.Plan);
        var corrections = state.Construction.DraftCorrections;
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, extension));
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("CONSTRUCTION_DRAFT_LIMIT", diagnostic.Code);
        Assert.Equal("/root", diagnostic.Location);
        Assert.Contains("25 recursive tasks", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("maximum is 8", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(raw, JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension));
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(corrections, state.Construction.DraftCorrections);
        Assert.Empty(state.Construction.Checkpoints);
    }

    private static PlanTask Value(string id) => new() { Id = id, Kind = "value", Objective = "Retain the exact numeric result.",
        Outputs = [new("number", new() { Kind = "number", Number = 1 })] };
}
