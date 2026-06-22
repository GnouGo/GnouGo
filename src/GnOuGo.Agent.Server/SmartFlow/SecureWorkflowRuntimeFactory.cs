using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
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

    public SecureWorkflowRuntimeFactory(
        LLMRuntimeOptionsStore optionsStore,
        IKeyVaultRuntimeConfigStore keyVaultStore,
        ILoggerFactory? loggerFactory = null,
        ILLMClient? llmClientOverride = null,
        IMcpClientFactory? mcpClientFactoryOverride = null,
        IMemoryCache? backgroundModeCache = null)
    {
        _optionsStore = optionsStore;
        _keyVaultStore = keyVaultStore;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _llmClientOverride = llmClientOverride;
        _mcpClientFactoryOverride = mcpClientFactoryOverride;
        _backgroundModeCache = backgroundModeCache;
    }

    internal async Task<SecureWorkflowRuntimeSession> CreateAsync(CancellationToken ct)
    {
        var options = await _keyVaultStore.BuildEffectiveOptionsAsync(_optionsStore.Current, ct);
        var sslLogger = _loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
        var http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, LLMHttpClientDefaults.MinimumTimeout, sslLogger);
        IMcpClientFactory mcpFactory = _mcpClientFactoryOverride ?? (options.McpServers.Count > 0
            ? new ConfiguredMcpClientFactory(options.McpServers)
            : new InMemoryMcpClientFactory());

        var llmClient = _llmClientOverride
            ?? new SnapshotRoutingLlmClientAdapter(http, options, _loggerFactory, _backgroundModeCache);

        return new SecureWorkflowRuntimeSession(
            llmClient,
            mcpFactory,
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
        LLMOptions options,
        HttpClient httpClient)
    {
        LlmClient = llmClient;
        McpClientFactory = mcpClientFactory;
        Options = options;
        _httpClient = httpClient;
    }

    public ILLMClient LlmClient { get; }

    public IMcpClientFactory McpClientFactory { get; }

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

    public SnapshotRoutingLlmClientAdapter(
        HttpClient http,
        LLMOptions options,
        ILoggerFactory loggerFactory,
        IMemoryCache? backgroundModeCache = null)
    {
        _http = http;
        _options = options;
        _loggerFactory = loggerFactory;
        _backgroundModeCache = backgroundModeCache;
    }

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var routingClient = new RoutingLLMClient(_http, _options, _loggerFactory, _backgroundModeCache);
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

