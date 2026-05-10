
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// Resolves model metadata from embedded defaults, external metadata files and inline options overrides.
/// </summary>
public sealed class LLMModelMetadataResolver
{
    private readonly IReadOnlyDictionary<string, LLMModelMetadata> _fileModels;
    private readonly IReadOnlyDictionary<string, string> _fileAliases;
    private readonly IReadOnlyDictionary<string, LLMModelMetadata> _inlineOverrides;

    public LLMModelMetadataResolver(LLMOptions? options = null)
    {
        options ??= new LLMOptions();
        (_fileModels, _fileAliases) = ModelMetadataCatalog.LoadFiles(options.ModelMetadataFiles);
        _inlineOverrides = options.ModelOverrides ?? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves merged metadata. Precedence: embedded catalog &lt; external files &lt; inline overrides,
    /// then provider/model heuristics fill missing capability values.
    /// </summary>
    public LLMModelMetadata Resolve(string? providerType, string model)
    {
        var cleanModel = ModelMetadataCatalog.StripVendorPrefix(model);
        var canonicalModel = ResolveFileAlias(model) ?? ResolveFileAlias(cleanModel);
        var metadata = CandidateKeys(model, cleanModel, canonicalModel)
            .Select(key => ModelMetadataCatalog.TryGetBuiltin(key, out var candidate) ? candidate : null)
            .FirstOrDefault(candidate => candidate != null)
            ?? new LLMModelMetadata { Id = canonicalModel ?? cleanModel, DisplayName = canonicalModel ?? cleanModel };

        foreach (var key in CandidateKeys(model, cleanModel, canonicalModel))
        {
            MergeIfFound(metadata, _fileModels, key);
            MergeIfFound(metadata, _inlineOverrides, key);
        }

        if (string.IsNullOrWhiteSpace(metadata.Id))
            metadata.Id = cleanModel;
        if (string.IsNullOrWhiteSpace(metadata.DisplayName))
            metadata.DisplayName = metadata.Id;
        if (string.IsNullOrWhiteSpace(metadata.ProviderType) && !string.IsNullOrWhiteSpace(providerType))
            metadata.ProviderType = providerType;

        ModelMetadataCatalog.ApplyHeuristicDefaults(metadata, providerType, cleanModel);
        return metadata;
    }

    private string? ResolveFileAlias(string key)
        => _fileAliases.TryGetValue(key, out var canonical) ? canonical : null;

    private static IEnumerable<string> CandidateKeys(string model, string cleanModel, string? canonicalModel)
    {
        yield return model;
        if (!string.Equals(cleanModel, model, StringComparison.OrdinalIgnoreCase))
            yield return cleanModel;
        if (!string.IsNullOrWhiteSpace(canonicalModel))
        {
            yield return canonicalModel!;
            var cleanCanonical = ModelMetadataCatalog.StripVendorPrefix(canonicalModel!);
            if (!string.Equals(cleanCanonical, canonicalModel, StringComparison.OrdinalIgnoreCase))
                yield return cleanCanonical;
        }
    }

    public IReadOnlyList<LLMModelMetadata> ListConfiguredMetadata(string? providerType)
    {
        var all = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _fileModels)
            AddIfMatches(all, kv.Value, providerType);
        foreach (var kv in _inlineOverrides)
            AddIfMatches(all, kv.Value, providerType, kv.Key);
        return all.Values.OrderBy(m => m.DisplayName ?? m.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void MergeIfFound(LLMModelMetadata target, IReadOnlyDictionary<string, LLMModelMetadata> source, string key)
    {
        if (source.TryGetValue(key, out var value))
            ModelMetadataCatalog.MergeInto(target, value, key);
    }

    private static void AddIfMatches(Dictionary<string, LLMModelMetadata> target, LLMModelMetadata metadata, string? providerType, string? fallbackId = null)
    {
        var clone = ModelMetadataCatalog.Clone(metadata);
        if (string.IsNullOrWhiteSpace(clone.Id) && !string.IsNullOrWhiteSpace(fallbackId))
            clone.Id = fallbackId;
        if (string.IsNullOrWhiteSpace(clone.Id))
            return;
        if (!string.IsNullOrWhiteSpace(providerType)
            && !string.IsNullOrWhiteSpace(clone.ProviderType)
            && !string.Equals(clone.ProviderType, providerType, StringComparison.OrdinalIgnoreCase))
            return;
        target[clone.Id] = clone;
    }
}

/// <summary>
/// Embedded and file-backed model metadata helpers.
/// </summary>
public static partial class ModelMetadataCatalog
{
    private static readonly ILogger Logger = NullLogger.Instance;

    public static bool TryGetBuiltin(string modelName, out LLMModelMetadata metadata)
    {
        if (TryGetBuiltinCore(modelName, out metadata))
            return true;

        var clean = StripVendorPrefix(modelName);
        if (!string.Equals(clean, modelName, StringComparison.OrdinalIgnoreCase)
            && TryGetBuiltinCore(clean, out metadata))
            return true;

        metadata = default!;
        return false;
    }

    public static bool TryGetBuiltinPricing(string modelName, out ModelPricingMetadata pricing)
    {
        if (TryGetBuiltin(modelName, out var metadata) && metadata.Pricing != null)
        {
            pricing = metadata.Pricing;
            return true;
        }

        pricing = default!;
        return false;
    }

    /// <summary>
    /// Computes the estimated cost in USD from model metadata pricing.
    /// When <paramref name="options"/> or <paramref name="providerType"/> is provided,
    /// pricing is resolved through the full metadata resolver, including external metadata
    /// files and <see cref="LLMOptions.ModelOverrides"/>.
    /// </summary>
    public static decimal? EstimateCost(
        string? modelName,
        long? inputTokens = null,
        long? outputTokens = null,
        LLMOptions? options = null,
        string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        ModelPricingMetadata? pricing;
        if (options != null || !string.IsNullOrWhiteSpace(providerType))
        {
            var metadata = new LLMModelMetadataResolver(options).Resolve(providerType, modelName);
            pricing = metadata.Pricing;
        }
        else
        {
            pricing = TryGetBuiltinPricing(modelName, out var builtinPricing) ? builtinPricing : null;
        }

        if (pricing == null)
            return null;

        var input = inputTokens ?? 0;
        var output = outputTokens ?? 0;

        return input / 1_000_000m * (pricing.InputPer1MTokens ?? 0m)
             + output / 1_000_000m * (pricing.OutputPer1MTokens ?? 0m);
    }

    public static IReadOnlyList<string> GetMissingRequiredMetadataFields(
        LLMOptions? options,
        string? providerType,
        string modelName,
        out LLMModelMetadata metadata)
    {
        metadata = new LLMModelMetadataResolver(options).Resolve(providerType, modelName);
        return GetMissingRequiredMetadataFields(metadata);
    }

    public static bool HasCompleteRequiredMetadata(LLMOptions? options, string? providerType, string modelName)
    {
        var missing = GetMissingRequiredMetadataFields(options, providerType, modelName, out _);
        return missing.Count == 0;
    }

    public static IReadOnlyList<string> GetMissingRequiredMetadataFields(LLMModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var missing = new List<string>();
        if (metadata.ContextWindowTokens is null or <= 0)
            missing.Add("contextWindowTokens");
        if (metadata.MaxInputTokens is null or <= 0)
            missing.Add("maxInputTokens");
        if (metadata.MaxOutputTokens is null or <= 0)
            missing.Add("maxOutputTokens");

        if (metadata.Pricing is null)
        {
            missing.Add("pricing.inputPer1MTokens");
            missing.Add("pricing.outputPer1MTokens");
        }
        else
        {
            if (metadata.Pricing.InputPer1MTokens is null or < 0)
                missing.Add("pricing.inputPer1MTokens");
            if (metadata.Pricing.OutputPer1MTokens is null or < 0)
                missing.Add("pricing.outputPer1MTokens");
        }

        var capabilities = metadata.Capabilities;
        if (capabilities.SupportsTemperature is null)
            missing.Add("capabilities.supportsTemperature");
        if (capabilities.SupportsReasoningEffort is null)
            missing.Add("capabilities.supportsReasoningEffort");
        if (capabilities.SupportsStructuredOutput is null)
            missing.Add("capabilities.supportsStructuredOutput");
        if (capabilities.SupportsTools is null)
            missing.Add("capabilities.supportsTools");
        if (capabilities.SupportsJsonMode is null)
            missing.Add("capabilities.supportsJsonMode");

        return missing;
    }

    internal static bool TryGetBuiltinCore(string modelName, out LLMModelMetadata metadata)
    {
        if (BuiltinModels.TryGetValue(modelName, out var value))
        {
            metadata = Clone(value);
            return true;
        }

        if (BuiltinAliases.TryGetValue(modelName, out var canonical) && BuiltinModels.TryGetValue(canonical, out value))
        {
            metadata = Clone(value);
            return true;
        }

        metadata = default!;
        return false;
    }

    internal static (IReadOnlyDictionary<string, LLMModelMetadata> Models, IReadOnlyDictionary<string, string> Aliases) LoadFiles(IEnumerable<string>? paths)
    {
        var models = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (paths == null)
            return (models, aliases);

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var resolvedPath = ResolveFilePath(path);
            if (resolvedPath == null)
                continue;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(resolvedPath)) as JsonObject;
                if (root == null)
                    continue;

                if (root["aliases"] is JsonObject aliasesObj)
                {
                    foreach (var kv in aliasesObj)
                    {
                        var value = kv.Value?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(value))
                            aliases[kv.Key] = value;
                    }
                }

                if (root["models"] is JsonObject modelsObj)
                {
                    foreach (var kv in modelsObj)
                    {
                        if (kv.Value is not JsonObject modelObj)
                            continue;
                        var metadata = ParseMetadata(kv.Key, modelObj);
                        foreach (var alias in metadata.Aliases)
                            aliases[alias.Key] = alias.Value;
                        models[kv.Key] = metadata;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Ignoring invalid external model metadata file '{MetadataPath}'.", resolvedPath);
                // External metadata files are optional user extensions. Ignore invalid files here;
                // callers can validate their files separately without making inference unavailable.
            }
        }

        foreach (var alias in aliases.ToArray())
        {
            if (models.TryGetValue(alias.Value, out var metadata))
                models[alias.Key] = Clone(metadata);
        }

        return (models, aliases);
    }

