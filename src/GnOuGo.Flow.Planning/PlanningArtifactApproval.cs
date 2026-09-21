using GnOuGo.Flow.Core.Planning;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning;
public static class PlanningArtifactApproval
{
    public static string? Hash(PlanningSession state) => state.ComputeArtifactHash();
    public static void Verify(PlanningSession state)
    {
        if (state.SchemaVersion != 8 || state.SemanticPlan is null || state.Grounding is null || state.GroundedPlan is null ||
            state.Graph is null || state.Catalog is null || state.Yaml is null || state.PendingCall is not null ||
            state.Diagnostics.Any(d => d.Required) || state.Scenarios.Count == 0 || state.Scenarios.Any(s => s.Outcome != "passed"))
            throw new PlanningConflictException("Approval requires complete schema-8 planning artifacts and passing scenarios.");
        if (SemanticPlanning.Validate(state.SemanticPlan).Count > 0 || CapabilityGrounder.ValidateBindings(state).Count > 0)
            throw new PlanningConflictException("The saved semantic plan and capability grounding are invalid.");
        var validated = GroundedPlanValidator.Validate(state.GroundedPlan, state.Catalog);
        if (validated.Plan is null) throw new PlanningConflictException("The saved grounded plan failed deterministic revalidation.");
        var rebuilt = PlanningGraphBuilder.Build(validated.Plan);
        PlanningConfirmationGuards.Apply(rebuilt, state.Catalog);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(rebuilt, PlanningJsonContext.Default.PlanningGraph),
                JsonSerializer.SerializeToNode(state.Graph, PlanningJsonContext.Default.PlanningGraph)) ||
            new PlanningGraphCompiler().Compile(rebuilt, state.Catalog, state.Request.Name) != state.Yaml)
            throw new PlanningConflictException("Approval requires the exact validated graph and compiled artifact.");
    }
}
