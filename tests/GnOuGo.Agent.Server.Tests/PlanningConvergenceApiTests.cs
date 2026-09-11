using System.Text.Json;
using GnOuGo.Agent.Shared;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningConvergenceApiTests
{
    [Fact]
    public void ProgressPublishesSeparateChoiceCountsAndPlanLevelGates()
    {
        var state = new PlanningSnapshot();
        state.Construction.Workflows.Add(new() { WorkflowKey = "main", TotalHoles = 5, ResolvedHoles = 3, UnresolvedHoles = 2,
            DeterministicallyResolvedHoles = 2, ModelHoles = 2, ModelHoleExposures = 3,
            HoleChoices = [new("field", 1, 4)] });
        state.GateProgress.Add(new() { WorkflowKey = "$plan", Gate = PlanningGates.Behavior, Failures = 2 });
        state.RepairAllowances.Add(new() { WorkflowKey = "$plan", Gate = PlanningGates.Behavior, Attempts = 3 });
        var dto = PlanningEndpoints.ToDto(state); var progress = Assert.Single(dto.Workflows!);
        Assert.Equal(5, progress.TotalHoles); Assert.Equal(2, progress.DeterministicallyResolvedHoles);
        Assert.Equal(2, progress.ModelHoles); Assert.Equal(3, progress.ModelHoleExposures);
        var hole = Assert.Single(progress.HoleChoices!); Assert.Equal(1, hole.DirectBindings); Assert.Equal(4, hole.ComputationParameters);
        var gate = Assert.Single(dto.Gates!); Assert.Equal("$plan", gate.WorkflowKey); Assert.Equal(3, gate.Repairs); Assert.Equal(2, gate.Failures);
        var json = JsonSerializer.Serialize(dto, ChatJsonContext.Default.PlanningSessionDto);
        Assert.Contains("modelHoleExposures", json); Assert.Contains("computationParameters", json);
    }
}
