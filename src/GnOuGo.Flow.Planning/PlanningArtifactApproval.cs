using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Shared approval/save guard for the exact validated artifact and its evidence.</summary>
public static class PlanningArtifactApproval
{
    public static string ContractFingerprint(PlanningSnapshot state) => PlanningContext.Contracts(state);
    public static string FixtureFingerprint(PlanningSnapshot state) => PlanningContext.Fixtures(state);

    public static void Verify(PlanningSnapshot state)
    {
        if (state.Graph is null || state.Preparation is null || state.BehaviorPlan is null ||
            state.ApprovedBehaviorHash != PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan) ||
            state.Diagnostics.Any(d => d.Required) || state.Validation.Stage != 5 ||
            state.Validation.Scenarios.Count == 0 || state.Validation.Scenarios.Any(s => s.Outcome != "passed") ||
            state.Validation.GraphFingerprint != PlanningGraphCompiler.Fingerprint(state.Graph) ||
            state.Validation.ContractFingerprint != ContractFingerprint(state) || state.Validation.FixtureFingerprint != FixtureFingerprint(state))
            throw new PlanningConflictException("The current graph, contracts and fixtures have not passed every validation gate.");
        var yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Preparation, state.Request.Name);
        if (state.Yaml != yaml || state.ArtifactHash != PlanningGraphCompiler.Fingerprint(yaml))
            throw new PlanningConflictException("The compiled artifact changed after validation.");
    }
}
