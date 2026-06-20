using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
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
    public async Task ExecuteAsync_AfterCompletion_ExportsAssociatedTrace()
    {
        var llm = new RecordingLlmClient();
        var exporter = new RecordingWorkflowTraceFileExporter();
        var service = SmartFlowTestFactory.CreateSmartFlowService(
            llm,
            new FakeMcpClientFactory(),
            SmartFlowTestFactory.CreateProvidersService(llm),
            SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory()),
            traceFileExporter: exporter);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/help", correlationId: "corr-export", agentName: null, CancellationToken.None));

        var started = Assert.Single(events, evt => evt.Type == "trace.started");
        var exported = Assert.Single(exporter.Exports);
        Assert.Equal(started.TraceId, Assert.Single(exporter.StartedTraceIds));
        Assert.Equal(started.TraceId, exported.TraceId);
        Assert.Equal("corr-export", exported.CorrelationId);
    }

    private sealed class RecordingWorkflowTraceFileExporter : IWorkflowTraceFileExporter
    {
        public List<string> StartedTraceIds { get; } = [];
        public List<(string TraceId, string CorrelationId)> Exports { get; } = [];

        public void BeginCapture(string traceId)
            => StartedTraceIds.Add(traceId);

        public Task ExportAsync(string traceId, string correlationId, CancellationToken cancellationToken)
        {
            Exports.Add((traceId, correlationId));
            return Task.CompletedTask;
        }
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
    public async Task ExecuteAsync_WorkflowRepair_KeepsPrefilteredPromptButDryRunCanUseAllConfiguredServers()
    {
        const string agentId = "8d8871b7-01cf-4a42-a95a-391d633d7d37";
        const string agentName = "document-agent";
        var brokenWorkflow = """
            version: 1
            name: document-agent
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
            name: document-agent
            skill:
              description: Repaired document workflow.
              tags: [document]
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
                  - id: create_doc
                    type: mcp.call
                    input:
                      server: GnOuGo.Document.Mcp
                      method: document_create
                      request:
                        title: "${data.inputs.task}"
                  - id: final_answer
                    type: set
                    input:
                      answer: "${data.steps.create_doc.response.path}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;

        string? persistedWorkflow = null;
        var agentMcp = BuildAgentMcpForRepair(agentId, agentName, brokenWorkflow, value => persistedWorkflow = value);
        var githubMcp = new FakeMcpSession("Github")
            .WithTool("github_issue_search", "Search Github issues.");
        var documentMcp = new FakeMcpSession("GnOuGo.Document.Mcp")
            .WithTool(
                "document_create",
                "Create a document and return its path.",
                JsonNode.Parse("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}"""),
                JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
                JsonNode.Parse("""{"path":"exports/generated.md"}"""));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = AutoApproveRepairAsync(humanInput, cts.Token);

        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "test",
            DefaultModel = "repair-model"
        });
        var llm = new PrefilterTrapWorkflowPlanLlmClient(repairedWorkflow);
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            runtimeStore,
            new FakeKeyVaultRuntimeConfigStore(),
            llmClientOverride: llm,
            mcpClientFactoryOverride: new FakeMcpClientFactory(agentMcp, githubMcp, documentMcp));

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
            smartFlow.ExecuteAsync("create a document from this issue", correlationId: "corr-repair-prefilter", agentName: agentName, CancellationToken.None));

        await responder;

        Assert.Equal(repairedWorkflow, persistedWorkflow);
        Assert.True(llm.PrefilterCallCount > 0);
        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text?.Contains("improved and saved", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(events, evt => evt.Type == "error");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistedAgentHandlesMcpError_OffersWorkflowRepair()
    {
        const string agentId = "8d8871b7-01cf-4a42-a95a-391d633d7d37";
        const string agentName = "git-agent";
        var handledErrorWorkflow = """
            version: 1
            name: git-agent
            workflows:
              main:
                inputs:
                  task:
                    type: string
                    required: true
                steps:
                  - id: clone_repo
                    type: mcp.call
                    input:
                      server: GnOuGo.Git.Mcp
                      method: git_clone
                      request:
                        remoteUrl: https://github.com/AxaFrance/oidc-client
                        targetDirectory: repos/AxaFrance-oidc-client-issue-1679
                      timeout_ms: 1200000
                    on_error:
                      cases:
                        - if: '${error.code == "MCP_CALL_ERROR"}'
                          action: continue
                          set_output:
                            status: handled
                            message: "${error.message}"
                            mcp_message: "${error.details.mcp_error_message}"
                  - id: final_answer
                    type: set
                    input:
                      answer: "${'Clone issue handled: ' + data.steps.clone_repo.mcp_message}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;
        var repairedWorkflow = """
            version: 1
            name: git-agent
            skill:
              description: Repaired git agent.
              tags: [git]
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
                      answer: "${'REPAIRED GIT: ' + data.inputs.task}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;

        string? persistedWorkflow = null;
        var agentMcp = BuildAgentMcpForRepair(agentId, agentName, handledErrorWorkflow, value => persistedWorkflow = value);
        var gitMcp = new FakeMcpSession("GnOuGo.Git.Mcp")
            .WithTool(
                "git_clone",
                "Clone a git repository into a workspace directory.",
                JsonNode.Parse("""{"type":"object","properties":{"remoteUrl":{"type":"string"},"targetDirectory":{"type":"string"}},"required":["remoteUrl","targetDirectory"]}"""))
            .OnTool("git_clone", (_, _) => Task.FromResult(new McpCallResult
            {
                IsError = true,
                Content = new JsonObject
                {
                    ["error_code"] = "TARGET_EXISTS",
                    ["error_message"] = "Clone target directory '/Users/a115vc/Desktop/GnOuGo/repos/AxaFrance-oidc-client-issue-1679' already exists and is not empty."
                }
            }));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = AutoApproveRepairAsync(humanInput, cts.Token);

        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "test",
            DefaultModel = "repair-model"
        });
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            runtimeStore,
            new FakeKeyVaultRuntimeConfigStore(),
            llmClientOverride: new FixedWorkflowPlanLlmClient(repairedWorkflow),
            mcpClientFactoryOverride: new FakeMcpClientFactory(agentMcp, gitMcp));

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
            smartFlow.ExecuteAsync("clone and fix issue 1679", correlationId: "corr-handled-mcp-repair", agentName: agentName, CancellationToken.None));

        await responder;

        Assert.Equal(repairedWorkflow, persistedWorkflow);
        Assert.Contains(events, evt =>
            evt.Type == "human_input_request" &&
            evt.Text?.Contains("handled an MCP error", StringComparison.OrdinalIgnoreCase) == true &&
            evt.Text.Contains("TARGET_EXISTS", StringComparison.Ordinal));
        Assert.Contains(events, evt =>
            evt.Type == "answer" &&
            evt.Text?.Contains("handled an MCP error", StringComparison.OrdinalIgnoreCase) == true &&
            evt.Text.Contains("improved and saved", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(events, evt => evt.Type == "error");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRoutedPersistedAgentHandlesMcpError_OffersWorkflowRepair()
    {
        const string agentId = "8d8871b7-01cf-4a42-a95a-391d633d7d37";
        const string agentName = "git-agent";
        var handledErrorWorkflow = """
            version: 1
            name: git-agent
            skill:
              description: Works with git repositories.
              tags: [git]
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
                  - id: clone_repo
                    type: mcp.call
                    input:
                      server: GnOuGo.Git.Mcp
                      method: git_clone
                      request:
                        remoteUrl: https://github.com/AxaFrance/oidc-client
                        targetDirectory: repos/AxaFrance-oidc-client-issue-1679
                      timeout_ms: 1200000
                    on_error:
                      cases:
                        - if: '${error.code == "MCP_CALL_ERROR"}'
                          action: continue
                          set_output:
                            status: handled
                            message: "${error.message}"
                            mcp_message: "${error.details.mcp_error_message}"
                  - id: final_answer
                    type: set
                    input:
                      answer: "${'Clone issue handled: ' + data.steps.clone_repo.mcp_message}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;
        var repairedWorkflow = """
            version: 1
            name: git-agent
            skill:
              description: Repaired routed git agent.
              tags: [git]
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
                      answer: "${'REPAIRED ROUTED GIT: ' + data.inputs.task}"
                outputs:
                  answer:
                    expr: "${data.steps.final_answer.answer}"
                    type: string
            """;

        string? persistedWorkflow = null;
        var agentMcp = BuildAgentMcpForRepair(agentId, agentName, handledErrorWorkflow, value => persistedWorkflow = value);
        var gitMcp = new FakeMcpSession("GnOuGo.Git.Mcp")
            .WithTool(
                "git_clone",
                "Clone a git repository into a workspace directory.",
                JsonNode.Parse("""{"type":"object","properties":{"remoteUrl":{"type":"string"},"targetDirectory":{"type":"string"}},"required":["remoteUrl","targetDirectory"]}"""))
            .OnTool("git_clone", (_, _) => Task.FromResult(new McpCallResult
            {
                IsError = true,
                Content = new JsonObject
                {
                    ["error_code"] = "TARGET_EXISTS",
                    ["error_message"] = "Clone target directory '/Users/a115vc/Desktop/GnOuGo/repos/AxaFrance-oidc-client-issue-1679' already exists and is not empty."
                }
            }));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = AutoApproveRepairAsync(humanInput, cts.Token);

        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "test",
            DefaultModel = "repair-model"
        });
        var llm = new RoutedWorkflowRepairLlmClient(agentName, repairedWorkflow);
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            runtimeStore,
            new FakeKeyVaultRuntimeConfigStore(),
            llmClientOverride: llm,
            mcpClientFactoryOverride: new FakeMcpClientFactory(agentMcp, gitMcp));
        using var services = new ServiceCollection()
            .AddSingleton<IAgentRepository>(new SmartFlowFakeAgentRepository(new AgentDefinition
            {
                Id = Guid.Parse(agentId),
                Name = agentName,
                Workflow = handledErrorWorkflow,
                OriginalPrompt = "work with git repositories",
                CreatedAt = DateTimeOffset.Parse("2026-06-15T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-06-15T00:00:00Z")
            }))
            .BuildServiceProvider();

        var smartFlow = new SmartFlowService(
            new RecordingLlmClient(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            runtimeFactory,
            SmartFlowTestFactory.CreateProvidersService(new RecordingLlmClient()),
            SmartFlowTestFactory.CreateAgentsService(new RecordingLlmClient(), new FakeMcpClientFactory()),
            humanInput,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<SmartFlowService>.Instance,
            candidateProvider: new SingleWorkflowCandidateProvider(new WorkflowRouteCandidate
            {
                Id = $"database:{agentName}",
                Name = agentName,
                Ref = new JsonObject
                {
                    ["kind"] = "database",
                    ["agent"] = agentName
                },
                Description = "Works with git repositories.",
                Tags = ["git"]
            }),
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());

        var events = await SmartFlowTestFactory.CollectAsync(
            smartFlow.ExecuteAsync("clone and fix issue 1679", correlationId: "corr-routed-handled-mcp-repair", agentName: null, CancellationToken.None));

        await responder;

        Assert.Equal(repairedWorkflow, persistedWorkflow);
        Assert.Contains(events, evt =>
            evt.Type == "human_input_request" &&
            evt.Text?.Contains("handled an MCP error", StringComparison.OrdinalIgnoreCase) == true &&
            evt.Text.Contains("TARGET_EXISTS", StringComparison.Ordinal));
        Assert.Contains(events, evt =>
            evt.Type == "answer" &&
            evt.Text?.Contains("handled an MCP error", StringComparison.OrdinalIgnoreCase) == true &&
            evt.Text.Contains("improved and saved", StringComparison.OrdinalIgnoreCase));
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

    private static FakeMcpSession BuildAgentMcpForRepair(
        string agentId,
        string agentName,
        string brokenWorkflow,
        Action<string?> captureUpdatedWorkflow)
        => new FakeMcpSession(AgentMcpHostingExtensions.ServerName)
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
                                ["original_prompt"] = "repair test prompts",
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
                var workflow = arguments?["workflow"]?.GetValue<string>();
                captureUpdatedWorkflow(workflow);
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
                            ["workflow"] = workflow,
                            ["original_prompt"] = arguments?["originalPrompt"]?.GetValue<string>() ?? "",
                            ["created_at"] = "2026-06-15T00:00:00Z",
                            ["updated_at"] = "2026-06-15T00:01:00Z"
                        }
                    }
                });
            });

    private static Task AutoApproveRepairAsync(AgentHumanInputProvider humanInput, CancellationToken ct)
        => Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(ct))
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
        }, ct);

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

    private sealed class PrefilterTrapWorkflowPlanLlmClient : ILLMClient
    {
        private readonly string _workflowYaml;

        public PrefilterTrapWorkflowPlanLlmClient(string workflowYaml)
        {
            _workflowYaml = workflowYaml;
        }

        public int PrefilterCallCount { get; private set; }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.OrdinalIgnoreCase))
            {
                PrefilterCallCount++;
                return Task.FromResult(new LLMResponse
                {
                    Text = """{"servers":[{"name":"Github","reason":"The failure mentions an issue."}]}""",
                    Json = JsonNode.Parse("""{"servers":[{"name":"Github","reason":"The failure mentions an issue."}]}""")
                });
            }

            if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
            {
                PrefilterCallCount++;
                return Task.FromResult(new LLMResponse
                {
                    Text = """{"servers":[{"name":"Github","tools":[],"prompts":[]}]}""",
                    Json = JsonNode.Parse("""{"servers":[{"name":"Github","tools":[],"prompts":[]}]}""")
                });
            }

            return Task.FromResult(new LLMResponse { Text = _workflowYaml });
        }
    }

    private sealed class RoutedWorkflowRepairLlmClient : ILLMClient
    {
        private readonly string _agentName;
        private readonly string _workflowYaml;

        public RoutedWorkflowRepairLlmClient(string agentName, string workflowYaml)
        {
            _agentName = agentName;
            _workflowYaml = workflowYaml;
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.Prompt.Contains("You are a workflow router", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonNode.Parse($$"""
                    {
                      "selected": [
                        {
                          "id": "database:{{_agentName}}",
                          "reason": "The prompt asks for git repository work.",
                          "confidence": 1.0
                        }
                      ]
                    }
                    """);
                return Task.FromResult(new LLMResponse
                {
                    Text = json!.ToJsonString(),
                    Json = json
                });
            }

            if (request.Prompt.Contains("You extract workflow input arguments", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonNode.Parse("""
                    {
                      "arguments": {
                        "task": "clone and fix issue 1679"
                      }
                    }
                    """);
                return Task.FromResult(new LLMResponse
                {
                    Text = json!.ToJsonString(),
                    Json = json
                });
            }

            if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonNode.Parse("""{"servers":[{"name":"GnOuGo.Git.Mcp","reason":"The failing step uses git_clone."}]}""");
                return Task.FromResult(new LLMResponse
                {
                    Text = json!.ToJsonString(),
                    Json = json
                });
            }

            if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonNode.Parse("""{"servers":[{"name":"GnOuGo.Git.Mcp","tools":["git_clone"],"prompts":[]}]}""");
                return Task.FromResult(new LLMResponse
                {
                    Text = json!.ToJsonString(),
                    Json = json
                });
            }

            return Task.FromResult(new LLMResponse { Text = _workflowYaml });
        }
    }

    private sealed class SingleWorkflowCandidateProvider : IWorkflowCandidateProvider
    {
        private readonly WorkflowRouteCandidate _candidate;

        public SingleWorkflowCandidateProvider(WorkflowRouteCandidate candidate)
        {
            _candidate = candidate;
        }

        public Task<IReadOnlyList<WorkflowRouteCandidate>> GetCandidatesAsync(
            WorkflowRouteCandidateQuery query,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkflowRouteCandidate>>([_candidate]);
    }

    private sealed class SmartFlowFakeAgentRepository : IAgentRepository
    {
        private readonly List<AgentDefinition> _agents;

        public SmartFlowFakeAgentRepository(params AgentDefinition[] agents)
        {
            _agents = agents.ToList();
        }

        public Task<AgentDefinition> AddAgentAsync(string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
            => Task.FromResult(_agents.ToList());

        public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_agents.FirstOrDefault(agent => string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
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
