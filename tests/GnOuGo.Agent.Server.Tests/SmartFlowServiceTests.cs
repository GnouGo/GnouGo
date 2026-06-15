using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using OtlpTenantCollector.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SmartFlowServiceTests
{
    [Fact]
    public async Task ExecuteAsync_Help_ReturnsCommandOverviewWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateSmartFlowService(
            llm,
            new FakeMcpClientFactory(),
            SmartFlowTestFactory.CreateProvidersService(llm),
            SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory()));

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/help", CancellationToken.None));

        var answer = Assert.Single(events, evt => evt.Type == "answer");
        Assert.Contains("# GnOuGo Help", answer.Text);
        Assert.Contains("`/llm add`", answer.Text);
        Assert.Contains("`/embedding add`", answer.Text);
        Assert.Contains("`/mcp add`", answer.Text);
        Assert.Contains("`/gnougo add`", answer.Text);
        Assert.Contains("`/status`", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPersistedDefaultAgentWorkflow_WhenNoAgentNameIsProvided()
    {
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

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
    public async Task ExecuteAsync_WhenPersistedAgentExecutionFailsAndUserApprovesRepair_UpdatesAgentThroughAgentMcp()
    {
        const string agentId = "8d8871b7-01cf-4a42-a95a-391d633d7d37";
        const string agentName = "broken-agent";
        var brokenWorkflow = """
            version: 1
            name: broken-agent
            workflows:
              main:
                inputs:
                  task:
                    type: string
                    required: true
                steps:
                  - id: failing_lookup
                    type: mcp.call
                    input:
                      server: GnOuGo.Agent.Mcp
                      method: agent_get_by_name
                      request:
                        name: missing-agent
                outputs:
                  answer:
                    expr: "${data.steps.failing_lookup.response.agent.name}"
                    type: string
            """;
        var repairedWorkflow = """
            version: 1
            name: broken-agent
            skill:
              description: Repaired test agent.
              tags: [test]
              inputs:
                task:
                  type: string
                  description: User task.
              outputs:
                answer:
                  type: string
                  description: Final answer.
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
                      answer: "${'REPAIRED: ' + data.inputs.task}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;

        string? persistedWorkflow = null;
        var agentMcp = new FakeMcpSession(AgentMcpHostingExtensions.ServerName)
            .OnTool("agent_get_by_name", (arguments, _) =>
            {
                var name = arguments?["name"]?.GetValue<string>() ?? "";
                if (string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new McpCallResult
                    {
                        IsError = false,
                        Content = new JsonObject
                        {
                            ["success"] = true,
                            ["agent"] = new JsonObject
                            {
                                ["id"] = agentId,
                                ["name"] = agentName,
                                ["workflow"] = brokenWorkflow,
                                ["original_prompt"] = "answer test prompts",
                                ["created_at"] = "2026-06-15T00:00:00Z",
                                ["updated_at"] = "2026-06-15T00:00:00Z"
                            }
                        }
                    });
                }

                return Task.FromResult(new McpCallResult
                {
                    IsError = true,
                    Content = new JsonObject
                    {
                        ["success"] = false,
                        ["error_code"] = "NOT_FOUND",
                        ["error_message"] = $"Agent '{name}' not found."
                    }
                });
            })
            .OnTool("agent_update", (arguments, _) =>
            {
                persistedWorkflow = arguments?["workflow"]?.GetValue<string>();
                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = new JsonObject
                        {
                            ["id"] = agentId,
                            ["name"] = agentName,
                            ["workflow"] = persistedWorkflow,
                            ["original_prompt"] = arguments?["originalPrompt"]?.GetValue<string>() ?? "",
                            ["created_at"] = "2026-06-15T00:00:00Z",
                            ["updated_at"] = "2026-06-15T00:01:00Z"
                        }
                    }
                });
            });

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(cts.Token))
            {
                if (request.StepId == "agent_workflow_repair")
                {
                    humanInput.TrySubmitResponse(
                        request.RunId,
                        request.StepId,
                        new JsonObject { ["response"] = "improve" });
                    break;
                }
            }
        }, cts.Token);

        var options = new LLMOptions
        {
            DefaultProvider = "test",
            DefaultModel = "repair-model"
        };
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            runtimeStore,
            keyVaultStore,
            llmClientOverride: new FixedWorkflowPlanLlmClient(repairedWorkflow),
            mcpClientFactoryOverride: new FakeMcpClientFactory(agentMcp));

        var smartFlow = new SmartFlowService(
            new RecordingLlmClient(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            runtimeFactory,
            SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
            SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
            humanInput,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<SmartFlowService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(
            smartFlow.ExecuteAsync("please answer", correlationId: "corr-repair", agentName: agentName, CancellationToken.None));

        await responder;

        Assert.Equal(repairedWorkflow, persistedWorkflow);
        Assert.Contains(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("agent_workflow_repair", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text?.Contains("improved and saved", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(events, evt => evt.Type == "error");
    }

    [Fact]
    public async Task ExecuteAsync_PrefersPersistedDefaultAgentOverRequestedAgentName()
    {
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

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
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

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
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

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

    [Fact]
    public async Task GetActiveWorkflowInputSchemaAsync_ReturnsEmbeddedPromptSchema_WhenNoAgentIsSelected()
    {
        var smartFlow = SmartFlowTestFactory.CreateSmartFlowService(
            new RecordingLlmClient(),
            new FakeMcpClientFactory(),
            SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
            SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()));

        var schema = await smartFlow.GetActiveWorkflowInputSchemaAsync(agentName: null, CancellationToken.None);

        Assert.True(schema.IsPromptOnly);
        Assert.Contains(schema.Fields, field => field.Name == "prompt" && field.Type == "string");
        Assert.Contains(schema.Fields, field => field.Name == "conversation_id" && field.Required == false);
    }

    [Fact]
    public void WorkflowInputComposer_TryBuildInputs_ParsesNestedAndComplexValues()
    {
        var schema = new WorkflowInputSchema("test-agent", new WorkflowInputFieldSchema[]
        {
            new("topic", "string", true),
            new("enabled", "boolean", true),
            new("score", "number", true),
            new("tags", "array", true),
            new("options", "object", true, Properties: new WorkflowInputFieldSchema[]
            {
                new("audience", "string", true),
                new("limits", "object", true)
            })
        });

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = "SlimFaas",
            ["enabled"] = "yes",
            ["score"] = "42.5",
            ["tags"] = "- api\n- workflow",
            ["options.audience"] = "developers",
            ["options.limits"] = "{ \"max\": 3 }"
        };

        var success = WorkflowInputComposer.TryBuildInputs(schema, values, out var inputs, out var errors);

        Assert.True(success, string.Join(Environment.NewLine, errors));
        Assert.Equal("SlimFaas", inputs["topic"]!.GetValue<string>());
        Assert.True(inputs["enabled"]!.GetValue<bool>());
        Assert.Equal(42.5m, inputs["score"]!.GetValue<decimal>());
        Assert.Equal("api", inputs["tags"]![0]!.GetValue<string>());
        Assert.Equal("developers", inputs["options"]!["audience"]!.GetValue<string>());
        Assert.Equal(3, inputs["options"]!["limits"]!["max"]!.GetValue<int>());
    }

    private static async Task SeedAgentAndUserConfigAsync(string dbPath)
        => await SeedAgentAndUserConfigAsync(dbPath, "slimfaas", "AGENT");

    private static async Task SeedAgentAndUserConfigAsync(string dbPath, string agentName, string answerPrefix)
        => await SeedAgentAndUserConfigWithWorkflowAsync(dbPath, agentName, BuildEchoAgentWorkflow(agentName, answerPrefix));

    private static async Task SeedAgentAndUserConfigWithWorkflowAsync(string dbPath, string agentName, string workflow)
    {
        await AgentMcpTestPersistence.SeedAgentAsync(dbPath, agentName, workflow);
        await AgentMcpTestPersistence.SeedUserConfigAsync(dbPath, new UserConfigUpdate(null, null, agentName));
    }

    private static async Task SeedAgentAsync(string dbPath, string agentName, string answerPrefix)
    {
        await AgentMcpTestPersistence.SeedAgentAsync(dbPath, agentName, BuildEchoAgentWorkflow(agentName, answerPrefix));
    }

    private static async Task SeedUserConfigAsync(string dbPath, string defaultAgent)
    {
        await AgentMcpTestPersistence.SeedUserConfigAsync(dbPath, new UserConfigUpdate(null, null, defaultAgent));
    }

    private static string BuildEchoAgentWorkflow(string agentName, string answerPrefix)
        => $$"""
           version: 1
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

    private sealed class FixedWorkflowPlanLlmClient : ILLMClient
    {
        private readonly string _workflowYaml;

        public FixedWorkflowPlanLlmClient(string workflowYaml)
        {
            _workflowYaml = workflowYaml;
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse { Text = _workflowYaml });
    }

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
