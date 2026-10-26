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
        if (state.SchemaVersion != 10 || state.Requirements is null || state.Plan is null || state.Graph is null || state.Catalog is null ||
            state.Yaml is null || state.PendingCall is not null || state.Diagnostics.Any(d => d.Required) ||
            state.Requirements.Outcomes.Count == 0 || state.Plan.Choices.Any(c => c.Selected is null) || state.PendingQuestions is not null ||
            PlanningClarifications.InputFindings(state).Count > 0 ||
            state.IntentVersion is not (2 or 3) && (state.ApprovedHash is null || state.ApprovedHash != state.ComputeArtifactHash()))
            throw new PlanningConflictException("Approval requires a complete format-10 TaskPlan. Regenerate incompatible planning sessions; execution journals remain schema 9.");
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog, state.Request);
        if (compilation.Graph is not { } graph || compilation.Diagnostics.Count > 0 || PlanningClarifications.OutputFindings(state, graph).Count > 0 || PlanningGeneratedGraph.Validate(graph, state.Catalog, TaskPlanCompiler.UsesCompactBindings(state.Request)).Any(d => d.Required))
            throw new PlanningConflictException("The saved TaskPlan no longer compiles to an approvable artifact.");
        if (PlanningCommitments.Validate(state, state.Requirements, final: true).Count > 0 ||
            PlanningCommitments.Assess(state).Any(r => r.Diagnostics.Any(d => d.Required)))
            throw new PlanningConflictException("Approval cannot accept a violated business commitment. Recomputed coverage disagrees with the required operations or producer results.");
        PlanningConfirmationGuards.Apply(graph, state.Catalog);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.SerializeToNode(state.Graph, PlanningJsonContext.Default.PlanningGraph)) ||
            PlanningExecutableValidation.Validate(graph, state.Catalog).Any(d => d.Required) ||
            new PlanningGraphCompiler().Compile(graph, state.Catalog, state.Request.Name, TaskPlanCompiler.UsesNormalExports(state.Request), TaskPlanCompiler.UsesFusedBindings(state.Request), TaskPlanCompiler.UsesConsumerBindings(state.Request), TaskPlanCompiler.UsesDirectProjections(state.Request), TaskPlanCompiler.UsesNativeMappings(state.Request), TaskPlanCompiler.UsesReadableMappings(state.Request), TaskPlanCompiler.UsesLeanMappings(state.Request), TaskPlanCompiler.UsesCompactProjections(state.Request), TaskPlanCompiler.UsesInlineConsumers(state.Request), TaskPlanCompiler.UsesDirectValues(state.Request), TaskPlanCompiler.UsesCheckedControls(state.Request), TaskPlanCompiler.UsesReadableBranches(state.Request), TaskPlanCompiler.UsesSimplifiedLocals(state.Request)) != state.Yaml)
            throw new PlanningConflictException("Recompilation does not reproduce the reviewed executable artifact.");
    }

    private static void VerifyConstruction(PlanningSession state)
    {
        if (state.Construction is not { } construction) return; // Captured historical artifacts retain their approval contract.
        var requirements = state.Requirements?.Outcomes.Select(r => r.Id).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (construction.Version is not (1 or 2) || construction.Version == 2 && !PlanningCompactConstruction.SourcesComplete(state) ||
            !construction.Complete || construction.Candidate is not null || construction.OpenScopes.Count != 0 ||
            construction.PendingReviewContext is not null || construction.PendingReviewHash is not null || construction.Checkpoints.Count == 0 ||
            construction.Checkpoints[^1].PlanHash != PlanningConstruction.Hash(state.Plan) ||
            construction.Requirements.Any(r => r.Status != (construction.Version == 2 ? "closed" : "declared") || r.Checkpoints.Count == 0 ||
                construction.Version == 2 && !construction.Checkpoints.Any(c => c.ClosedRequirementIds?.Contains(r.Id, StringComparer.Ordinal) == true &&
                    c.InvalidatedTaskIds.Count == 0 && Reviewed(c, PlanningConstruction.LocalPurpose) &&
                    (!c.NeedsIntermediateReview || Reviewed(c, PlanningConstruction.IntermediatePurpose))) ||
                r.Checkpoints.Any(sequence => sequence < 1 || sequence > construction.Checkpoints.Count ||
                    !construction.Checkpoints[sequence - 1].RequirementIds.Contains(r.Id, StringComparer.Ordinal))) ||
            !construction.Requirements.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(requirements))
            throw new PlanningConflictException("Approval requires complete incremental construction with closed scopes and explicit requirement statuses.");
        for (var index = 0; index < construction.Checkpoints.Count; index++)
        {
            var checkpoint = construction.Checkpoints[index];
            if (checkpoint.Sequence != index + 1 || !Hash(checkpoint.BaseHash) || !Hash(checkpoint.PlanHash) ||
                checkpoint.ClosedRequirementIds is { } closed && (closed.Distinct(StringComparer.Ordinal).Count() != closed.Count || closed.Any(id => !checkpoint.RequirementIds.Contains(id, StringComparer.Ordinal))) ||
                !Reviewed(checkpoint, PlanningConstruction.LocalPurpose) ||
                checkpoint.NeedsIntermediateReview && !Reviewed(checkpoint, PlanningConstruction.IntermediatePurpose))
                throw new PlanningConflictException("Every accepted construction checkpoint requires its artifact-bound local and intermediate review evidence.");
        }

        bool Reviewed(PlanningConstructionCheckpoint checkpoint, string purpose) => PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, purpose) &&
            (PlanningCompactConstruction.UsesIncrementalReview(state) || checkpoint.Reviews.Any(review =>
            (review["purpose"]?.ToString() == purpose || CombinedReview(checkpoint, review, purpose)) &&
            review["candidateHash"]?.ToString() == checkpoint.PlanHash &&
            (checkpoint.ClosedRequirementIds is null || review["closedRequirementIds"] is JsonArray closed &&
                closed.Select(id => id!.ToString()).SequenceEqual(checkpoint.ClosedRequirementIds, StringComparer.Ordinal)) &&
            Hash(review["fingerprint"]?.ToString()) && review["outcome"]?.ToString() == "unverified" &&
            review["response"] is JsonObject response && response["findings"] is JsonArray { Count: 0 } &&
            response["evidenceRequests"] is null && response["uncertainty"] is JsonValue uncertainty &&
            uncertainty.TryGetValue<string>(out var message) && message.Length <= 512));

        // Coverage is host-issued evidence retained with the same context fingerprint,
        // candidate and closure claims; it does not invent a second Judge receipt.
        bool CombinedReview(PlanningConstructionCheckpoint checkpoint, JsonObject review, string purpose) =>
            PlanningCompactConstruction.UsesConvergentConstruction(state) && purpose == PlanningConstruction.IntermediatePurpose &&
            checkpoint.NeedsIntermediateReview && review["purpose"]?.ToString() == PlanningConstruction.LocalPurpose &&
            checkpoint.Step is { } step && step.BaseHash == checkpoint.BaseHash && checkpoint.ClosedRequirementIds is { } closed &&
            step.CloseRequirementIds.SequenceEqual(closed, StringComparer.Ordinal) &&
            review["reviewCoverage"] is JsonArray { Count: 2 } coverage &&
            coverage[0]?.ToString() == "local" && coverage[1]?.ToString() == "intermediate" &&
            review["candidateIdentity"]?.ToString() is { } identity && identity.StartsWith("candidate:v1:", StringComparison.Ordinal) && Hash(identity[13..]) &&
            review["rawResponse"] is JsonObject { Count: 2 } raw && raw["action"] is JsonObject action &&
            action.All(item => item.Key is "kind" or "findings" or "resolutions") && action["kind"]?.ToString() == "findings" &&
            action["findings"] is JsonArray { Count: 0 } && review["response"] is JsonObject response && response["inconclusive"] is null &&
            JsonNode.DeepEquals(raw["uncertainty"], response["uncertainty"]) &&
            JsonNode.DeepEquals(action["resolutions"], response["resolutions"]);
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    }
}
