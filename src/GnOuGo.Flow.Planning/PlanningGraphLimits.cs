using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Admission only: captured policy changes no lowering decisions or executable bytes.
internal readonly record struct PlanningGraphLimits(int Workflows, int StepsPerWorkflow)
{
    internal static PlanningGraphLimits From(PlanningPolicy policy) => new(policy.MaxWorkflows ?? 100, policy.MaxStepsPerWorkflow ?? 300);
}
