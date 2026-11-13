namespace GnOuGo.AI.Core;

/// <summary>
/// In-process local model runtime. Implementations must not require an HTTP service or child process.
/// </summary>
public interface ILocalLLMRuntime
{
    Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default);
}

/// <summary>Optional declaration by runtimes that consume native incremental generation.</summary>
public interface ILocalLLMStreamingRuntime
{
    bool SupportsNativeStreaming { get; }
}

/// <summary>Manages host-scoped model assets used by an embedded local runtime.</summary>
public interface ILocalModelManager
{
    Task<LocalModelInfo> InstallAsync(
        string modelId,
        IProgress<LocalModelProgress>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken ct = default);

    Task<bool> RemoveAsync(string modelId, CancellationToken ct = default);
}

public enum LocalModelStatus
{
    NotInstalled,
    Partial,
    Installed,
    Corrupt
}

public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    LocalModelStatus Status,
    long DownloadedBytes,
    long TotalBytes,
    string License,
    string Source);

public sealed record LocalModelProgress(
    string ModelId,
    long DownloadedBytes,
    long TotalBytes,
    double Percentage);

/// <summary>Classifies failures that are eligible for the bounded local retry/fallback policy.</summary>
public enum LocalLLMFailureKind
{
    ModelUnavailable,
    ModelLoad,
    Inference,
    InvalidStructuredOutput
}

/// <summary>Typed, redacted local-provider failure.</summary>
public sealed class LocalLLMException : Exception
{
    public LocalLLMException(
        LocalLLMFailureKind kind,
        string message,
        Exception? innerException = null,
        IReadOnlyList<string>? validationErrors = null,
        bool generationStarted = false)
        : base(message, innerException)
    {
        Kind = kind;
        GenerationStarted = generationStarted;
        ValidationErrors = validationErrors ?? [];
    }

    public LocalLLMFailureKind Kind { get; }

    public bool GenerationStarted { get; }

    public IReadOnlyList<string> ValidationErrors { get; }
}

/// <summary>Adapts an embedded runtime to the standard provider contract.</summary>
public sealed class LocalLLMProvider : ILLMProvider
{
    public const string Type = "local";

    private readonly ILocalLLMRuntime _runtime;

    public LocalLLMProvider(ILocalLLMRuntime runtime) => _runtime = runtime;

    public string ProviderType => Type;

    public async Task<LLMClientResponse> CallAsync(
        string model,
        ModelProviderOptions provider,
        LLMClientRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        request = CloneRequest(request);
        request.ResolvedTransport ??= LLMTransportResolver.ForRequest(model, provider, request);
        if (request.ResolvedTransport.IsStreaming && _runtime is not ILocalLLMStreamingRuntime { SupportsNativeStreaming: true })
        {
            if (request.ResolvedTransport.RequestedMode == LLMStreamingMode.Streaming)
                throw new InvalidOperationException("The configured local runtime does not declare native streaming support.");
            request.ResolvedTransport = request.ResolvedTransport with { EffectiveMode = LLMStreamingMode.NonStreaming, CapabilitySource = "local runtime has no native streaming declaration" };
        }
        var localRequest = CloneRequest(request);
        localRequest.Provider = Type;
        localRequest.Model = model;

        try
        {
            return await _runtime.CallAsync(localRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LocalLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalLLMException(
                LocalLLMFailureKind.Inference,
                "The embedded local model could not complete the request.",
                ex);
        }
    }

    internal static LLMClientRequest CloneRequest(LLMClientRequest request)
        => new()
        {
            ResolvedTransport = request.ResolvedTransport,
            DeclaredTransportCapabilities = request.DeclaredTransportCapabilities,
            RequireOutputTokenLimit = request.RequireOutputTokenLimit,
            DisableTransportRetries = request.DisableTransportRetries,
            Provider = request.Provider,
            Model = request.Model,
            Prompt = request.Prompt,
            Temperature = request.Temperature,
            StructuredOutputSchema = request.StructuredOutputSchema?.DeepClone(),
            StructuredOutputStrict = request.StructuredOutputStrict,
            Reasoning = request.Reasoning,
            UseBackgroundMode = request.UseBackgroundMode,
            Tools = request.Tools,
            MaxOutputTokens = request.MaxOutputTokens
        };
}
