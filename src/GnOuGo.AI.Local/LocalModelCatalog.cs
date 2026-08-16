using GnOuGo.AI.Core;

namespace GnOuGo.AI.Local;

public sealed record LocalModelCatalogEntry(
    string Id,
    string DisplayName,
    string FileName,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256,
    string License,
    int ContextWindowTokens);

public static class LocalModelCatalog
{
    public const string Qwen3Id = "qwen3:0.6b";

    public static LocalModelCatalogEntry Qwen3 { get; } = new(
        Qwen3Id,
        "Qwen3 0.6B Q4_0",
        "Qwen3-0.6B-Q4_0.gguf",
        new Uri("https://huggingface.co/ggml-org/Qwen3-0.6B-GGUF/resolve/a41486f827d17edd055fe6b3b0ba3f8d427c0519/Qwen3-0.6B-Q4_0.gguf"),
        428_970_080,
        "da2572f16c06133561ce56accaa822216f2391ef4d37fba427801cd6736417d4",
        "Apache-2.0",
        8192);

    public static IReadOnlyList<LocalModelCatalogEntry> Entries { get; } = [Qwen3];

    public static LocalModelCatalogEntry Resolve(string modelId)
    {
        var entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId?.Trim(), StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new ArgumentException(
            $"Unknown local model '{modelId}'. Available models: {string.Join(", ", Entries.Select(static candidate => candidate.Id))}.",
            nameof(modelId));
    }

    public static LLMModelMetadata CreateMetadata(LocalModelCatalogEntry entry)
        => new()
        {
            Id = entry.Id,
            ProviderType = LocalLLMProvider.Type,
            DisplayName = entry.DisplayName,
            OwnedBy = "Qwen",
            ContextWindowTokens = entry.ContextWindowTokens,
            MaxInputTokens = entry.ContextWindowTokens - 1024,
            MaxOutputTokens = 1024,
            Pricing = new ModelPricingMetadata
            {
                Currency = "USD",
                InputPer1MTokens = 0,
                OutputPer1MTokens = 0
            },
            Capabilities = new ModelCapabilityMetadata
            {
                SupportsTemperature = true,
                SupportsReasoningEffort = true,
                SupportsStructuredOutput = true,
                SupportsTools = true,
                SupportsJsonMode = true,
                SupportsVision = false,
                SupportsAudio = false,
                SupportsEmbeddings = false,
                SupportedReasoningEfforts = ["minimal", "low", "medium", "high", "max"]
            },
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["license"] = entry.License,
                ["sizeBytes"] = entry.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sha256"] = entry.Sha256,
                ["source"] = entry.DownloadUri.ToString()
            }
        };

    internal static string ResolveModelPath(string modelsDirectory, LocalModelCatalogEntry entry)
    {
        var root = Path.GetFullPath(modelsDirectory);
        var path = Path.GetFullPath(Path.Combine(root, entry.FileName));
        if (!GnOuGo.Workspace.GnOuGoWorkspace.IsPathWithinRoot(path, root))
            throw new InvalidOperationException("The local model path escaped the configured model directory.");
        return path;
    }
}

public sealed class LocalLLMModelCatalogProvider : ILLMModelCatalogProvider
{
    private readonly ILocalModelManager _models;

    public LocalLLMModelCatalogProvider(ILocalModelManager models) => _models = models;

    public string ProviderType => LocalLLMProvider.Type;

    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(
        ModelProviderOptions provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var states = await _models.ListAsync(ct).ConfigureAwait(false);
        return states
            .Where(static state => state.Status == LocalModelStatus.Installed)
            .Select(state =>
            {
                var entry = LocalModelCatalog.Resolve(state.Id);
                return new LLMModelDescriptor(
                    entry.Id,
                    entry.DisplayName,
                    LocalLLMProvider.Type,
                    "Qwen",
                    entry.ContextWindowTokens,
                    entry.ContextWindowTokens - 1024,
                    1024,
                    Capabilities: new ModelCapabilityMetadata
                    {
                        SupportsTemperature = true,
                        SupportsReasoningEffort = true,
                        SupportsStructuredOutput = true,
                        SupportsTools = true,
                        SupportsJsonMode = true,
                        SupportsVision = false,
                        SupportsAudio = false,
                        SupportsEmbeddings = false,
                        SupportedReasoningEfforts = ["minimal", "low", "medium", "high", "max"]
                    },
                    Extra: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["license"] = entry.License,
                        ["sizeBytes"] = entry.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
            })
            .ToArray();
    }
}
