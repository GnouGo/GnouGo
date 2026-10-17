using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DescriptionSelectionTests
{
    [Fact]
    public async Task DescriptionCriteriaComeFromTheRequestAndSurviveRevisionAndRepair()
    {
        var runtime = new TestRuntime();
        var planner = new HybridWorkflowPlanner();
        const string criteria = "Select one complete description or characteristics block even without a heading; retain null for genuine absence or uncertain subject association.";
        var initial = PlannerFixture.Session(); initial.Request.Prompt += " " + criteria;
        var state = await PlannerFixture.RunAsync(runtime, initial);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            Text = "Keep the accepted business result and explain the same observed-block selection rule." }, runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        foreach (var request in runtime.Calls)
        {
            var instructions = request.Prompt[..request.Prompt.IndexOf("{\"request\"", StringComparison.Ordinal)];
            Assert.DoesNotContain("YAML", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(criteria, request.Prompt);
            Assert.DoesNotContain(criteria, instructions);
            Assert.DoesNotContain("Requested descriptions:", instructions);
            Assert.Contains("Keep original IDs/exact values", instructions);
            Assert.Contains("Lookup offered IDs before originals", instructions);
            Assert.Contains(PlanningSchemaReferences.Walk(request.StructuredOutputSchema!.AsObject(), "", 0),
                entry => entry.Schema["description"]?.ToString().Contains("requires: false fails", StringComparison.Ordinal) == true);
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()),
                1, state.Request.Generation.MaxInputTokensPerRequest);
        }
        // The actual issued typed-repair envelope also receives the instruction,
        // without acquiring permission to change a task's objective.
        state.Diagnostics = [new("TASK_VALUE_TYPE", "/tasks/greet/outputs/message", "Incompatible value")];
        state.RevisionScope = ["/tasks/greet/outputs/message"];
        var repair = new PlanningPrompt(state).Request();
        Assert.Contains(criteria, repair.Prompt);
        Assert.DoesNotContain("Requested descriptions:", repair.Prompt);
        Assert.DoesNotContain("editablePaths\":[\"/tasks/greet/objective", repair.Prompt);
    }

}
