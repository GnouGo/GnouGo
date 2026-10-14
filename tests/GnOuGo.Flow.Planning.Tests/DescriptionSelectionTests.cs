using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DescriptionSelectionTests
{
    [Fact]
    public async Task FreshGenerationAndExplicitRevisionReceiveDescriptionGuidance()
    {
        var runtime = new TestRuntime();
        var planner = new HybridWorkflowPlanner();
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            Text = "Keep the accepted business result and explain the same observed-block selection rule." }, runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        foreach (var request in runtime.Calls)
        {
            Assert.Contains("heading optional", request.Prompt);
            Assert.Contains("offered/original lookup", request.Prompt);
            Assert.Contains("Declared flags:", request.Prompt);
            Assert.Contains("\"port\":\"success\"", request.Prompt);
            Assert.Contains(PlanningSchemaReferences.Walk(request.StructuredOutputSchema!.AsObject(), "", 0),
                entry => entry.Schema["description"]?.ToString().Contains("requires: false fails", StringComparison.Ordinal) == true);
            Assert.Contains("several suitable!=absent", request.Prompt);
            Assert.Contains("absent/uncertain subject", request.Prompt);
            Assert.Contains("Verbatim;no controls", request.Prompt);
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()),
                1, state.Request.Generation.MaxInputTokensPerRequest);
        }
        // The actual issued typed-repair envelope also receives the instruction,
        // without acquiring permission to change a task's objective.
        state.Diagnostics = [new("TASK_VALUE_TYPE", "/tasks/greet/outputs/message", "Incompatible value")];
        state.RevisionScope = ["/tasks/greet/outputs/message"];
        var repair = new PlanningPrompt(state).Request();
        Assert.Contains("heading optional", repair.Prompt);
        Assert.DoesNotContain("editablePaths\":[\"/tasks/greet/objective", repair.Prompt);
    }

}
