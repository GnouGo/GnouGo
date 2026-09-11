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
            DeterministicSchemaHoles = 1, ModelSchemaHoles = 1, ModelRequired = 2, ModelUsed = 1,
            HoleChoices = [new("field", 1, 4)] });
        state.GateProgress.Add(new() { WorkflowKey = "$plan", Gate = PlanningGates.Behavior, Failures = 2 });
        state.RepairAllowances.Add(new() { WorkflowKey = "$plan", Gate = PlanningGates.Behavior, Attempts = 3 });
        state.RequestCounts.Add(new("$plan", "behavior", PlanningGates.Behavior, 2, 1, 1, 2000, null, null, null, null));
        var dto = PlanningEndpoints.ToDto(state); var progress = Assert.Single(dto.Workflows!);
        Assert.Equal(5, progress.TotalHoles); Assert.Equal(2, progress.DeterministicallyResolvedHoles);
        Assert.Equal(2, progress.ModelHoles); Assert.Equal(3, progress.ModelHoleExposures);
        Assert.Equal(1, progress.DeterministicSchemaHoles); Assert.Equal(1, progress.ModelSchemaHoles);
        Assert.Equal(2, progress.ModelRequired); Assert.Equal(1, progress.ModelUsed);
        var usage = Assert.Single(dto.RequestCounts!); Assert.Equal(2, usage.Reservations); Assert.Equal(1, usage.Unverifiable); Assert.Null(usage.InputTokens);
        var hole = Assert.Single(progress.HoleChoices!); Assert.Equal(1, hole.DirectBindings); Assert.Equal(4, hole.ComputationParameters);
        var gate = Assert.Single(dto.Gates!); Assert.Equal("$plan", gate.WorkflowKey); Assert.Equal(3, gate.Repairs); Assert.Equal(2, gate.Failures);
        var json = JsonSerializer.Serialize(dto, ChatJsonContext.Default.PlanningSessionDto);
        Assert.Contains("modelHoleExposures", json); Assert.Contains("computationParameters", json);
    }
}
