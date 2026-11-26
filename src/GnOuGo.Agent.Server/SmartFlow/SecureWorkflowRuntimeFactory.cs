using GnOuGo.Flow.Copilot;
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
    private readonly LlmTraceCapture? _capture;
    private readonly IReadOnlyDictionary<string, string> _copilotRunners;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>>? _copilotTools;
    private readonly LlmDefaultsResolver? _defaults;
    private readonly IConfiguration? _agentConfiguration;
    private readonly IAgentTaskRunner? _codeRunner;

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
        LlmTraceCapture? capture = null,
        IReadOnlyDictionary<string, string>? copilotRunners = null,
        LlmDefaultsResolver? defaults = null,
        IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>>? copilotTools = null, IConfiguration? agentConfiguration = null, IAgentTaskRunner? codeRunner = null)
    {
        _optionsStore = optionsStore;
        _keyVaultStore = keyVaultStore;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _llmClientOverride = llmClientOverride;
        _mcpClientFactoryOverride = mcpClientFactoryOverride;
        _llmCapabilityResolver = llmCapabilityResolver;
        _humanInputProvider = humanInputProvider;
        _localRuntime = localRuntime;
        _capture = capture;
        _copilotRunners = copilotRunners ?? new Dictionary<string, string>();
        _defaults = defaults;
        _copilotTools = copilotTools;
        _agentConfiguration = agentConfiguration;
        _codeRunner = codeRunner;
    }

    internal async Task<SecureWorkflowRuntimeSession> CreateAsync(CancellationToken ct, GnOuGo.Flow.Core.Planning.PlanningRequest? planning = null)
    {
        var options = _defaults is null ? await _keyVaultStore.BuildEffectiveOptionsAsync(_optionsStore.Current, ct)
            : await _defaults.ResolveAsync(ct);
        options = GnOuGo.Flow.Integrations.Planning.CapturedPlanningModel.Restore(options, planning);
        var sslLogger = _loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
        var http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, LLMHttpClientDefaults.MinimumTimeout, sslLogger);
        var workflowServers = options.McpServers.Where(pair => pair.Key != "GnOuGo.GithubCopilot.Mcp")
            .ToDictionary(pair => pair.Key, pair => pair.Value, options.McpServers.Comparer);
        IMcpClientFactory mcpFactory = _mcpClientFactoryOverride ?? (workflowServers.Count > 0
            ? new ConfiguredMcpClientFactory(
                workflowServers,
                _humanInputProvider,
                options.DefaultProvider,
                options.DefaultModel)
            : new InMemoryMcpClientFactory());

        var llmClient = _llmClientOverride
            ?? new SnapshotRoutingLlmClientAdapter(http, options, _loggerFactory, _localRuntime, _capture);

        return new SecureWorkflowRuntimeSession(
            llmClient,
            mcpFactory,
            planning?.Options[GnOuGo.Flow.Integrations.Planning.CapturedPlanningModel.OptionName] is not null
                ? GnOuGo.Flow.Integrations.Planning.CapturedPlanningModel.Capabilities(options) : _llmCapabilityResolver,
            options,
            http) { CopilotRunners = _copilotRunners, CopilotTools = _copilotTools, AgentConfiguration = _agentConfiguration, CodeRunner = _codeRunner };
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
    public IReadOnlyDictionary<string, string> CopilotRunners { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>>? CopilotTools { get; init; }
    public IConfiguration? AgentConfiguration { get; init; }
    internal IAgentTaskRunner? CodeRunner { get; init; }
    public WorkflowEngine ConfigureAgentRunners(WorkflowEngine engine) => engine.WithCopilotRunners(CopilotRunners, CopilotTools, AgentConfiguration, CodeRunner);

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
