using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Runtime;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

internal sealed class FakeKeyVaultRuntimeConfigStore : IKeyVaultRuntimeConfigStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyVaultSecretSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private LLMOptions? _effectiveOptions;

    public FakeKeyVaultRuntimeConfigStore WithEffectiveOptions(LLMOptions options)
    {
        _effectiveOptions = options;
        return this;
    }

    public FakeKeyVaultRuntimeConfigStore AddSecret(string key, string value, int latestVersion = 1, string? createdAt = null)
    {
        _values[key] = value;
        _summaries[key] = new KeyVaultSecretSummary(key, createdAt ?? DateTimeOffset.UtcNow.ToString("O"), latestVersion);
        return this;
    }

    public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>(_summaries.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<string?> GetSecretValueAsync(string key, CancellationToken ct)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SaveSecretValueAsync(string key, string value, CancellationToken ct)
    {
        AddSecret(key, value, _summaries.TryGetValue(key, out var summary) ? summary.LatestVersion + 1 : 1);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSecretAsync(string key, CancellationToken ct)
    {
        var removed = _values.Remove(key);
        _summaries.Remove(key);
        return Task.FromResult(removed);
    }

    public Task<LLMOptions> BuildEffectiveOptionsAsync(LLMOptions baseOptions, CancellationToken ct)
    {
        return Task.FromResult(_effectiveOptions ?? baseOptions);
    }
}

internal sealed class RecordingLlmClient : ILLMClient
{
    public int CallCount { get; private set; }
    public LLMRequest? LastRequest { get; private set; }

    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(new LLMResponse { Text = BuildResponseText(request) });
    }

    private static string BuildResponseText(LLMRequest request)
    {
        if (request.Prompt.Contains("Generate a valid GnOuGo.Flow YAML workflow", StringComparison.OrdinalIgnoreCase)
            || request.Prompt.Contains("Return only a complete workflow YAML document", StringComparison.OrdinalIgnoreCase))
        {
            return """
                version: 1
                name: generated-agent
                skill:
                  description: Generated chat agent workflow.
                  tags: [agent, generated]
                  inputs:
                    task:
                      type: string
                      description: User request to answer.
                  outputs:
                    answer:
                      type: string
                      description: Final answer for the user.
                workflows:
                  main:
                    inputs:
                      task:
                        type: string
                        required: true
                    steps:
                      - id: final_answer
                        type: set
                        input:
                          answer: "${data.inputs.task}"
                    outputs:
                      answer:
                        expr: "${data.steps.final_answer.answer}"
                        type: string
                """;
        }

        return "stub-response";
    }
}

internal sealed class FakeModelCatalog : ILLMModelCatalog
{
    private readonly Dictionary<string, IReadOnlyList<LLMModelDescriptor>> _results =
        new(StringComparer.OrdinalIgnoreCase);

    public int CallCount { get; private set; }

    public FakeModelCatalog Add(string provider, params LLMModelDescriptor[] models)
    {
        _results[provider] = models;
        return this;
    }

    public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        CallCount++;
        if (_results.TryGetValue(provider, out var models))
            return Task.FromResult(models);

        throw new InvalidOperationException($"No fake model catalog entry registered for '{provider}'.");
    }
}

internal sealed class FakeMcpClientFactory : IMcpClientFactory
{
    private readonly Dictionary<string, IMcpSession> _sessions;

    public FakeMcpClientFactory(params IMcpSession[] sessions)
    {
        _sessions = sessions.ToDictionary(s => s.ServerName, StringComparer.OrdinalIgnoreCase);
        ServerMetadata = _sessions.Keys
            .Select(name => new McpServerMetadata { Name = name, Description = $"Fake session for {name}" })
            .ToList();
    }

    public IReadOnlyList<McpServerMetadata> ServerMetadata { get; }

    public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
    {
        if (_sessions.TryGetValue(serverName, out var session))
            return Task.FromResult(session);

        throw new InvalidOperationException($"No fake MCP session registered for '{serverName}'.");
    }
}

internal sealed class FakeMcpSession : IMcpSession
{
    private readonly Dictionary<string, Func<JsonNode?, CancellationToken, Task<McpCallResult>>> _toolHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<McpToolInfo> _tools = [];

    public FakeMcpSession(string serverName)
    {
        ServerName = serverName;
    }

    public string ServerName { get; }

    public FakeMcpSession OnTool(string toolName, JsonObject response)
        => OnTool(toolName, (_, _) => Task.FromResult(new McpCallResult { IsError = false, Content = response.DeepClone() }));

    public FakeMcpSession OnTool(string toolName, Func<JsonNode?, CancellationToken, Task<McpCallResult>> handler)
    {
        _toolHandlers[toolName] = handler;
        return this;
    }

