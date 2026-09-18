using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
internal static class PlanningCapabilityBindings
{
    internal static bool Supports(PlanningCapability capability, string stepType) => capability.StepType == stepType;
}
