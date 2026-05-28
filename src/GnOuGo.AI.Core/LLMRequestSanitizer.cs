namespace GnOuGo.AI.Core;

/// <summary>
/// Removes optional request parameters that are not supported by the resolved model metadata.
/// The sanitizer is intentionally non-throwing: unsupported hints are omitted instead of failing the workflow.
/// </summary>
public static class LLMRequestSanitizer
{
    public static LLMClientRequest Sanitize(LLMClientRequest request, LLMModelMetadata metadata)
    {
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
            MaxOutputTokens = request.MaxOutputTokens ?? metadata.MaxOutputTokens
        };

        var capabilities = metadata.Capabilities ?? new ModelCapabilityMetadata();
        var unsupported = capabilities.UnsupportedRequestParameters ?? [];

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

    private static bool Contains(IEnumerable<string> values, string value)
        => values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}