    internal static void ApplyHeuristicDefaults(LLMModelMetadata metadata, string? providerType, string model)
    {
        var capabilities = metadata.Capabilities ??= new ModelCapabilityMetadata();
        var provider = (metadata.ProviderType ?? providerType ?? string.Empty).ToLowerInvariant();
        var m = model.ToLowerInvariant();
        var isOpenAiCompatible = provider is "openai" or "copilot";
        var isReasoningModel = m.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || m.Contains("reasoner", StringComparison.OrdinalIgnoreCase);

        if (isOpenAiCompatible)
        {
            capabilities.SupportsTemperature ??= !isReasoningModel;
            capabilities.SupportsReasoningEffort ??= isReasoningModel;
            capabilities.SupportsStructuredOutput ??= !m.Contains("embedding", StringComparison.OrdinalIgnoreCase);
            capabilities.SupportsTools ??= !m.Contains("embedding", StringComparison.OrdinalIgnoreCase);
            capabilities.SupportsJsonMode ??= !m.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        }
        else if (provider == "ollama")
        {
            var supportsThink = m.Contains("deepseek-r1", StringComparison.OrdinalIgnoreCase)
                || m.Contains("qwen3", StringComparison.OrdinalIgnoreCase)
                || m.Contains("qwq", StringComparison.OrdinalIgnoreCase);
            capabilities.SupportsTemperature ??= true;
            capabilities.SupportsReasoningEffort ??= supportsThink;
            capabilities.SupportsStructuredOutput ??= true;
            capabilities.SupportsTools ??= true;
            capabilities.SupportsJsonMode ??= true;
        }
        else
        {
            capabilities.SupportsTemperature ??= true;
            capabilities.SupportsReasoningEffort ??= false;
        }

        if (capabilities.SupportsTemperature == false)
            AddUnsupported(capabilities, "temperature");
        if (capabilities.SupportsReasoningEffort == false)
            AddUnsupported(capabilities, "reasoning_effort");
    }

