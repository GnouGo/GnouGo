using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
public static class PlanningArtifactApproval
{
    public static string? Hash(PlanningSession state) => state.ComputeArtifactHash();
    public static void Verify(PlanningSession state) => Verify(state, null);
    // Only the coordinator and isolated test host may supply a provider-resolved receipt.
    internal static void Verify(PlanningSession state, PlanningApprovalReview? trustedReview)
    {
        PlanningApprovalReviews.VerifyRetained(state, trustedReview);
        VerifyConstruction(state);
        if (state.SchemaVersion != 11 || state.Requirements is null || state.Plan is null || state.Graph is null || state.Catalog is null ||
            state.Yaml is null || state.PendingCall is not null || state.Diagnostics.Any(d => d.Required) ||
            state.Requirements.Outcomes.Count == 0 || state.Plan.Choices.Any(c => c.Selected is null) || state.PendingQuestions is not null ||
            PlanningClarifications.InputFindings(state).Count > 0)
            throw new PlanningConflictException("Approval requires a complete format-11 TaskPlan. Regenerate incompatible planning sessions; execution journals remain schema 9.");
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog, state.Request);
        if (compilation.Graph is not { } graph || compilation.Diagnostics.Count > 0 || PlanningClarifications.OutputFindings(state, graph).Count > 0 || PlanningGeneratedGraph.Validate(graph, state.Catalog, TaskPlanCompiler.UsesCompactBindings(state.Request)).Any(d => d.Required))
            throw new PlanningConflictException("The saved TaskPlan no longer compiles to an approvable artifact.");
        if (PlanningCommitments.Validate(state, state.Requirements, final: true).Count > 0 ||
            PlanningCommitments.Assess(state).Any(r => r.Diagnostics.Any(d => d.Required)))
            throw new PlanningConflictException("Approval cannot accept a violated business commitment. Recomputed coverage disagrees with the required operations or producer results.");
        PlanningConfirmationGuards.Apply(graph, state.Catalog);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.SerializeToNode(state.Graph, PlanningJsonContext.Default.PlanningGraph)) ||
            PlanningExecutableValidation.Validate(graph, state.Catalog, PlanningTokenThresholds.OptimizationAdvisory(state.Request)).Any(d => d.Required) ||
            new PlanningGraphCompiler().Compile(graph, state.Catalog, state.Request.Name, TaskPlanCompiler.UsesNormalExports(state.Request), TaskPlanCompiler.UsesFusedBindings(state.Request), TaskPlanCompiler.UsesConsumerBindings(state.Request), TaskPlanCompiler.UsesDirectProjections(state.Request), TaskPlanCompiler.UsesNativeMappings(state.Request), TaskPlanCompiler.UsesReadableMappings(state.Request), TaskPlanCompiler.UsesLeanMappings(state.Request), TaskPlanCompiler.UsesCompactProjections(state.Request), TaskPlanCompiler.UsesInlineConsumers(state.Request), TaskPlanCompiler.UsesDirectValues(state.Request), TaskPlanCompiler.UsesCheckedControls(state.Request), TaskPlanCompiler.UsesReadableBranches(state.Request), TaskPlanCompiler.UsesSimplifiedLocals(state.Request), PlanningTokenThresholds.OptimizationAdvisory(state.Request)) != state.Yaml)
            throw new PlanningConflictException("Recompilation does not reproduce the reviewed executable artifact.");
    }

    private static void VerifyConstruction(PlanningSession state)
    {
        if (state.Construction is not { } construction || !PlanningCompactConstruction.SourcesComplete(state) ||
            !construction.Complete || construction.Candidate is not null || construction.OpenScopes.Count != 0 ||
            construction.PendingReviewContext is not null || construction.PendingReviewHash is not null ||
            construction.Checkpoints.Count == 0 || construction.Checkpoints[^1].PlanHash != PlanningConstruction.Hash(state.Plan) ||
            !HybridWorkflowPlanner.ConstructionArtifactReviewValid(state))
            throw new PlanningConflictException("Approval requires complete incremental construction and an exact global business review.");
        var requirements = state.Requirements?.Outcomes.Select(r => r.Id).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (!construction.Requirements.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(requirements) ||
            construction.Requirements.Any(r => r.Status != "closed" || r.Checkpoints.Count == 0 ||
                r.Checkpoints.Any(sequence => sequence < 1 || sequence > construction.Checkpoints.Count ||
                    !construction.Checkpoints[sequence - 1].RequirementIds.Contains(r.Id, StringComparer.Ordinal)) ||
                !construction.Checkpoints.Any(c => c.ClosedRequirementIds?.Contains(r.Id, StringComparer.Ordinal) == true && c.InvalidatedTaskIds.Count == 0)))
            throw new PlanningConflictException("Approval requires explicit completion of every original requirement.");
        for (var index = 0; index < construction.Checkpoints.Count; index++)
        {
            var checkpoint = construction.Checkpoints[index];
            if (checkpoint.Sequence != index + 1 || !Hash(checkpoint.BaseHash) || !Hash(checkpoint.PlanHash) ||
                index > 0 && checkpoint.BaseHash != construction.Checkpoints[index - 1].PlanHash ||
                checkpoint.ClosedRequirementIds is { } closed && (closed.Distinct(StringComparer.Ordinal).Count() != closed.Count ||
                    closed.Any(id => !checkpoint.RequirementIds.Contains(id, StringComparer.Ordinal))))
                throw new PlanningConflictException("Construction checkpoint identities or completion claims are inconsistent.");
        }
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    }
}
