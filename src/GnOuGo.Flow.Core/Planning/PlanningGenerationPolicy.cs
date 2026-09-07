using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

public static class PlanningGenerationPolicy
{
    public static void Validate(PlanningGenerationOptions options)
    {
        if (options.MaxNodesPerUnit is < 1 or > 4 || options.MaxInputTokensPerUnit is < 512 or > 128_000 || options.MaxOutputTokens is < 1 or > 65_536)
            throw new ArgumentException("Invalid construction unit or model output limits.");
        if (options.Reasoning is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "auto"))
            throw new ArgumentException("Invalid generation reasoning level.");
    }

    public static LLMRequest Apply(LLMRequest request, PlanningGenerationOptions options)
    {
        Validate(options);
        request.MaxTokens = Math.Min(request.MaxTokens ?? options.MaxOutputTokens, options.MaxOutputTokens);
        request.RequireOutputTokenLimit = true;
        request.DisableTransportRetries = true;
        if (options.Reasoning is not null) request.Reasoning = options.Reasoning;
        return request;
    }
}
