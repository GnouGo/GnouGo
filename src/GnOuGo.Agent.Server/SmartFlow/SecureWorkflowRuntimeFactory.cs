using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed class SecureWorkflowRuntimeFactory
{
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly IKeyVaultRuntimeConfigStore _keyVaultStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILLMClient? _llmClientOverride;
    private readonly IMcpClientFactory? _mcpClientFactoryOverride;
    private readonly ILLMCapabilityResolver? _llmCapabilityResolver;
    private readonly IHumanInputProvider? _humanInputProvider;
    private readonly ILocalLLMRuntime? _localRuntime;
    private readonly Reviews.ReviewPublicationService? _reviews;
    private readonly LlmTraceCapture? _capture;

    internal bool UsesLiveMcpConfiguration => _mcpClientFactoryOverride is null;

    public SecureWorkflowRuntimeFactory(
        LLMRuntimeOptionsStore optionsStore,
        IKeyVaultRuntimeConfigStore keyVaultStore,
        ILoggerFactory? loggerFactory = null,
        ILLMClient? llmClientOverride = null,
        IMcpClientFactory? mcpClientFactoryOverride = null,
        ILLMCapabilityResolver? llmCapabilityResolver = null,
        IHumanInputProvider? humanInputProvider = null,
        ILocalLLMRuntime? localRuntime = null,
        Reviews.ReviewPublicationService? reviews = null,
        LlmTraceCapture? capture = null)
    {
        _optionsStore = optionsStore;
        _keyVaultStore = keyVaultStore;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _llmClientOverride = llmClientOverride;
        _mcpClientFactoryOverride = mcpClientFactoryOverride;
        _llmCapabilityResolver = llmCapabilityResolver;
        _humanInputProvider = humanInputProvider;
        _localRuntime = localRuntime;
        _reviews = reviews;
        _capture = capture;
    }

    internal async Task<SecureWorkflowRuntimeSession> CreateAsync(CancellationToken ct)
    {
        var options = await _keyVaultStore.BuildEffectiveOptionsAsync(_optionsStore.Current, ct);
        var sslLogger = _loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
        var http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, LLMHttpClientDefaults.MinimumTimeout, sslLogger);
        IMcpClientFactory mcpFactory = _mcpClientFactoryOverride ?? (options.McpServers.Count > 0
            ? new ConfiguredMcpClientFactory(
                options.McpServers,
                _humanInputProvider,
                options.DefaultProvider,
                options.DefaultModel)
            : new InMemoryMcpClientFactory());
        if (_reviews is not null) mcpFactory = _reviews.Decorate(mcpFactory);

        var llmClient = _llmClientOverride
            ?? new SnapshotRoutingLlmClientAdapter(http, options, _loggerFactory, _localRuntime, _capture);

        return new SecureWorkflowRuntimeSession(
            llmClient,
            mcpFactory,
            _llmCapabilityResolver,
            options,
            http);
    }
}

internal sealed class SecureWorkflowRuntimeSession : IAsyncDisposable
{
    private readonly HttpClient _httpClient;

    public SecureWorkflowRuntimeSession(
        ILLMClient llmClient,
        IMcpClientFactory mcpClientFactory,
        ILLMCapabilityResolver? llmCapabilityResolver,
        LLMOptions options,
        HttpClient httpClient)
    {
        LlmClient = llmClient;
        McpClientFactory = mcpClientFactory;
        LlmCapabilityResolver = llmCapabilityResolver;
        Options = options;
        _httpClient = httpClient;
    }

    public ILLMClient LlmClient { get; }

    public IMcpClientFactory McpClientFactory { get; }

    public ILLMCapabilityResolver? LlmCapabilityResolver { get; }

    public LLMOptions Options { get; }

    public async ValueTask DisposeAsync()
    {
        if (McpClientFactory is IAsyncDisposable disposableFactory)
            await disposableFactory.DisposeAsync();

        _httpClient.Dispose();
    }
}

internal sealed class SnapshotRoutingLlmClientAdapter : ILLMClient
{
    private readonly HttpClient _http;
    private readonly LLMOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILocalLLMRuntime? _localRuntime;
    private readonly LlmTraceCapture? _capture;

    public SnapshotRoutingLlmClientAdapter(
        HttpClient http,
        LLMOptions options,
        ILoggerFactory loggerFactory,
        ILocalLLMRuntime? localRuntime = null,
        LlmTraceCapture? capture = null)
    {
        _http = http;
        _options = options;
        _loggerFactory = loggerFactory;
        _localRuntime = localRuntime;
        _capture = capture;
    }

    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        => _capture is null ? DispatchAsync(request, ct)
            : _capture.CallAsync(request, _options, DispatchAsync, ct);

    private async Task<LLMResponse> DispatchAsync(LLMRequest request, CancellationToken ct)
    {
        var providers = RoutingLLMClient.CreateDefaultProviders(_http, _loggerFactory).AsEnumerable();
        if (_localRuntime is not null)
            providers = providers.Append(new LocalLLMProvider(_localRuntime));
        var routingClient = new RoutingLLMClient(_options, providers);
        var aiRequest = RoutingLLMClientAdapter.MapRequest(request);

        LLMClientResponse aiResponse;
        try
        {
            aiResponse = await routingClient.CallAsync(aiRequest, ct);
        }
        catch (LLMProviderException ex)
        {
            throw LLMProviderFailureMapper.Map(ex);
        }
        return RoutingLLMClientAdapter.MapResponse(aiResponse);
    }
}
