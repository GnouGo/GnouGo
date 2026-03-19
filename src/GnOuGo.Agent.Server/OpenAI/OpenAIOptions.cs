namespace GnOuGo.Agent.Server.OpenAI;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// Base URL for the OpenAI API.
    /// Default: https://api.openai.com/v1/
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";

    /// <summary>
    /// API key. Prefer using the OPENAI_API_KEY environment variable.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Default model used for generation.
    /// </summary>
    public string Model { get; init; } = "gpt-5.2";

    /// <summary>
    /// Sampling temperature (0..2).
    /// </summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// Whether OpenAI should store responses (recommended: false).
    /// </summary>
    public bool Store { get; init; } = false;
}
