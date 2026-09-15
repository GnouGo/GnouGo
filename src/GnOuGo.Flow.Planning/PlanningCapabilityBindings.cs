using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningCapabilityBindings
{
    internal static readonly string[] LocalOperationTypes = ["set", "emit", "assert.non_null", "template.render"];

    internal static bool SupportsBehavior(PlanningCapability capability, string kind) => kind == "operation"
        ? capability.StepType is not ("human.input" or "switch" or "sequence" or "parallel" or "loop.sequential" or "loop.parallel" or "workflow.call") && Supports(capability, capability.StepType)
        : Supports(capability, kind switch { "decision" => "switch", "loop" => "loop.sequential", "confirmation" => "human.input", "workflow" => "workflow.call", _ => kind });

    // A locked local-processing obligation has no selected executor. Its default
    // set representation must not prohibit native control flow. External/native
    // capability selections still require their exact executor contract.
    public static bool Supports(PlanningCapability capability, string stepType)
        => capability.Resolution == "local"
            ? capability.EffectKind == "none" && capability.Server is null && capability.RequestBindings.Count == 0 && capability.FixedInput.Count == 0 &&
                (LocalOperationTypes.Contains(stepType, StringComparer.Ordinal) || stepType is "switch" or "sequence" or "parallel" or "loop.sequential" or "loop.parallel")
            : capability.StepType == stepType;
}