    public FakeMcpSession WithTool(
        string toolName,
        string? description = null,
        JsonNode? inputSchema = null,
        JsonNode? outputSchema = null,
        JsonNode? exampleResponse = null)
    {
        _tools.Add(new McpToolInfo
        {
            Name = toolName,
            Description = description,
            InputSchema = inputSchema?.DeepClone(),
            OutputSchema = outputSchema?.DeepClone(),
            ExampleResponse = exampleResponse?.DeepClone()
        });
        return this;
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>(_tools);

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

    public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

    public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
    {
        if (_toolHandlers.TryGetValue(toolName, out var handler))
            return handler(arguments, ct);

        throw new InvalidOperationException($"No fake MCP tool handler registered for '{ServerName}/{toolName}'.");
    }

    public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
        => throw new NotSupportedException("Prompt calls are not used by these tests.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class SmartFlowTestFactory
{
    public static ConfigureProvidersService CreateProvidersService(
        RecordingLlmClient llmClient,
        ILLMModelCatalog? modelCatalog = null,
        LLMOptions? options = null,
        AgentHumanInputProvider? humanInput = null,
        IKeyVaultRuntimeConfigStore? keyVaultStore = null,
        AgentOTelTelemetry? telemetry = null,
        BundledMcpSettings? bundledMcpSettings = null)
        => new(
            llmClient,
            humanInput ?? new AgentHumanInputProvider(),
            modelCatalog ?? new FakeModelCatalog(),
            keyVaultStore ?? new FakeKeyVaultRuntimeConfigStore(),
            CreateRuntimeOptionsStore(options),
            telemetry ?? CreateTelemetry(),
            NullLogger<ConfigureProvidersService>.Instance,
            bundledMcpSettings: Options.Create(bundledMcpSettings ?? new BundledMcpSettings()));

    public static ConfigureAgentsService CreateAgentsService(
        RecordingLlmClient llmClient,
        IMcpClientFactory mcpFactory,
        LLMOptions? options = null,
        IKeyVaultRuntimeConfigStore? keyVaultStore = null)
    {
        var runtimeStore = CreateRuntimeOptionsStore(options);
        var effectiveKeyVaultStore = keyVaultStore ?? new FakeKeyVaultRuntimeConfigStore();
        var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, effectiveKeyVaultStore);

        return new ConfigureAgentsService(
            llmClient,
            mcpFactory,
            new MemoryCache(new MemoryCacheOptions()),
            new AgentHumanInputProvider(),
            effectiveKeyVaultStore,
            runtimeFactory,
            runtimeStore,
            CreateTelemetry(),
            NullLogger<ConfigureAgentsService>.Instance);
    }

    public static SmartFlowService CreateSmartFlowService(
        RecordingLlmClient llmClient,
        IMcpClientFactory mcpFactory,
        ConfigureProvidersService configureProviders,
        ConfigureAgentsService configureAgents,
        LLMOptions? options = null,
        IKeyVaultRuntimeConfigStore? keyVaultStore = null)
    {
        var runtimeStore = CreateRuntimeOptionsStore(options);
        var effectiveKeyVaultStore = keyVaultStore ?? new FakeKeyVaultRuntimeConfigStore();
        var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, effectiveKeyVaultStore);

        return new SmartFlowService(
            llmClient,
            new MemoryCache(new MemoryCacheOptions()),
            runtimeFactory,
            configureProviders,
            configureAgents,
            new AgentHumanInputProvider(),
            CreateTelemetry(),
            NullLogger<SmartFlowService>.Instance);
    }

    private static AgentOTelTelemetry CreateTelemetry()
        => CreateTelemetryHarness().Telemetry;

    public static TelemetryHarness CreateTelemetryHarness()
    {
        var queue = new TelemetryIngestQueue(new AppOptions(
            DbPath: "ignored.db",
            BatchSize: 100,
            FlushSeconds: 1,
            ChannelCapacity: 1024,
            RetentionSweepSeconds: 60,
            DevModeEnabled: true));

        var telemetry = new AgentOTelTelemetry(new CollectorTracePersistence(
            queue,
            new TestOptionsMonitor<OpenTelemetrySettings>(new OpenTelemetrySettings
            {
                Enabled = false,
                ServiceName = "GnOuGo.Agent.Server"
            }),
            NullLogger<CollectorTracePersistence>.Instance),
            new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new OpenTelemetrySettings
            {
                Enabled = false,
                ServiceName = "GnOuGo.Agent.Server"
            })));

        return new TelemetryHarness(telemetry, queue);
    }

    public static LLMRuntimeOptionsStore CreateRuntimeOptionsStore(LLMOptions? options = null)
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions()),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        SetRuntimeOptions(store, options ?? new LLMOptions
        {
            DefaultProvider = "OpenAi",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        return store;
    }


    public static void SetRuntimeOptions(LLMRuntimeOptionsStore store, LLMOptions options)
    {
        var field = typeof(LLMRuntimeOptionsStore)
            .GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not access LLMRuntimeOptionsStore._current.");

        field.SetValue(store, options);
    }

    public static async Task<List<SmartFlowEvent>> CollectAsync(IAsyncEnumerable<SmartFlowEvent> source, CancellationToken ct = default)
    {
        var events = new List<SmartFlowEvent>();
        await foreach (var item in source.WithCancellation(ct))
            events.Add(item);
        return events;
    }

    public static JsonObject KeyVaultListSecretsResult(params (string Key, int LatestVersion, string CreatedAt)[] secrets)
    {
        var data = new JsonArray();
        foreach (var secret in secrets)
        {
            data.Add(new JsonObject
            {
                ["Key"] = secret.Key,
                ["LatestVersion"] = secret.LatestVersion,
                ["CreatedAt"] = secret.CreatedAt
            });
        }

        return new JsonObject
        {
            ["Success"] = true,
            ["Data"] = data
        };
    }

    public static JsonObject AgentListResult(params JsonObject[] agents)
    {
        var data = new JsonArray();
        foreach (var agent in agents)
            data.Add(agent.DeepClone());

        return new JsonObject
        {
            ["success"] = true,
            ["agents"] = data
        };
    }

    public static JsonObject AgentSummary(string id, string name, string updatedAt)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["workflow"] = "version: 1",
            ["updated_at"] = updatedAt
        };
    }
}

internal sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name) => currentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed record TelemetryHarness(AgentOTelTelemetry Telemetry, TelemetryIngestQueue Queue);
