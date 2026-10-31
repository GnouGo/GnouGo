using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    // Reuse retained exact contracts for used operations only. Probes call this
    // on their cloned session; no discovery, permission or execution is granted.
    internal static void AdmitResolvedOperations(PlanningSession state, TaskPlan plan)
    {
        foreach (var operation in TaskPlanRevisions.Tasks(plan).Select(task => task.Operation).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (state.Catalog!.Capabilities.Any(capability => TaskOperations.Describe(capability).Id == operation)) continue;
            var matches = state.Discovery.Resolved.Where(capability => TaskOperations.Describe(capability).Id == operation)
                .DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
            if (matches.Length == 1) state.Catalog.Capabilities.Add(matches[0]);
        }
    }
}
