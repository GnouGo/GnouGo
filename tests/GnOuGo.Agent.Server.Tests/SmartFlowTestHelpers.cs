using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

internal sealed class RecordingLlmClient : ILLMClient
{
    public int CallCount { get; private set; }
    public List<LLMRequest> Requests { get; } = new();

    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        CallCount++;
        Requests.Add(request);
        return Task.FromResult(new LLMResponse { Text = "stub-response" });
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

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

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
        IMcpClientFactory mcpFactory,
        ILLMModelCatalog? modelCatalog = null,
        LLMOptions? options = null,
        AgentHumanInputProvider? humanInput = null)
        => new(
            llmClient,
            mcpFactory,
            new MemoryCache(new MemoryCacheOptions()),
            humanInput ?? new AgentHumanInputProvider(),
            modelCatalog ?? new FakeModelCatalog(),
            CreateRuntimeOptionsStore(options),
            new AgentOTelTelemetry(),
            NullLogger<ConfigureProvidersService>.Instance);

    public static ConfigureAgentsService CreateAgentsService(
        RecordingLlmClient llmClient,
        IMcpClientFactory mcpFactory,
        LLMOptions? options = null)
        => new(
            llmClient,
            mcpFactory,
            new MemoryCache(new MemoryCacheOptions()),
            new AgentHumanInputProvider(),
            CreateRuntimeOptionsStore(options),
            new AgentOTelTelemetry(),
            NullLogger<ConfigureAgentsService>.Instance);

    public static LLMRuntimeOptionsStore CreateRuntimeOptionsStore(LLMOptions? options = null)
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions()),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        var field = typeof(LLMRuntimeOptionsStore)
            .GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not access LLMRuntimeOptionsStore._current.");

        field.SetValue(store, options ?? new LLMOptions
        {
            DefaultProvider = "OpenAi",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        return store;
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

    public static JsonObject AgentSummary(string id, string name, int schedulesCount, string updatedAt)
    {
        var schedules = new JsonArray();
        for (var i = 0; i < schedulesCount; i++)
        {
            schedules.Add(new JsonObject
            {
                ["name"] = $"schedule-{i + 1}",
                ["cron"] = "0 8 * * *"
            });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["workflow"] = "dsl: 1",
            ["schedules"] = schedules,
            ["updated_at"] = updatedAt
        };
    }
}

