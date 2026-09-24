using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
public static class PlanningArtifactApproval
{
    public static string? Hash(PlanningSession state) => state.ComputeArtifactHash();
    public static void Verify(PlanningSession state)
    {
        if (state.SchemaVersion != 9 || state.Requirements is null || state.Graph is null || state.Catalog is null ||
            state.Yaml is null || state.PendingCall is not null || state.Diagnostics.Any(d => d.Required) ||
            state.Requirements.Outcomes.Count == 0 || state.Requirements.Questions.Count != 0)
            throw new PlanningConflictException("Approval requires a complete schema-9 graph. Regenerate incompatible workflows.");
        if (PlanningGeneratedGraph.Validate(PlanningConfirmationGuards.UserGraph(state.Graph), state.Requirements, state.Catalog).Any(d => d.Required) ||
            PlanningExecutableValidation.Validate(state.Graph, state.Catalog).Any(d => d.Required) ||
            new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name) != state.Yaml)
            throw new PlanningConflictException("The saved graph does not match its validated executable artifact.");
    }
}
