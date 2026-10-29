using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

// Trusted benchmark configuration, never a model-owned or public MCP option.
internal static class PlanningTokenThresholds
{
    internal const string OptionName = "token_threshold_policy";

    internal static bool Advisory(PlanningRequest request) => Advisory(request.Options);

    internal static bool Advisory(JsonObject? options)
    {
        if (options?[OptionName] is null) return false;
        if (options[OptionName] is not JsonValue value || !value.TryGetValue<string>(out var mode) || mode != "advisory")
            throw new PlanningConflictException("The captured token-threshold policy is invalid.");
        return true;
    }

    internal static void RejectReserved(JsonObject options)
    {
        if (options.ContainsKey(OptionName))
            throw new PlanningConflictException("Campaign token-threshold authority cannot be enabled by a production planning host.");
    }

    internal static void Warn(string stage, string kind, long current, long threshold) =>
        Trace.TraceWarning("Advisory token threshold: stage={0}, kind={1}, current={2}, threshold={3}. Accounting and non-token guards remain enforced.",
            stage, kind, current, threshold);
}
