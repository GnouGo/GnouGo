using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.AI.Core;
using OtlpTenantCollector.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SmartFlowServiceTests
{
    [Fact]
    public async Task ExecuteAsync_UsesPersistedDefaultAgentWorkflow_WhenNoAgentNameIsProvided()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-smartflow-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Select(TestServerAddressResolver.NormalizeBaseAddress)
                .First();

            await SeedAgentAndUserConfigAsync(dbPath);

            var options = new LLMOptions
            {
                DefaultProvider = string.Empty,
                DefaultModel = string.Empty,
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{address}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            };

            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
            var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, keyVaultStore);
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            var smartFlow = new SmartFlowService(
                new RecordingLlmClient(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                runtimeFactory,
                SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
                SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
                new AgentHumanInputProvider(),
                SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
                NullLogger<SmartFlowService>.Instance,
                userConfigClient);

            var events = await SmartFlowTestFactory.CollectAsync(
                smartFlow.ExecuteAsync("Explain SlimFaas", correlationId: "corr-smartflow", agentName: null, CancellationToken.None));

            Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "AGENT: Explain SlimFaas");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_PrefersPersistedDefaultAgentOverRequestedAgentName()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-smartflow-preferred-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Select(TestServerAddressResolver.NormalizeBaseAddress)
                .First();

            await SeedAgentAndUserConfigAsync(dbPath);
            await SeedAgentAsync(dbPath, "legacy-agent", "LEGACY");

            var options = new LLMOptions
            {
                DefaultProvider = string.Empty,
                DefaultModel = string.Empty,
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{address}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            };

            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
            var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, keyVaultStore);
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            var smartFlow = new SmartFlowService(
                new RecordingLlmClient(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                runtimeFactory,
                SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
                SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
                new AgentHumanInputProvider(),
                SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
                NullLogger<SmartFlowService>.Instance,
                userConfigClient);

            var events = await SmartFlowTestFactory.CollectAsync(
                smartFlow.ExecuteAsync("Explain SlimFaas", correlationId: "corr-smartflow-preferred", agentName: "legacy-agent", CancellationToken.None));

            Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "AGENT: Explain SlimFaas");
            Assert.DoesNotContain(events, evt => evt.Type == "answer" && evt.Text == "LEGACY: Explain SlimFaas");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistedDefaultAgentCannotBeLoaded_ReturnsErrorWithoutDynamicFallback()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-smartflow-missing-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Select(TestServerAddressResolver.NormalizeBaseAddress)
                .First();

            await SeedUserConfigAsync(dbPath, "missing-agent");

            var options = new LLMOptions
            {
                DefaultProvider = string.Empty,
                DefaultModel = string.Empty,
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{address}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            };

            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
            var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, keyVaultStore);
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            var smartFlow = new SmartFlowService(
                new RecordingLlmClient(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                runtimeFactory,
                SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
                SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
                new AgentHumanInputProvider(),
                SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
                NullLogger<SmartFlowService>.Instance,
                userConfigClient);

            var events = await SmartFlowTestFactory.CollectAsync(
                smartFlow.ExecuteAsync("Explain SlimFaas", correlationId: "corr-smartflow-missing", agentName: null, CancellationToken.None));

            Assert.Contains(events, evt => evt.Type == "error" && evt.Text is not null && evt.Text.Contains("missing-agent", StringComparison.Ordinal));
            Assert.DoesNotContain(events, evt => evt.Type == "answer");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_PersistsWorkflowSpansUnderSameTraceAsChatMessage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-smartflow-traces-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Select(TestServerAddressResolver.NormalizeBaseAddress)
                .First();

            await SeedAgentAndUserConfigAsync(dbPath);

            var options = new LLMOptions
            {
                DefaultProvider = string.Empty,
                DefaultModel = string.Empty,
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{address}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            };

            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
            var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, keyVaultStore);
            var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            var smartFlow = new SmartFlowService(
                new RecordingLlmClient(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                runtimeFactory,
                SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
                SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
                new AgentHumanInputProvider(),
                telemetryHarness.Telemetry,
                NullLogger<SmartFlowService>.Instance,
                userConfigClient);

            const string correlationId = "corr-smartflow-trace";
            var events = await SmartFlowTestFactory.CollectAsync(
                smartFlow.ExecuteAsync("Explain SlimFaas", correlationId, agentName: null, CancellationToken.None));

            Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "AGENT: Explain SlimFaas");

            var spans = DrainPersistedSpans(telemetryHarness.Queue);
            Assert.True(spans.Count >= 3);

            var distinctTraceIds = spans
                .Select(span => Convert.ToHexString(span.TraceId).ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.Single(distinctTraceIds);
            Assert.Contains(spans, span => span.Name == "chat.message");
            Assert.Contains(spans, span => span.Name == "workflow");
            Assert.Contains(spans, span => span.Name == "set final_answer");
            Assert.All(spans, span => Assert.Contains(correlationId, span.AttributesJson ?? string.Empty, StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task SeedAgentAndUserConfigAsync(string dbPath)
        => await SeedAgentAndUserConfigAsync(dbPath, "slimfaas", "AGENT");

    private static async Task SeedAgentAndUserConfigAsync(string dbPath, string agentName, string answerPrefix)
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

        db.Agents.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = agentName,
            Workflow = BuildEchoAgentWorkflow(agentName, answerPrefix),
            SchedulesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.UserConfigs.Add(new UserConfigRecord
        {
            Id = Guid.NewGuid(),
            TenantScopeKey = "global",
            DefaultAgent = agentName,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentAsync(string dbPath, string agentName, string answerPrefix)
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

        db.Agents.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = agentName,
            Workflow = BuildEchoAgentWorkflow(agentName, answerPrefix),
            SchedulesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedUserConfigAsync(string dbPath, string defaultAgent)
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

        db.UserConfigs.Add(new UserConfigRecord
        {
            Id = Guid.NewGuid(),
            TenantScopeKey = "global",
            DefaultAgent = defaultAgent,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private static string BuildEchoAgentWorkflow(string agentName, string answerPrefix)
        => $$"""
           dsl: 1
           name: {{agentName}}
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
                     answer: "${'{{answerPrefix}}: ' + data.inputs.task}"
               outputs:
                 answer:
                   expr: "${data.steps.final_answer.answer}"
                   type: string
           """;

    private static List<SpanRow> DrainPersistedSpans(OtlpTenantCollector.Services.TelemetryIngestQueue queue)
    {
        var spans = new List<SpanRow>();
        while (queue.Channel.Reader.TryRead(out var row))
        {
            if (row is SpanRow span)
                spans.Add(span);
        }

        return spans;
    }
}

