using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Mcp;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Assets.Animation;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureAgentsServiceTests
{
    [Theory]
    [InlineData("/gnougo add", "/planning")]
    [InlineData("  /GnOuGo add  ", "/planning")]
    [InlineData("/gnougo reprompt reviewer", "/planning?agent=reviewer")]
    [InlineData("/gnougo reprompt review&approve", "/planning?agent=review%26approve")]
    public async Task ExecuteAsync_DefaultPlanner_OpensTypedDesignerWithoutDispatchingModels(
        string command,
        string expectedLink)
    {
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory());

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync(command, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Contains($"[Open the workflow designer]({expectedLink})", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentRemove_RevokesPersistentCopilotGrantsForStableAgentId()
    {
        var deleted = false;
        JsonObject? revokeRequest = null;
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult
            {
                Content = deleted
                    ? new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" }
                    : new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = SmartFlowTestFactory.AgentSummary(
                            "stable-agent-id",
                            "Reviewer",
                            "2026-08-07T12:00:00Z")
                    }
            }))
            .OnTool("agent_delete", (_, _) =>
            {
                deleted = true;
                return Task.FromResult(new McpCallResult
                {
                    Content = new JsonObject { ["success"] = true }
                });
            });
        var copilotMcp = new FakeMcpSession("GnOuGo.GithubCopilot.Mcp")
            .OnTool("copilot_permission_grants_revoke_agent", (request, _) =>
            {
                revokeRequest = request as JsonObject;
                return Task.FromResult(new McpCallResult
                {
                    Content = new JsonObject { ["success"] = true, ["revokedCount"] = 1 }
                });
            });
        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(cts.Token))
            {
                humanInput.TrySubmitResponse(
                    request.RunId,
                    request.StepId,
                    new JsonObject { ["response"] = "confirm" });
                break;
            }
        }, cts.Token);
        var service = CreateConfigureAgentsServiceForStreaming(
            new RecordingLlmClient(),
            humanInput,
            agentMcp,
            copilotMcp);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/gnougo remove Reviewer", cts.Token),
            cts.Token);
        await responder;

        Assert.True(deleted);
        Assert.Contains(events, item => item.Text?.Contains("removed", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("stable-agent-id", revokeRequest?["agentId"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(revokeRequest?["tenantId"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ExecuteAsync_AgentSelect_WhenAgentExists_EmitsAnswerAndAgentSelected()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", new JsonObject
            {
                ["success"] = true,
                ["agent"] = SmartFlowTestFactory.AgentSummary(
                    "12345678-1234-1234-1234-1234567890ab",
                    "slimfaas",
                    "2026-04-01T12:35:00+00:00")
            });

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/gnougo select slimfaas", null, agentMcp);

        Assert.True(result.Success);
        Assert.Equal("slimfaas", result.Outputs?["agent_selected"]?.GetValue<string>());
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "✅ Agent 'slimfaas' is now the active agent for this chat.");
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentSelect_PersistsDefaultAgentViaMountedAgentMcp()
    {
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

        var dbPath = AgentMcpTestPersistence.CreateIsolatedDatabasePath("agent-select");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Select(TestServerAddressResolver.NormalizeBaseAddress)
                .First();

            await SeedAgentAsync(dbPath, "slimfaas");

            var llm = new RecordingLlmClient();
            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{address}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            });
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
            var runtimeFactory = new SecureWorkflowRuntimeFactory(runtimeStore, keyVaultStore);
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            var service = new ConfigureAgentsService(
                llm,
                new FakeMcpClientFactory(),
                new MemoryCache(new MemoryCacheOptions()),
                new AgentHumanInputProvider(),
                keyVaultStore,
                runtimeFactory,
                runtimeStore,
                SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
                NullLogger<ConfigureAgentsService>.Instance,
                userConfigClient);

            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/gnougo select slimfaas", CancellationToken.None), TestContext.Current.CancellationToken);

            Assert.Contains(events, evt => evt.Type == "agent_selected" && evt.Text == "slimfaas");

            var config = await AgentMcpTestPersistence.GetUserConfigAsync(dbPath, TestContext.Current.CancellationToken);
            Assert.Equal("slimfaas", config.DefaultAgent);
            Assert.Null(config.DefaultLlmProvider);
            Assert.Null(config.DefaultLlmModel);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();

            AgentMcpTestPersistence.CleanupIsolatedWorkspace(dbPath);
        }
    }

    private static async Task SeedAgentAsync(string dbPath, string name)
    {
        await AgentMcpTestPersistence.SeedAgentAsync(
            dbPath,
            name,
            "version: 1\nname: slimfaas\nworkflows:\n  main:\n    outputs: {}");
    }

    [Fact]
    public async Task ExecuteAsync_AgentSelect_WhenAgentDoesNotExist_ReturnsNotFoundMessage()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", new JsonObject
            {
                ["success"] = false,
                ["error_code"] = "NOT_FOUND",
                ["error_message"] = "Agent 'slimfaas' not found."
            });

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/gnougo select slimfaas", null, agentMcp);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Outputs?["agent_selected"]?.GetValue<string>());
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "❌ Agent 'slimfaas' not found. Use `/gnougo list` to see available agents.");
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentEdit_WhenNameChangesAndIsAvailable_RenamesAgent()
    {
        var llm = new RecordingLlmClient();
        var updateCalls = 0;

        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (arguments, _) =>
            {
                var name = arguments?["name"]?.GetValue<string>();
                JsonObject content;

                if (string.Equals(name, "slimfaas", StringComparison.Ordinal))
                {
                    content = new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = SmartFlowTestFactory.AgentSummary(
                            "12345678-1234-1234-1234-1234567890ab",
                            "slimfaas",
                            "2026-04-01T12:35:00+00:00")
                    };
                }
                else if (string.Equals(name, "slimfaas-prod", StringComparison.Ordinal))
                {
                    content = new JsonObject
                    {
                        ["success"] = false,
                        ["error_code"] = "NOT_FOUND",
                        ["error_message"] = "Agent 'slimfaas-prod' not found."
                    };
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected name lookup: {name}");
                }

                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = content
                });
            })
            .OnTool("agent_update", (arguments, _) =>
            {
                updateCalls++;
                Assert.Equal("12345678-1234-1234-1234-1234567890ab", arguments?["id"]?.GetValue<string>());
                Assert.Equal("slimfaas-prod", arguments?["name"]?.GetValue<string>());

                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["success"] = true }
                });
            });

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId.EndsWith("edit_name", StringComparison.Ordinal)
                    ? new JsonObject { ["agent_name"] = " slimfaas-prod " }
                    : request.StepId.EndsWith("edit_choice", StringComparison.Ordinal)
                        ? new JsonObject { ["response"] = "workflow" }
                        : request.StepId.EndsWith("edit_workflow", StringComparison.Ordinal)
                            ? new JsonObject { ["yaml"] = "version: 1\nname: slimfaas-prod\nworkflows: {}" }
                            : request.StepId.EndsWith("confirm_edit", StringComparison.Ordinal)
                                ? new JsonObject { ["response"] = "save" }
                                : throw new InvalidOperationException($"Unexpected step id: {request.StepId}");

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId.EndsWith("confirm_edit", StringComparison.Ordinal))
                    break;
            }
        }, token);

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/gnougo edit slimfaas", humanInput, agentMcp);
        await responder;

        Assert.True(result.Success);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(1, updateCalls);
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "✅ Agent 'slimfaas-prod' updated.");
    }

    [Fact]
    public async Task ExecuteAsync_AgentEdit_WhenRenamedNameAlreadyExists_StopsBeforeSave()
    {
        var llm = new RecordingLlmClient();
        var updateCalls = 0;
        string? unexpectedStepId = null;

        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (arguments, _) =>
            {
                var name = arguments?["name"]?.GetValue<string>();
                JsonObject content;

                if (string.Equals(name, "slimfaas", StringComparison.Ordinal))
                {
                    content = new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = SmartFlowTestFactory.AgentSummary(
                            "12345678-1234-1234-1234-1234567890ab",
                            "slimfaas",
                            "2026-04-01T12:35:00+00:00")
                    };
                }
                else if (string.Equals(name, "dailyreporter", StringComparison.Ordinal))
                {
                    content = new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = SmartFlowTestFactory.AgentSummary(
                            "87654321-4321-4321-4321-ba0987654321",
                            "DailyReporter",
                            "2026-04-01T12:40:00+00:00")
                    };
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected name lookup: {name}");
                }

                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = content
                });
            })
            .OnTool("agent_update", (_, _) =>
            {
                updateCalls++;
                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["success"] = true }
                });
            });

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                if (request.StepId.EndsWith("edit_name", StringComparison.Ordinal))
                {
                    humanInput.TrySubmitResponse(request.RunId, request.StepId, new JsonObject { ["agent_name"] = " dailyreporter " });
                    continue;
                }

                unexpectedStepId = request.StepId;
                break;
            }
        }, token);

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/gnougo edit slimfaas", humanInput, agentMcp);
        try
        {
            await responder;
        }
        catch (OperationCanceledException)
        {
            // The workflow already completed; the background request reader can still be awaiting more items.
        }

        Assert.True(result.Success);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(0, updateCalls);
        Assert.Null(unexpectedStepId);
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "❌ Agent 'DailyReporter' already exists. Use `/gnougo edit DailyReporter` to update it or choose another name.");
    }

    [Fact]
    public async Task ExecuteAsync_AgentList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_list", SmartFlowTestFactory.AgentListResult(
                SmartFlowTestFactory.AgentSummary(
                    "12345678-1234-1234-1234-1234567890ab",
                    "daily-reporter",
                    "2026-04-01T12:30:00+00:00"),
                SmartFlowTestFactory.AgentSummary(
                    "87654321-4321-4321-4321-ba0987654321",
                    "reviewer",
                    "2026-04-01T12:35:00+00:00")));

        var service = SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory(agentMcp));

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/GnOuGo list", CancellationToken.None), TestContext.Current.CancellationToken);

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured Agents", answer.Text);
        Assert.Contains("| daily-reporter | `12345678` |", answer.Text);
        Assert.Contains("| reviewer | `87654321` |", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    private static async Task<(RunResult Result, List<SmartFlowEvent> Events)> ExecuteConfigureAgentsWorkflowAsync(
        RecordingLlmClient llm,
        string command,
        AgentHumanInputProvider? humanInput = null,
        params IMcpSession[] sessions)
        => await ExecuteConfigureAgentsWorkflowByNameAsync(
            llm,
            workflowName: "main",
            new JsonObject { ["command"] = command },
            humanInput,
            sessions);

    private static JsonObject BuildRecommendedIntentClarificationResponse(HumanInputRequest request)
    {
        Assert.True(request.AllowAbandon);
        var fields = Assert.IsAssignableFrom<IReadOnlyCollection<HumanInputFieldDef>>(request.Fields);
        Assert.NotEmpty(fields);

        var response = new JsonObject
        {
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
        };
        foreach (var field in fields)
        {
            var recommended = Assert.Single(
                Assert.IsAssignableFrom<IReadOnlyCollection<HumanInputOptionDef>>(field.OptionDefinitions),
                static option => option.Recommended);
            Assert.Equal(recommended.Value, field.Default);
            response[field.Name] = recommended.Value;
        }

        return response;
    }

    private static ConfigureAgentsService CreateConfigureAgentsServiceForStreaming(
        RecordingLlmClient llm,
        AgentHumanInputProvider humanInput,
        params IMcpSession[] sessions)
    {
        var options = new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini"
        };
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret(
                KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, "openai"),
                """
                {"provider":"openai","model":"gpt-4o-mini"}
                """);
        var mcpFactory = new FakeMcpClientFactory(sessions);
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            runtimeStore,
            keyVaultStore,
            llmClientOverride: llm,
            mcpClientFactoryOverride: mcpFactory);

        return new ConfigureAgentsService(
            llm,
            mcpFactory,
            new MemoryCache(new MemoryCacheOptions()),
            humanInput,
            keyVaultStore,
            runtimeFactory,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureAgentsService>.Instance,
            exchangeRateProvider: new TestExchangeRateProvider());
    }

    private static async Task<(RunResult Result, List<SmartFlowEvent> Events)> ExecuteConfigureAgentsWorkflowByNameAsync(
        RecordingLlmClient llm,
        string workflowName,
        JsonObject inputs,
        AgentHumanInputProvider? humanInput = null,
        params IMcpSession[] sessions)
    {
        var service = SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory(sessions));
        var workflowYaml = (string)(typeof(ConfigureAgentsService)
            .GetField("_workflowYaml", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(service)
            ?? throw new InvalidOperationException("Could not read configure-agents workflow YAML."));

        var doc = WorkflowParser.Parse(workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        var workflow = compiled.Workflows[workflowName];

        var events = new List<SmartFlowEvent>();
        var effectiveHumanInput = humanInput ?? new AgentHumanInputProvider();
        var engine = new WorkflowEngine
        {
            LLMClient = llm,
            ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(),
            ExchangeRateProvider = new TestExchangeRateProvider(),
            LlmDefaults = new LlmRuntimeDefaults
            {
                Provider = "openai",
                Model = "gpt-4o-mini"
            },
            McpClientFactory = new FakeMcpClientFactory(sessions),
            McpCache = new MemoryCache(new MemoryCacheOptions()),
            HumanInputProvider = effectiveHumanInput,
            Telemetry = new AgentStreamingTelemetry(events.Add),
            Logger = NullLogger<ConfigureAgentsService>.Instance,
            Limits = new ExecutionLimits { LogStepContent = true }
        };

        if (string.Equals(workflowName, "agent_add", StringComparison.Ordinal))
        {
            inputs.TryAdd("planning_budget_amount", 50m);
            inputs.TryAdd("planning_budget_currency", "EUR");
        }

        var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

        var result = await engine.ExecuteAsync(workflow, resolvedInputs, CancellationToken.None);
        return (result, events);
    }
}
