using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

public static class PlanningGenerationPolicy
{
    public const int AnswerTargetTokens = 2_048;
    public static int InputTarget(PlanningGenerationOptions options) => checked(options.MaxInputTokensPerRequest * 4 / 5);

    public static string ReasoningFor(PlanningGenerationOptions options, string phase) => phase switch
    {
        var name when name.StartsWith("behavior", StringComparison.Ordinal) => options.ReasoningProfile.Behavior,
        var name when name.StartsWith("semantic_review", StringComparison.Ordinal) => options.ReasoningProfile.SemanticReview,
        _ => options.ReasoningProfile.Routine
    };

    public static void Validate(PlanningGenerationOptions options)
    {
        if (options.MaxInputTokensPerRequest is < 512 or > 128_000 || options.MaxOutputTokens is < 1 or > 65_536)
            throw new ArgumentException("Invalid model input or output limits.");
        if (options.ReasoningProfile is null || new[] { options.ReasoningProfile.Routine, options.ReasoningProfile.Behavior, options.ReasoningProfile.SemanticReview }
            .Any(value => value is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "auto")))
            throw new ArgumentException("Invalid phase reasoning profile.");
    }

    public static LLMRequest Apply(LLMRequest request, PlanningGenerationOptions options)
    {
        Validate(options);
        request.MaxTokens = Math.Min(request.MaxTokens ?? options.MaxOutputTokens, options.MaxOutputTokens);
        request.RequireOutputTokenLimit = true;
        request.DisableTransportRetries = true;
        return request;
    }
}
