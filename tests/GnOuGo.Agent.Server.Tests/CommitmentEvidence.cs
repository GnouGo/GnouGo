using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

internal static class CommitmentEvidence
{
    internal static async Task WriteCompilingBaseline(PlanningSession prototype, IPlanningRuntime runtime, string name, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_COMMITMENT_EVIDENCE") is not { Length: > 0 } directory) return;
        var state = JsonSerializer.SerializeToNode(prototype, PlanningJsonContext.Default.PlanningSession)!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        state.IntentVersion = 2; state.Status = PlanningStatus.Clarification;
        state.Request.Options.Remove("automatic_correction"); state.Request.Options.Remove("clarification_policy");
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "configure_mode", Mode = PlanningMode.Auto, ExpectedRevision = state.Revision }, runtime, ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(prototype.ModelCalls, state.ModelCalls);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + "-before.yaml"), state.Yaml, ct);
    }
}