    internal static void MergeInto(LLMModelMetadata target, LLMModelMetadata source, string? fallbackId = null)
    {
        if (!string.IsNullOrWhiteSpace(source.Id)) target.Id = source.Id;
        else if (string.IsNullOrWhiteSpace(target.Id) && !string.IsNullOrWhiteSpace(fallbackId)) target.Id = fallbackId;
        if (!string.IsNullOrWhiteSpace(source.ProviderType)) target.ProviderType = source.ProviderType;
        if (!string.IsNullOrWhiteSpace(source.DisplayName)) target.DisplayName = source.DisplayName;
        if (!string.IsNullOrWhiteSpace(source.OwnedBy)) target.OwnedBy = source.OwnedBy;
        target.ContextWindowTokens = source.ContextWindowTokens ?? target.ContextWindowTokens;
        target.MaxInputTokens = source.MaxInputTokens ?? target.MaxInputTokens;
        target.MaxOutputTokens = source.MaxOutputTokens ?? target.MaxOutputTokens;

        if (source.Pricing != null)
        {
            target.Pricing ??= new ModelPricingMetadata();
            if (!string.IsNullOrWhiteSpace(source.Pricing.Currency)) target.Pricing.Currency = source.Pricing.Currency;
            target.Pricing.InputPer1MTokens = source.Pricing.InputPer1MTokens ?? target.Pricing.InputPer1MTokens;
            target.Pricing.OutputPer1MTokens = source.Pricing.OutputPer1MTokens ?? target.Pricing.OutputPer1MTokens;
            target.Pricing.CachedInputPer1MTokens = source.Pricing.CachedInputPer1MTokens ?? target.Pricing.CachedInputPer1MTokens;
            target.Pricing.ReasoningOutputPer1MTokens = source.Pricing.ReasoningOutputPer1MTokens ?? target.Pricing.ReasoningOutputPer1MTokens;
        }

        if (source.Capabilities != null)
        {
            target.Capabilities ??= new ModelCapabilityMetadata();
            target.Capabilities.SupportsTemperature = source.Capabilities.SupportsTemperature ?? target.Capabilities.SupportsTemperature;
            target.Capabilities.SupportsReasoningEffort = source.Capabilities.SupportsReasoningEffort ?? target.Capabilities.SupportsReasoningEffort;
            target.Capabilities.SupportsStructuredOutput = source.Capabilities.SupportsStructuredOutput ?? target.Capabilities.SupportsStructuredOutput;
            target.Capabilities.SupportsTools = source.Capabilities.SupportsTools ?? target.Capabilities.SupportsTools;
            target.Capabilities.SupportsJsonMode = source.Capabilities.SupportsJsonMode ?? target.Capabilities.SupportsJsonMode;
            target.Capabilities.SupportsVision = source.Capabilities.SupportsVision ?? target.Capabilities.SupportsVision;
            target.Capabilities.SupportsAudio = source.Capabilities.SupportsAudio ?? target.Capabilities.SupportsAudio;
            target.Capabilities.SupportsEmbeddings = source.Capabilities.SupportsEmbeddings ?? target.Capabilities.SupportsEmbeddings;
            if (source.Capabilities.SupportedReasoningEfforts != null)
                target.Capabilities.SupportedReasoningEfforts = [.. source.Capabilities.SupportedReasoningEfforts];
            if (source.Capabilities.UnsupportedRequestParameters != null)
                target.Capabilities.UnsupportedRequestParameters = [.. source.Capabilities.UnsupportedRequestParameters];
        }

        foreach (var kv in source.Aliases)
            target.Aliases[kv.Key] = kv.Value;
        foreach (var kv in source.Extra)
            target.Extra[kv.Key] = kv.Value;
    }

