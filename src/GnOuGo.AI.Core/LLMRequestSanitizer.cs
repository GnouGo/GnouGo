namespace GnOuGo.AI.Core;

/// <summary>
/// Removes optional request parameters that are not supported by the resolved model metadata.
/// Unsupported optional hints are omitted. Explicit budget requirements fail closed.
/// </summary>
public static class LLMRequestSanitizer
{
    public static LLMClientRequest Sanitize(LLMClientRequest request, LLMModelMetadata metadata)
        => Sanitize(request, metadata, requestPolicy: null);

    public static LLMClientRequest Sanitize(
        LLMClientRequest request,
        LLMModelMetadata metadata,
        LLMProviderRequestPolicyOptions? requestPolicy)
    {
        requestPolicy ??= new LLMProviderRequestPolicyOptions();
        var maxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens, metadata, requestPolicy);
        var sanitized = new LLMClientRequest
        {
            Provider = request.Provider,
            Model = request.Model,
            Prompt = request.Prompt,
            Temperature = request.Temperature,
            StructuredOutputSchema = request.StructuredOutputSchema,
            StructuredOutputStrict = request.StructuredOutputStrict,
            Reasoning = request.Reasoning,
            UseBackgroundMode = request.UseBackgroundMode,
            Tools = request.Tools,
            MaxOutputTokens = maxOutputTokens,
            RequireOutputTokenLimit = request.RequireOutputTokenLimit,
            DisableTransportRetries = request.DisableTransportRetries
        };

        var capabilities = metadata.Capabilities ?? new ModelCapabilityMetadata();
        var unsupported = capabilities.UnsupportedRequestParameters ?? [];
        // Wire aliases belong to each transport. One unsupported alias must not
        // disable a ceiling that its supported protocol can still represent.
        if (request.RequireOutputTokenLimit && maxOutputTokens is not > 0)
            throw new InvalidOperationException("The configured model cannot enforce the required output-token limit.");

        if (capabilities.SupportsTemperature == false || Contains(unsupported, "temperature"))
            sanitized.Temperature = null;

        if (capabilities.SupportsReasoningEffort == false
            || Contains(unsupported, "reasoning")
            || Contains(unsupported, "reasoning_effort"))
            sanitized.Reasoning = null;

        if (capabilities.SupportsStructuredOutput == false
            || Contains(unsupported, "structured_output")
            || Contains(unsupported, "response_format"))
        {
            sanitized.StructuredOutputSchema = null;
            sanitized.StructuredOutputStrict = null;
        }

        if (capabilities.SupportsTools == false || Contains(unsupported, "tools"))
            sanitized.Tools = null;

        return sanitized;
    }

    private static int? ResolveMaxOutputTokens(
        int? explicitLimit,
        LLMModelMetadata metadata,
        LLMProviderRequestPolicyOptions requestPolicy)
    {
        int? resolved;
        if (explicitLimit.HasValue)
        {
            resolved = explicitLimit is > 0 ? explicitLimit : null;
        }
        else
        {
            resolved = requestPolicy.UnspecifiedOutputTokens switch
            {
                LLMUnspecifiedOutputTokensMode.Configured => requestPolicy.DefaultMaxOutputTokens,
                LLMUnspecifiedOutputTokensMode.ModelMaximum => metadata.MaxOutputTokens,
                _ => null
            };
        }

        if (resolved is not > 0)
            return null;

        if (metadata.MaxOutputTokens is > 0)
            resolved = Math.Min(resolved.Value, metadata.MaxOutputTokens.Value);
        if (requestPolicy.MaxOutputTokensCap is > 0)
            resolved = Math.Min(resolved.Value, requestPolicy.MaxOutputTokensCap.Value);

        return resolved;
    }

    private static bool Contains(IEnumerable<string> values, string value)
        => values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}
