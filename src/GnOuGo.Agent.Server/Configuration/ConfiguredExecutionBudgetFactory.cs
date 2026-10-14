using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Configuration;

/// <summary>Explicit host policy for new executions; resumed runs use their stored policy.</summary>
internal sealed class ConfiguredExecutionBudgetFactory(
    LLMUsageBudgetLimits? limits, IExchangeRateProvider exchangeRates) : ILLMUsageBudgetScopeFactory
{
    internal const string SectionName = "Flow:Execution:LlmBudget";

    internal static LLMUsageBudgetLimits? Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.Exists())
            return null;

        try
        {
            // Explicit reads keep immutable Core records independent of reflection
            // binding metadata, including in the partially trimmed Server publish.
            RequireKeys(section, "MaxCalls", "MaxTotalTokens", "MaxElapsed", "MaxEstimatedCost", "MaxEstimatedCostUsd");
            foreach (var child in section.GetChildren())
            {
                if (string.Equals(child.Key, "MaxEstimatedCost", StringComparison.OrdinalIgnoreCase))
                {
                    if (child.Value is not null) throw new ArgumentException("Estimated cost must be an amount/currency object.");
                    RequireKeys(child, "Amount", "Currency");
                    foreach (var scalar in child.GetChildren()) RequireScalar(scalar);
                }
                else RequireScalar(child);
            }
            var monetary = section.GetSection("MaxEstimatedCost");
            var limits = new LLMUsageBudgetLimits
            {
                MaxCalls = section.GetValue<int?>("MaxCalls"),
                MaxTotalTokens = section.GetValue<long?>("MaxTotalTokens"),
                MaxElapsed = section.GetValue<TimeSpan?>("MaxElapsed"),
                MaxEstimatedCostUsd = section.GetValue<decimal?>("MaxEstimatedCostUsd"),
                MaxEstimatedCost = monetary.Exists() ? new(
                    monetary.GetValue<decimal?>("Amount") ?? throw new ArgumentException("Estimated cost requires Amount."),
                    monetary["Currency"] ?? throw new ArgumentException("Estimated cost requires Currency.")) : null
            };
            limits.Validate();
            return limits;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{SectionName} must declare valid explicit finite execution limits.", error);
        }
    }

    private static void RequireKeys(IConfigurationSection section, params string[] keys)
    {
        if (section.GetChildren().Any(child => !keys.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown execution budget setting.");
    }

    private static void RequireScalar(IConfigurationSection section)
    {
        if (section.GetChildren().Any()) throw new ArgumentException("Execution budget limits must be scalar values.");
    }

    public LLMUsageBudgetScope? CreateScope()
        => limits is null ? null : new(limits, exchangeRateProvider: exchangeRates);
}
