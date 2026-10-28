using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Presentation targets advise; captured admission ceilings remain blocking.</summary>
internal static class PlanningInputWarnings
{
    internal static void Record(PlanningSession state, string purpose, PlanningRequestMeasurement measurement)
    {
        var phase = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose);
        measurement.InputTarget = phase.TargetInputTokens;
        if (measurement.TargetWarning is not null || measurement.EstimatedInputTokens <= phase.TargetInputTokens ||
            measurement.EstimatedInputTokens > phase.MaxInputTokens) return;
        var components = string.Join(", ", measurement.Components.Where(component => component.SerializedBytes > 0)
            .OrderByDescending(component => component.SerializedBytes).ThenBy(component => component.Component, StringComparer.Ordinal)
            .Select(component => component.Component + "=" + component.SerializedBytes + " bytes"));
        measurement.TargetWarning = $"The complete {purpose} request is {measurement.CompleteRequestBytes} bytes and " +
            $"{measurement.EstimatedInputTokens} estimated input tokens: target {phase.TargetInputTokens}, maximum {phase.MaxInputTokens}. " +
            "Remaining request components: " + components + ".";
        // The measurement is the durable, exact-request journal. Neither candidate nor
        // session diagnostics are mutated: warnings grant no repair authority and must
        // not participate in artifact identity or approval hashes.
    }
}