    public static LLMModelMetadata Clone(LLMModelMetadata source)
    {
        var clone = new LLMModelMetadata
        {
            Id = source.Id,
            ProviderType = source.ProviderType,
            DisplayName = source.DisplayName,
            OwnedBy = source.OwnedBy,
            ContextWindowTokens = source.ContextWindowTokens,
            MaxInputTokens = source.MaxInputTokens,
            MaxOutputTokens = source.MaxOutputTokens,
            Pricing = source.Pricing == null ? null : new ModelPricingMetadata
            {
                Currency = source.Pricing.Currency,
                InputPer1MTokens = source.Pricing.InputPer1MTokens,
                OutputPer1MTokens = source.Pricing.OutputPer1MTokens,
                CachedInputPer1MTokens = source.Pricing.CachedInputPer1MTokens,
                ReasoningOutputPer1MTokens = source.Pricing.ReasoningOutputPer1MTokens
            },
            Capabilities = source.Capabilities == null ? new ModelCapabilityMetadata() : new ModelCapabilityMetadata
            {
                SupportsTemperature = source.Capabilities.SupportsTemperature,
                SupportsReasoningEffort = source.Capabilities.SupportsReasoningEffort,
                SupportsStructuredOutput = source.Capabilities.SupportsStructuredOutput,
                SupportsTools = source.Capabilities.SupportsTools,
                SupportsJsonMode = source.Capabilities.SupportsJsonMode,
                SupportsVision = source.Capabilities.SupportsVision,
                SupportsAudio = source.Capabilities.SupportsAudio,
                SupportsEmbeddings = source.Capabilities.SupportsEmbeddings,
                SupportedReasoningEfforts = source.Capabilities.SupportedReasoningEfforts == null ? null : [.. source.Capabilities.SupportedReasoningEfforts],
                UnsupportedRequestParameters = source.Capabilities.UnsupportedRequestParameters == null ? null : [.. source.Capabilities.UnsupportedRequestParameters]
            }
        };
        foreach (var kv in source.Aliases)
            clone.Aliases[kv.Key] = kv.Value;
        foreach (var kv in source.Extra)
            clone.Extra[kv.Key] = kv.Value;
        return clone;
    }

