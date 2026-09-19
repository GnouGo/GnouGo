using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Core.Planning;
public static class PlanningGenerationPolicy
{
    public static void Validate(PlanningGenerationOptions options)
    {
        if (options.MaxInputTokensPerRequest is < 512 or > 128_000 || options.MaxOutputTokens is < 1 or > 65_536)
            throw new ArgumentException("Invalid model input or output limits.");
        if (options.Reasoning is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "auto"))
            throw new ArgumentException("Invalid reasoning level.");
    }
    public static LLMRequest Apply(LLMRequest request, PlanningGenerationOptions options)
    {
        Validate(options);
        request.MaxTokens = Math.Min(request.MaxTokens ?? options.MaxOutputTokens, options.MaxOutputTokens);
        request.Reasoning = options.Reasoning;
        request.RequireOutputTokenLimit = true;
        request.DisableTransportRetries = true;
        return request;
    }
}
