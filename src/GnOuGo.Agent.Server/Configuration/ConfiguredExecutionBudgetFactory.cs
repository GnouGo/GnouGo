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
            var limits = section.Get<LLMUsageBudgetLimits>(options => options.ErrorOnUnknownConfiguration = true)
                ?? new LLMUsageBudgetLimits();
            limits.Validate();
            return limits;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{SectionName} must declare valid explicit finite execution limits.", error);
        }
    }

    public LLMUsageBudgetScope? CreateScope()
        => limits is null ? null : new(limits, exchangeRateProvider: exchangeRates);
}
