using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDecisionValidation
{
    internal static void Binding(PlanningSession state, GroundedPlan candidate, ISet<string>? actionIds = null)
    {
        var check = new PlanningSession { Request = state.Request, Catalog = state.Catalog, SemanticPlan = state.SemanticPlan, Grounding = state.Grounding, GroundedPlan = candidate };
        var diagnostics = CapabilityGrounder.ValidateBindings(check, actionIds);
        var validation = GroundedPlanValidator.Validate(candidate, state.Catalog!);
        diagnostics.AddRange(validation.Diagnostics);
        if (validation.Plan is not null) diagnostics.AddRange(CapabilityGrounder.ValidateBusinessOutputs(check, validation.Plan));
        if (diagnostics.Any(d => d.Required)) throw new PlanningResponseException(diagnostics);
    }
}