    internal static string StripVendorPrefix(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;
        var slashIdx = model.IndexOf('/');
        if (slashIdx > 0 && slashIdx < model.Length - 1)
        {
            var prefix = model[..slashIdx];
            if (prefix.Length <= 30 && !prefix.Contains('.'))
                return model[(slashIdx + 1)..];
        }
        return model;
    }

    private static LLMModelMetadata ParseMetadata(string id, JsonObject obj)
    {
        var metadata = new LLMModelMetadata { Id = GetString(obj, "id") ?? id };
        metadata.ProviderType = GetString(obj, "providerType");
        metadata.DisplayName = GetString(obj, "displayName");
        metadata.OwnedBy = GetString(obj, "ownedBy");
        metadata.ContextWindowTokens = GetInt(obj, "contextWindowTokens");
        metadata.MaxInputTokens = GetInt(obj, "maxInputTokens");
        metadata.MaxOutputTokens = GetInt(obj, "maxOutputTokens");

        if (obj["pricing"] is JsonObject pricing)
        {
            metadata.Pricing = new ModelPricingMetadata
            {
                Currency = GetString(pricing, "currency") ?? "USD",
                InputPer1MTokens = GetDecimal(pricing, "inputPer1MTokens"),
                OutputPer1MTokens = GetDecimal(pricing, "outputPer1MTokens"),
                CachedInputPer1MTokens = GetDecimal(pricing, "cachedInputPer1MTokens"),
                ReasoningOutputPer1MTokens = GetDecimal(pricing, "reasoningOutputPer1MTokens")
            };
        }

        if (obj["capabilities"] is JsonObject capabilities)
        {
            metadata.Capabilities = new ModelCapabilityMetadata
            {
                SupportsTemperature = GetBool(capabilities, "supportsTemperature"),
                SupportsReasoningEffort = GetBool(capabilities, "supportsReasoningEffort"),
                SupportsStructuredOutput = GetBool(capabilities, "supportsStructuredOutput"),
                SupportsTools = GetBool(capabilities, "supportsTools"),
                SupportsJsonMode = GetBool(capabilities, "supportsJsonMode"),
                SupportsVision = GetBool(capabilities, "supportsVision"),
                SupportsAudio = GetBool(capabilities, "supportsAudio"),
                SupportsEmbeddings = GetBool(capabilities, "supportsEmbeddings"),
                SupportedReasoningEfforts = GetStringArray(capabilities, "supportedReasoningEfforts"),
                UnsupportedRequestParameters = GetStringArray(capabilities, "unsupportedRequestParameters")
            };
        }

        if (obj["aliases"] is JsonObject aliases)
        {
            foreach (var kv in aliases)
            {
                var value = kv.Value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                    metadata.Aliases[kv.Key] = value;
            }
        }

        if (obj["extra"] is JsonObject extra)
        {
            foreach (var kv in extra)
                metadata.Extra[kv.Key] = kv.Value?.ToJsonString() ?? string.Empty;
        }

        return metadata;
    }

    private static string? ResolveFilePath(string path)
    {
        if (Path.IsPathRooted(path) && File.Exists(path))
            return path;
        var candidates = new[]
        {
            Path.GetFullPath(path),
            Path.Combine(AppContext.BaseDirectory, path),
            Path.Combine(Directory.GetCurrentDirectory(), path)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddUnsupported(ModelCapabilityMetadata capabilities, string parameter)
    {
        capabilities.UnsupportedRequestParameters ??= [];
        if (!capabilities.UnsupportedRequestParameters.Contains(parameter, StringComparer.OrdinalIgnoreCase))
            capabilities.UnsupportedRequestParameters.Add(parameter);
    }

    private static string? GetString(JsonObject obj, string name) => obj[name]?.GetValue<string>();
    private static int? GetInt(JsonObject obj, string name) => obj[name]?.GetValue<int>();
    private static decimal? GetDecimal(JsonObject obj, string name) => obj[name]?.GetValue<decimal>();
    private static bool? GetBool(JsonObject obj, string name) => obj[name]?.GetValue<bool>();

    private static List<string>? GetStringArray(JsonObject obj, string name)
    {
        if (obj[name] is not JsonArray arr)
            return null;
        var values = new List<string>();
        foreach (var item in arr)
        {
            var value = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values;
    }
}





