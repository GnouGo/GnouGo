using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

// Host-owned and verified before use. Never accepted from authored workflow options.
internal static class PlanningApplicationBudget
{
    internal const string OptionName = "application_llm_budget";
    internal static bool Unlimited(PlanningRequest request)
    {
        if (request.Options[OptionName] is null) return false;
        if (request.Options[OptionName] is not JsonObject policy || policy["version"]?.GetValue<int>() != 1 ||
            policy["mode"]?.GetValue<string>() is not ("unlimited" or "limited") ||
            string.IsNullOrWhiteSpace(policy["origin"]?.GetValue<string>()))
            throw new PlanningConflictException("The captured application LLM budget policy is invalid.");
        return policy["mode"]!.GetValue<string>() == "unlimited";
    }
    internal static bool TrackingOnly(PlanningRequest request) => Unlimited(request) && !ExplicitWorkflowLimits(request);
    internal static bool ExplicitWorkflowLimits(PlanningRequest request) => request.Options[OptionName]?["explicitWorkflowLimits"]?.GetValue<bool>() == true;
    internal static string Currency(PlanningRequest request) => request.Options[OptionName]?["currency"]?.GetValue<string>() ?? "USD";
    internal static TimeSpan? HardElapsedLimit(PlanningRequest request)
    {
        if (TrackingOnly(request)) return null;
        var authored = PlanningBudgetOptions.Parse(request.Options)?.MaxElapsed;
        if (PlanningTokenThresholds.OptimizationAdvisory(request))
            return ExplicitWorkflowLimits(request) ? authored : null;
        if (request.Options[OptionName]?["mode"]?.ToString() == "limited" && request.Options[OptionName]?["planningLimits"] is { } captured)
        {
            var host = PlanningBudgetOptions.Parse(new JsonObject { ["llm_budget"] = captured.DeepClone() })?.MaxElapsed;
            if (host is not null && (authored is null || host < authored)) return host;
        }
        return authored;
    }
}
