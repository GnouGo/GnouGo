using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
public static class PlanningArtifactApproval
{
    public static string? Hash(PlanningSession state) => state.ComputeArtifactHash();
    public static void Verify(PlanningSession state)
    {
        if (state.SchemaVersion != 10 || state.Requirements is null || state.Plan is null || state.Graph is null || state.Catalog is null ||
            state.Yaml is null || state.PendingCall is not null || state.Diagnostics.Any(d => d.Required) ||
            state.Requirements.Outcomes.Count == 0 || state.Plan.Choices.Any(c => c.Selected is null))
            throw new PlanningConflictException("Approval requires a complete format-10 TaskPlan. Regenerate incompatible planning sessions; execution journals remain schema 9.");
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog);
        if (compilation.Graph is not { } graph || compilation.Diagnostics.Count > 0 || PlanningGeneratedGraph.Validate(graph, state.Catalog).Any(d => d.Required))
            throw new PlanningConflictException("The saved TaskPlan no longer compiles to an approvable artifact.");
        PlanningConfirmationGuards.Apply(graph, state.Catalog);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.SerializeToNode(state.Graph, PlanningJsonContext.Default.PlanningGraph)) ||
            PlanningExecutableValidation.Validate(graph, state.Catalog).Any(d => d.Required) ||
            new PlanningGraphCompiler().Compile(graph, state.Catalog, state.Request.Name) != state.Yaml)
            throw new PlanningConflictException("Recompilation does not reproduce the reviewed executable artifact.");
    }
}
