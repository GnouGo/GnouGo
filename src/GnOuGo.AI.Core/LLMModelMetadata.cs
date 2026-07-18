namespace GnOuGo.AI.Core;

/// <summary>
/// Pricing metadata for a model, expressed per one million tokens.
/// </summary>
public sealed class ModelPricingMetadata
{
    public string Currency { get; set; } = "USD";
    public decimal? InputPer1MTokens { get; set; }
    public decimal? OutputPer1MTokens { get; set; }
    public decimal? CachedInputPer1MTokens { get; set; }
    public decimal? ReasoningOutputPer1MTokens { get; set; }
}

/// <summary>
/// Capability metadata used to decide which optional request parameters are safe to emit.
/// Null means unknown and lets provider-level defaults/heuristics decide.
/// </summary>
public sealed class ModelCapabilityMetadata
{
    public bool? SupportsTemperature { get; set; }
    public bool? SupportsReasoningEffort { get; set; }
    public bool? SupportsStructuredOutput { get; set; }
    public bool? SupportsTools { get; set; }
    public bool? SupportsJsonMode { get; set; }
    public bool? SupportsVision { get; set; }
    public bool? SupportsAudio { get; set; }
    public bool? SupportsEmbeddings { get; set; }
    public List<string>? SupportedReasoningEfforts { get; set; }
    public List<string>? UnsupportedRequestParameters { get; set; }
}

/// <summary>
/// Complete model metadata: limits, pricing, capabilities and extension values.
/// Values can come from the embedded catalog, external JSON files, or inline LLMOptions overrides.
/// </summary>
public sealed class LLMModelMetadata
{
    public string Id { get; set; } = "";
    public string? ProviderType { get; set; }
    public string? DisplayName { get; set; }
    public string? OwnedBy { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int? MaxInputTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public ModelPricingMetadata? Pricing { get; set; }
    public ModelCapabilityMetadata Capabilities { get; set; } = new();
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Describes how model metadata was selected for a requested provider/model pair.
/// </summary>
public enum LLMModelMetadataMatchKind
{
    Exact,
    Alias,
    Fuzzy,
    Heuristic
}

/// <summary>
/// Detailed model metadata resolution result, including the catalog entry used as a fallback.
/// </summary>
public sealed record LLMModelMetadataResolution(
    LLMModelMetadata Metadata,
    LLMModelMetadataMatchKind MatchKind,
    string? MatchedProviderType,
    string? MatchedModelId,
    double? Similarity);
