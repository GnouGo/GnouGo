using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Exact-request warnings do not create candidate defects or repair authority.</summary>
internal static class PlanningInputWarnings
{
    internal static void Record(PlanningSession state, string purpose, PlanningRequestMeasurement measurement)
    {
        var phase = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose);
        measurement.InputTarget = phase.TargetInputTokens;
        if (measurement.EstimatedInputTokens > phase.TargetInputTokens)
            Add(measurement, purpose, "input_target", measurement.EstimatedInputTokens, phase.TargetInputTokens, "conservative_request_estimate");
        if (measurement.EstimatedInputTokens > phase.MaxInputTokens && PlanningTokenThresholds.Advisory(state.Request))
            Add(measurement, purpose, "input_maximum", measurement.EstimatedInputTokens, phase.MaxInputTokens, "conservative_request_estimate");
        if (measurement.TargetWarning is not null || measurement.EstimatedInputTokens <= phase.TargetInputTokens ||
            measurement.EstimatedInputTokens > phase.MaxInputTokens && !PlanningTokenThresholds.Advisory(state.Request)) return;
        var components = string.Join(", ", measurement.Components.Where(component => component.SerializedBytes > 0)
            .OrderByDescending(component => component.SerializedBytes).ThenBy(component => component.Component, StringComparer.Ordinal)
            .Select(component => component.Component + "=" + component.SerializedBytes + " bytes"));
        measurement.TargetWarning = $"The complete {purpose} request is {measurement.CompleteRequestBytes} bytes and " +
            $"{measurement.EstimatedInputTokens} estimated input tokens: target {phase.TargetInputTokens}, maximum {phase.MaxInputTokens}. " +
            "Remaining request components: " + components + "." +
            (PlanningTokenThresholds.Advisory(state.Request) ? " Campaign token thresholds are advisory; non-token admission remains enforced." : "");
        // The measurement is the durable, exact-request journal. Neither candidate nor
        // session diagnostics are mutated: warnings grant no repair authority and must
        // not participate in artifact identity or approval hashes.
    }

    internal static void Add(PlanningRequestMeasurement measurement, string stage, string kind, long current, long threshold,
        string basis = "host_budget_measurement")
    {
        var warning = new PlanningThresholdWarning(kind, current, threshold, basis, stage);
        var warnings = measurement.Warnings ??= [];
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }
}
