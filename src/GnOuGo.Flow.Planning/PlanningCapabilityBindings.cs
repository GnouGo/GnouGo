using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningCapabilityBindings
{
    // A locked local-processing obligation has no selected executor. Its default
    // set representation must not prohibit native control flow. External/native
    // capability selections still require their exact executor contract.
    public static bool Supports(PlanningCapability capability, string stepType)
        => capability.Resolution == "local"
            ? capability.EffectKind == "none" && capability.Server is null && capability.RequestBindings.Count == 0 && capability.FixedInput.Count == 0 &&
                stepType is "set" or "switch" or "sequence" or "parallel" or "loop.sequential" or "loop.parallel"
            : capability.StepType == stepType;
}
