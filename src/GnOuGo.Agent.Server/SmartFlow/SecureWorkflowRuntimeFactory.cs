using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IMemoryCache? _backgroundModeCache;
    private readonly ILLMCapabilityResolver? _llmCapabilityResolver;
    private readonly IHumanInputProvider? _humanInputProvider;
    private readonly ILocalLLMRuntime? _localRuntime;

    internal bool UsesLiveMcpConfiguration => _mcpClientFactoryOverride is null;

    public SecureWorkflowRuntimeFactory(
        LLMRuntimeOptionsStore optionsStore,
        IKeyVaultRuntimeConfigStore keyVaultStore,
        ILoggerFactory? loggerFactory = null,
        ILLMClient? llmClientOverride = null,
        IMcpClientFactory? mcpClientFactoryOverride = null,
        IMemoryCache? backgroundModeCache = null,
        ILLMCapabilityResolver? llmCapabilityResolver = null,
        IHumanInputProvider? humanInputProvider = null,
        ILocalLLMRuntime? localRuntime = null)
    {
        _optionsStore = optionsStore;
        _keyVaultStore = keyVaultStore;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _llmClientOverride = llmClientOverride;
        _mcpClientFactoryOverride = mcpClientFactoryOverride;
        _backgroundModeCache = backgroundModeCache;
        _llmCapabilityResolver = llmCapabilityResolver;
        _humanInputProvider = humanInputProvider;
        _localRuntime = localRuntime;
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

        var llmClient = _llmClientOverride
            ?? new SnapshotRoutingLlmClientAdapter(http, options, _loggerFactory, _backgroundModeCache, _localRuntime);

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
    private readonly IMemoryCache? _backgroundModeCache;
    private readonly ILocalLLMRuntime? _localRuntime;

    public SnapshotRoutingLlmClientAdapter(
        HttpClient http,
        LLMOptions options,
        ILoggerFactory loggerFactory,
        IMemoryCache? backgroundModeCache = null,
        ILocalLLMRuntime? localRuntime = null)
    {
        _http = http;
        _options = options;
        _loggerFactory = loggerFactory;
        _backgroundModeCache = backgroundModeCache;
        _localRuntime = localRuntime;
    }

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var providers = RoutingLLMClient.CreateDefaultProviders(_http, _loggerFactory, _backgroundModeCache).AsEnumerable();
        if (_localRuntime is not null)
            providers = providers.Append(new LocalLLMProvider(_localRuntime));
        var routingClient = new RoutingLLMClient(_options, providers);
        var aiRequest = new LLMClientRequest
        {
            Provider = request.Provider,
            Model = request.Model,
            Prompt = request.Prompt,
            Temperature = request.Temperature,
            StructuredOutputSchema = request.StructuredOutputSchema,
            StructuredOutputStrict = request.StructuredOutputStrict,
            Reasoning = request.Reasoning,
            UseBackgroundMode = request.UseBackgroundMode,
        };

        if (request.Tools is { Count: > 0 })
        {
            aiRequest.Tools = request.Tools.Select(t => new LLMToolDef
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema?.DeepClone()
            }).ToList();
        }

        var aiResponse = await routingClient.CallAsync(aiRequest, ct);
        var response = new LLMResponse
        {
            Text = aiResponse.Text,
            Json = aiResponse.Json,
            Usage = aiResponse.Usage,
            Raw = aiResponse.Raw,
        };

        if (aiResponse.ToolCalls is { Count: > 0 })
        {
            response.ToolCalls = aiResponse.ToolCalls.Select(tc => new LLMToolCall
            {
                Id = tc.Id,
                Name = tc.Name,
                Arguments = tc.Arguments
            }).ToList();
        }

        return response;
    }
}
