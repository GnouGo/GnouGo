using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

// Trusted benchmark configuration, never a model-owned or public MCP option.
internal static class PlanningTokenThresholds
{
    internal const string OptionName = "token_threshold_policy";
    internal const string OptimizationOptionName = "optimization_threshold_policy";
    private static readonly AsyncLocal<Action<string, string, long, long>?> Observer = new();

    internal static IDisposable Observe(Action<string, string, long, long> observer)
    {
        var previous = Observer.Value;
        Observer.Value = (stage, kind, current, threshold) =>
        {
            previous?.Invoke(stage, kind, current, threshold);
            observer(stage, kind, current, threshold);
        };
        return new Observation(previous);
    }

    internal static bool Advisory(PlanningRequest request) => Advisory(request.Options);

    internal static bool Advisory(JsonObject? options)
    {
        // Validate both captured fields even when one already grants advisory
        // authority. Existing token-only sessions never acquire broader authority.
        var tokens = Read(options, OptionName, "advisory");
        var optimization = OptimizationAdvisory(options);
        return tokens || optimization;
    }

    internal static bool OptimizationAdvisory(PlanningRequest request) => OptimizationAdvisory(request.Options);

    internal static bool OptimizationAdvisory(JsonObject? options)
    {
        _ = Read(options, OptionName, "advisory");
        return Read(options, OptimizationOptionName, "diagnostic-v1");
    }

    private static bool Read(JsonObject? options, string name, string expected)
    {
        if (options is null || !options.TryGetPropertyValue(name, out var node)) return false;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var mode) || mode != expected)
            throw new PlanningConflictException("The captured " + name + " is invalid.");
        return true;
    }

    internal static void RejectReserved(JsonObject options)
    {
        if (options.ContainsKey(OptionName) || options.ContainsKey(OptimizationOptionName))
            throw new PlanningConflictException("Campaign threshold authority cannot be enabled by a production planning host.");
    }

    internal static void Warn(string stage, string kind, long current, long threshold)
    {
        Trace.TraceWarning("Advisory planning threshold: stage={0}, kind={1}, current={2}, threshold={3}. Financial, correctness, permission and replay controls remain enforced.",
            stage, kind, current, threshold);
        Observer.Value?.Invoke(stage, kind, current, threshold);
    }

    private sealed class Observation(Action<string, string, long, long>? previous) : IDisposable
    {
        public void Dispose() => Observer.Value = previous;
    }
}
