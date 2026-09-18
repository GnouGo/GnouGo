using GnOuGo.Flow.Core.Planning;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning;
public static class PlanningArtifactApproval
{
    public static string? Hash(PlanningSession state) => state.ComputeArtifactHash();
    public static void Verify(PlanningSession state)
    {
        if (state.Graph is null || state.Catalog is null || state.Yaml is null || state.PendingCall is not null ||
            state.Diagnostics.Any(d => d.Required) || state.Scenarios.Count == 0 || state.Scenarios.Any(s => s.Outcome != "passed") ||
            new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name) != state.Yaml)
            throw new PlanningConflictException("Approval requires the exact validated graph, scenarios, and compiled artifact.");
    }
}
