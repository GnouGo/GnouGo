using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureAgentsServiceTests
{
    [Fact]
    public async Task ExecuteAsync_AgentAdd_WhenCreationSucceeds_SelectsCreatedAgentByDefault()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (arguments, _) =>
            {
                Assert.Equal("slimfaas", arguments?["name"]?.GetValue<string>());
                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject
                    {
                        ["success"] = false,
                        ["error_code"] = "NOT_FOUND",
                        ["error_message"] = "Agent 'slimfaas' not found."
                    }
                });
            })
            .OnTool("agent_add", new JsonObject
            {
                ["success"] = true,
                ["agent"] = SmartFlowTestFactory.AgentSummary(
                    "12345678-1234-1234-1234-1234567890ab",
                    "slimfaas",
                    "2026-04-01T12:35:00+00:00")
            });

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId.EndsWith("input_name", StringComparison.Ordinal)
                    ? new JsonObject { ["agent_name"] = " slimfaas " }
                    : request.StepId.EndsWith("input_prompt", StringComparison.Ordinal)
                        ? new JsonObject { ["description"] = "Explain SlimFaas" }
                        : request.StepId.EndsWith("review_workflow", StringComparison.Ordinal)
                            ? new JsonObject { ["response"] = "approve" }
                                : throw new InvalidOperationException($"Unexpected step id: {request.StepId}");

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId.EndsWith("review_workflow", StringComparison.Ordinal))
                    break;
            }
        }, token);

        var (result, events) = await ExecuteConfigureAgentsWorkflowByNameAsync(llm, "agent_add", new JsonObject(), humanInput, agentMcp);
        try
        {
            await responder;
        }
        catch (OperationCanceledException)
        {
            // The workflow already completed; the background request reader can still be awaiting more items.
        }

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("slimfaas", result.Outputs?["agent_name"]?.GetValue<string>());
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "✅ Agent 'slimfaas' created successfully and is now the active agent for this chat.");
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WhenNameAlreadyExistsAfterNameEntry_EmitsConflictMessageAndStopsEarly()
    {
        var llm = new RecordingLlmClient();
        var agentAddCalls = 0;
        string? unexpectedStepId = null;

        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (arguments, _) =>
            {
                Assert.Equal("dailyreporter", arguments?["name"]?.GetValue<string>());
                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject
                    {
                        ["success"] = true,
                        ["agent"] = SmartFlowTestFactory.AgentSummary(
                            "12345678-1234-1234-1234-1234567890ab",
                            "DailyReporter",
                            "2026-04-01T12:35:00+00:00")
                    }
                });
            })
            .OnTool("agent_add", (_, _) =>
            {
                agentAddCalls++;
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
                if (request.StepId.EndsWith("input_name", StringComparison.Ordinal))
                {
                    humanInput.TrySubmitResponse(request.RunId, request.StepId, new JsonObject { ["agent_name"] = " dailyreporter " });
                    break;
                }

                unexpectedStepId = request.StepId;
                break;
            }
        }, token);

        var (result, events) = await ExecuteConfigureAgentsWorkflowByNameAsync(llm, "agent_add", new JsonObject(), humanInput, agentMcp);
        await responder;

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(string.Empty, result.Outputs?["agent_name"]?.GetValue<string>());
        Assert.Null(unexpectedStepId);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(0, agentAddCalls);
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "❌ Agent 'DailyReporter' already exists. Use `/agent edit DailyReporter` to update it or choose another name.");
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WhenSaveReportsNameAlreadyExists_EmitsConflictMessage()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", new JsonObject
            {
                ["success"] = false,
                ["error_code"] = "NOT_FOUND",
                ["error_message"] = "Agent 'slimfaas' not found."
            })
            .OnTool("agent_add", new JsonObject
            {
                ["success"] = false,
                ["error_code"] = "ALREADY_EXISTS",
                ["error_message"] = "An agent named 'slimfaas' already exists."
            });

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId.EndsWith("input_name", StringComparison.Ordinal)
                    ? new JsonObject { ["agent_name"] = "slimfaas" }
                    : request.StepId.EndsWith("input_prompt", StringComparison.Ordinal)
                        ? new JsonObject { ["description"] = "Explain SlimFaas" }
                        : request.StepId.EndsWith("review_workflow", StringComparison.Ordinal)
                            ? new JsonObject { ["response"] = "approve" }
                                : throw new InvalidOperationException($"Unexpected step id: {request.StepId}");

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId.EndsWith("review_workflow", StringComparison.Ordinal))
                    break;
            }
        }, token);

        var (result, events) = await ExecuteConfigureAgentsWorkflowByNameAsync(llm, "agent_add", new JsonObject(), humanInput, agentMcp);
        try
        {
            await responder;
        }
        catch (OperationCanceledException)
        {
            // The workflow already completed; the background request reader can still be awaiting more items.
        }

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(string.Empty, result.Outputs?["agent_name"]?.GetValue<string>());
        Assert.True(llm.CallCount > 0);
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "❌ Failed to create agent 'slimfaas'. An agent named 'slimfaas' already exists.");
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

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/agent select slimfaas", null, agentMcp);

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

        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-select-{Guid.NewGuid():N}.db");
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

            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent select slimfaas", CancellationToken.None));

            Assert.Contains(events, evt => evt.Type == "agent_selected" && evt.Text == "slimfaas");

            var config = await AgentMcpTestPersistence.GetUserConfigAsync(dbPath);
            Assert.Equal("slimfaas", config.DefaultAgent);
            Assert.Null(config.DefaultLlmProvider);
            Assert.Null(config.DefaultLlmModel);
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

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/agent select slimfaas", null, agentMcp);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Outputs?["agent_selected"]?.GetValue<string>());
        Assert.Contains(events, evt =>
            evt.Type == "thinking:response" &&
            evt.Text == "❌ Agent 'slimfaas' not found. Use `/agent list` to see available agents.");
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

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/agent edit slimfaas", humanInput, agentMcp);
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

        var (result, events) = await ExecuteConfigureAgentsWorkflowAsync(llm, "/agent edit slimfaas", humanInput, agentMcp);
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
            evt.Text == "❌ Agent 'DailyReporter' already exists. Use `/agent edit DailyReporter` to update it or choose another name.");
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

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured Agents", answer.Text);
        Assert.Contains("| daily-reporter | `12345678` |", answer.Text);
        Assert.Contains("| reviewer | `87654321` |", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WithoutConfiguredProvider_ReturnsGuidanceWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_list_secrets", SmartFlowTestFactory.KeyVaultListSecretsResult(
                ("gnougo_mcp_github", 1, "2026-04-01T12:00:00+00:00")));

        var service = SmartFlowTestFactory.CreateAgentsService(
            llm,
            new FakeMcpClientFactory(keyVault),
            new LLMOptions
            {
                DefaultProvider = "OpenAi",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>()
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent add", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Equal("❌ Configure a default LLM provider first. Use `/llm add` to create one, then `/llm default` before retrying `/agent add`.", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WithoutConfiguredDefaultProvider_ReturnsGuidanceWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--ollama", "{\"provider\":\"ollama\",\"url\":\"http://localhost:11434\",\"model\":\"llama3\",\"authType\":\"none\"}");

        var service = SmartFlowTestFactory.CreateAgentsService(
            llm,
            new FakeMcpClientFactory(new FakeMcpSession("GnOuGo.Agent.Mcp")),
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
                }
            },
            keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent add", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Equal("❌ Configure a default LLM provider first. Use `/llm add` to create one, then `/llm default` before retrying `/agent add`.", answer.Text);
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

        var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

        var result = await engine.ExecuteAsync(workflow, resolvedInputs, CancellationToken.None);
        return (result, events);
    }
}
