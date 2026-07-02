using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Shared;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Event emitted during a SmartFlow workflow execution for streaming to the UI.
/// </summary>
public sealed record SmartFlowEvent(
    string Type,
    string? Text,
    string? CorrelationId = null,
    string? TraceId = null,
    string? ConversationId = null)
{
    public static SmartFlowEvent TraceStarted(string correlationId, string traceId)
        => new("trace.started", null, correlationId, traceId);

    public static SmartFlowEvent ConversationReady(string conversationId)
        => new("conversation", conversationId, ConversationId: conversationId);

    public SmartFlowEvent WithCorrelation(string correlationId)
        => string.IsNullOrWhiteSpace(CorrelationId) ? this with { CorrelationId = correlationId } : this;
}

/// <summary>
/// Wraps the GnOuGo.Flow workflow engine to execute either the persisted MCP-selected
/// agent workflow or the embedded dynamic-workflow-agent when no agent is selected.
/// </summary>
public sealed class SmartFlowService
{
    private readonly ILLMClient _llm;
    private readonly IMemoryCache _mcpCache;
    private readonly SecureWorkflowRuntimeFactory _runtimeFactory;
    private readonly ConfigureProvidersService _configureProviders;
    private readonly ConfigureAgentsService _configureAgents;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly AgentUserConfigMcpClient? _userConfigClient;
    private readonly IWorkflowCandidateProvider? _candidateProvider;
    private readonly InMemoryChatHistoryStore? _historyStore;

    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<SmartFlowService> _logger;
    private readonly IWorkflowTraceFileExporter? _traceFileExporter;
    private readonly string _routingWorkflowYaml;
    private readonly TimeSpan _mcpCacheSlidingExpiration;

    /// <summary>Slash commands that route to the configure-providers workflow.</summary>
    private static readonly string[] ProviderCommands = { "/llm", "/embedding", "/mcp", "/status" };

    [ActivatorUtilitiesConstructor]
    public SmartFlowService(
        ILLMClient llm,
        IMemoryCache mcpCache,
        SecureWorkflowRuntimeFactory runtimeFactory,
        ConfigureProvidersService configureProviders,
        ConfigureAgentsService configureAgents,
        AgentHumanInputProvider humanInput,
        AgentOTelTelemetry otel,
        ILogger<SmartFlowService> logger,
        AgentUserConfigMcpClient? userConfigClient = null,
        IWorkflowCandidateProvider? candidateProvider = null,
        InMemoryChatHistoryStore? historyStore = null,
        IServiceScopeFactory? scopeFactory = null,
        IWorkflowTraceFileExporter? traceFileExporter = null,
        IOptions<McpCapabilityCacheSettings>? mcpCapabilityCacheSettings = null)
    {
        _llm = llm;
        _mcpCache = mcpCache;
        _runtimeFactory = runtimeFactory;
        _configureProviders = configureProviders;
        _configureAgents = configureAgents;
        _humanInput = humanInput;
        _userConfigClient = userConfigClient;
        _candidateProvider = candidateProvider;
        _historyStore = historyStore;
        _scopeFactory = scopeFactory;
        _traceFileExporter = traceFileExporter;
        _otel = otel;
        _logger = logger;
        _mcpCacheSlidingExpiration = (mcpCapabilityCacheSettings?.Value ?? new McpCapabilityCacheSettings()).SlidingExpiration;

        _routingWorkflowYaml = LoadEmbeddedWorkflowYaml("main-routing-agent.yaml");
    }


    public SmartFlowService(
        ILLMClient llm,
        IMemoryCache mcpCache,
        SecureWorkflowRuntimeFactory runtimeFactory,
        ConfigureProvidersService configureProviders,
        ConfigureAgentsService configureAgents,
        AgentHumanInputProvider humanInput,
        AgentOTelTelemetry otel,
        ILogger<SmartFlowService> logger,
        AgentUserConfigMcpClient? userConfigClient)
        : this(
            llm,
            mcpCache,
            runtimeFactory,
            configureProviders,
            configureAgents,
            humanInput,
            otel,
            logger,
            userConfigClient,
            candidateProvider: null,
            historyStore: null,
            scopeFactory: null)
    {
    }
    /// <summary>
    /// Executes the resolved workflow for the given user task and streams events.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteAsync(task, correlationId: null, agentName: null, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        string? agentName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteAsync(task, correlationId: null, agentName, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        string? correlationId,
        string? agentName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteAsync(task, correlationId, agentName, filesIds: null, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        string? correlationId,
        string? agentName,
        IReadOnlyList<string>? filesIds,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteAsync(task, correlationId, agentName, filesIds, workflowInputs: null, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        string? correlationId,
        string? agentName,
        IReadOnlyList<string>? filesIds,
        JsonObject? workflowInputs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteAsync(task, correlationId, agentName, filesIds, workflowInputs, conversationId: null, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        string? correlationId,
        string? agentName,
        IReadOnlyList<string>? filesIds,
        JsonObject? workflowInputs,
        string? conversationId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? ActivityTraceId.CreateRandom().ToHexString()
            : correlationId.Trim();
        var effectiveConversationId = string.IsNullOrWhiteSpace(conversationId)
            ? Guid.NewGuid().ToString("N")
            : conversationId.Trim();

        var messageTrace = _otel.StartChatMessageActivity(effectiveCorrelationId, task);
        try
        {
            _traceFileExporter?.BeginCapture(messageTrace.TraceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize workflow trace file capture.");
        }

        var executionCompleted = false;
        try
        {
            yield return SmartFlowEvent.TraceStarted(effectiveCorrelationId, messageTrace.TraceId);
            yield return SmartFlowEvent.ConversationReady(effectiveConversationId).WithCorrelation(effectiveCorrelationId);

            var hasError = false;
            var finalAnswer = "";
            var history = LoadConversationHistory(effectiveConversationId, topK: 40);
            var mergedWorkflowInputs = MergeWorkflowInputsWithConversation(workflowInputs, effectiveConversationId, history);

            await foreach (var evt in ExecuteCoreAsync(task, effectiveCorrelationId, agentName, filesIds, mergedWorkflowInputs, messageTrace.Activity, ct))
            {
                hasError |= string.Equals(evt.Type, "error", StringComparison.OrdinalIgnoreCase);
                if (evt.Type is "answer")
                    finalAnswer = evt.Text ?? "";
                yield return evt.WithCorrelation(effectiveCorrelationId) with { ConversationId = effectiveConversationId };
            }

            if (!hasError && !string.IsNullOrWhiteSpace(finalAnswer))
                AppendConversationTurn(effectiveConversationId, task, finalAnswer, effectiveCorrelationId);

            messageTrace.SetStatus(hasError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            executionCompleted = true;
        }
        finally
        {
            if (!executionCompleted)
                messageTrace.SetStatus(ActivityStatusCode.Error, "Workflow execution did not complete.");

            var traceId = messageTrace.TraceId;
            messageTrace.Dispose();

            if (_traceFileExporter is not null)
            {
                try
                {
                    await _traceFileExporter.ExportAsync(
                        traceId,
                        effectiveCorrelationId,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not export workflow trace {TraceId} to a file.", traceId);
                }
            }
        }
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteCoreAsync(
        string task,
        string correlationId,
        string? requestedAgentName,
        IReadOnlyList<string>? filesIds,
        JsonObject? workflowInputs,
        Activity? parentActivity,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var previousActivity = Activity.Current;
        if (parentActivity is not null)
            Activity.Current = parentActivity;

        try
        {
            // ── Slash command routing ──
            var trimmed = task.Trim();

            if (IsCommand(trimmed, "/help"))
            {
                yield return new SmartFlowEvent("answer", RenderHelp());
                yield break;
            }

            // Route /gnougo commands to ConfigureAgentsService
            if (IsCommand(trimmed, "/gnougo"))
            {
                await foreach (var evt in _configureAgents.ExecuteAsync(trimmed, ct))
                {
                    yield return evt;
                }
                yield break;
            }

            // Route /llm, /mcp, /status commands to ConfigureProvidersService (with full command including sub-commands)
            foreach (var cmd in ProviderCommands)
            {
                if (IsCommand(trimmed, cmd))
                {
                    await foreach (var evt in _configureProviders.ExecuteAsync(trimmed, ct))
                    {
                        yield return evt;
                    }
                    yield break;
                }
            }

            // Channel for streaming telemetry events
            var channel = Channel.CreateUnbounded<SmartFlowEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Create a telemetry decorator that captures emit steps
            var telemetry = new CompositeWorkflowTelemetry(
                new AgentStreamingTelemetry(evt => channel.Writer.TryWrite(evt)),
                _otel);

            await using var runtime = await _runtimeFactory.CreateAsync(ct);
            var engine = new WorkflowEngine
            {
                LLMClient = runtime.LlmClient,
                LLMCapabilities = runtime.LlmCapabilityResolver,
                LlmDefaults = new LlmRuntimeDefaults
                {
                    Provider = runtime.Options.DefaultProvider,
                    Model = runtime.Options.DefaultModel
                },
                McpClientFactory = runtime.McpClientFactory,
                McpCache = _mcpCache,
                McpCacheSlidingExpiration = _mcpCacheSlidingExpiration,
                HumanInputProvider = _humanInput,
                WorkflowCallResolver = CreateWorkflowCallResolver(),
                WorkflowCandidateProvider = _candidateProvider,
                Telemetry = telemetry,
                Logger = _logger,
                Limits = new ExecutionLimits
                {
                    LogStepContent = true,
                    RunId = correlationId
                }
            };

            RunResult? result = null;
            ResolvedWorkflow resolvedWorkflow;
            string? selectedAgentName;
            Exception? resolveError = null;

            try
            {
                resolvedWorkflow = await ResolveWorkflowAsync(runtime, requestedAgentName, ct);
                selectedAgentName = resolvedWorkflow.AgentName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve workflow for chat execution.");
                resolveError = ex;
                resolvedWorkflow = null!;
                selectedAgentName = null;
            }

            if (resolveError is not null)
            {
                yield return new SmartFlowEvent("error", resolveError.Message);
                yield break;
            }

            var workflow = resolvedWorkflow.Workflow;
            var inputs = BuildWorkflowInputs(task, selectedAgentName, correlationId, filesIds, workflowInputs);
            var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

            Exception? error = null;

            var executionTask = Task.Run(async () =>
            {
                var previousTaskActivity = Activity.Current;
                if (parentActivity is not null)
                    Activity.Current = parentActivity;

                try
                {
                    result = await engine.ExecuteAsync(workflow, resolvedInputs, ct);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    Activity.Current = previousTaskActivity;
                    channel.Writer.TryComplete();
                }
            }, ct);

            // Stream events as they arrive
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }

            await executionTask;

            if (error is not null)
            {
                var repaired = false;
                await foreach (var evt in OfferWorkflowRepairAsync(
                                   runtime,
                                   resolvedWorkflow,
                                   task,
                                   new WorkflowFailure("INTERNAL_ERROR", error.Message, error.GetType().FullName, null),
                                   parentActivity,
                                   repairedValue => repaired = repairedValue,
                                   ct))
                {
                    yield return evt;
                }

                if (repaired)
                    yield break;

                yield return new SmartFlowEvent("error", error.Message);
                yield break;
            }

            // Extract the final answer from workflow outputs
            if (result is { Success: true, Outputs: not null })
            {
                var handledFailure = FindRepairableHandledFailure(result);
                if (handledFailure is not null && resolvedWorkflow.Agent is not null)
                {
                    var repaired = false;
                    await foreach (var evt in OfferWorkflowRepairAsync(
                                       runtime,
                                       resolvedWorkflow,
                                       task,
                                       handledFailure,
                                       parentActivity,
                                       repairedValue => repaired = repairedValue,
                                       ct,
                                       handledFailure: true))
                    {
                        yield return evt;
                    }

                    if (repaired)
                        yield break;
                }

                var routedRepair = await TryResolveRoutedWorkflowRepairAsync(runtime, result, ct);
                if (routedRepair is not null)
                {
                    var repaired = false;
                    await foreach (var evt in OfferWorkflowRepairAsync(
                                       runtime,
                                       routedRepair.ResolvedWorkflow,
                                       task,
                                       routedRepair.Failure,
                                       parentActivity,
                                       repairedValue => repaired = repairedValue,
                                       ct,
                                       handledFailure: routedRepair.HandledFailure))
                    {
                        yield return evt;
                    }

                    if (repaired)
                        yield break;
                }

                var answer = result.Outputs["answer"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    yield return new SmartFlowEvent("answer", answer);
                }
                else
                {
                    // Fallback: serialize entire outputs
                    yield return new SmartFlowEvent("answer", result.Outputs.ToJsonString());
                }
            }
            else if (result is { Success: false })
            {
                var errMsg = result.Error?.Message ?? "Workflow execution failed";
                var repaired = false;
                await foreach (var evt in OfferWorkflowRepairAsync(
                                   runtime,
                                   resolvedWorkflow,
                                   task,
                                   WorkflowFailure.FromResult(result),
                                   parentActivity,
                                   repairedValue => repaired = repairedValue,
                                   ct))
                {
                    yield return evt;
                }

                if (repaired)
                    yield break;

                yield return new SmartFlowEvent("error", errMsg);
            }
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    private static bool IsCommand(string text, string command)
        => text.StartsWith(command, StringComparison.OrdinalIgnoreCase)
           && (text.Length == command.Length || char.IsWhiteSpace(text[command.Length]));

    private static string RenderHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GnOuGo Help");
        sb.AppendLine();
        sb.AppendLine("GnOuGo is a local agent workspace. You can chat normally, upload documents, route requests to configured agents, and use slash commands for configuration.");
        sb.AppendLine();
        sb.AppendLine("## Commands");
        sb.AppendLine();
        sb.AppendLine("| Command | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `/help` | Show this overview |");
        sb.AppendLine("| `/status` | Display the current LLM, embedding, MCP, and agent configuration summary |");
        sb.AppendLine("| `/llm` | Show LLM provider commands |");
        sb.AppendLine("| `/llm list` | List configured LLM providers |");
        sb.AppendLine("| `/llm models <name>` | List live models for a configured LLM provider |");
        sb.AppendLine("| `/llm add` | Configure a new LLM provider |");
        sb.AppendLine("| `/llm default [name]` | Set or change the default LLM provider/model |");
        sb.AppendLine("| `/llm edit <name>` | Edit an existing LLM provider |");
        sb.AppendLine("| `/llm remove <name>` | Remove an LLM provider |");
        sb.AppendLine("| `/embedding` | Show embedding model commands |");
        sb.AppendLine("| `/embedding list` | List configured embedding models |");
        sb.AppendLine("| `/embedding add` | Configure a new embedding model |");
        sb.AppendLine("| `/embedding default [name]` | Set or change the default embedding model |");
        sb.AppendLine("| `/embedding edit <name>` | Edit an embedding model configuration |");
        sb.AppendLine("| `/embedding remove <name>` | Remove an embedding model configuration |");
        sb.AppendLine("| `/mcp` | Show MCP server commands |");
        sb.AppendLine("| `/mcp list` | List configured MCP servers |");
        sb.AppendLine("| `/mcp add` | Add a new MCP server |");
        sb.AppendLine("| `/mcp edit <name>` | Edit an existing MCP server |");
        sb.AppendLine("| `/mcp remove <name>` | Remove an MCP server |");
        sb.AppendLine("| `/gnougo` | Show agent management commands |");
        sb.AppendLine("| `/gnougo list` | List configured agents |");
        sb.AppendLine("| `/gnougo add` | Create a new agent with the interactive wizard |");
        sb.AppendLine("| `/gnougo edit <name>` | Edit an existing agent |");
        sb.AppendLine("| `/gnougo reprompt <name>` | Improve an existing agent workflow from a prompt |");
        sb.AppendLine("| `/gnougo remove <name>` | Remove an agent |");
        sb.AppendLine("| `/gnougo select <name>` | Set the active chat agent |");
        sb.AppendLine();
        sb.AppendLine("## How GnOuGo Works");
        sb.AppendLine();
        sb.AppendLine("- Regular messages are routed to the active agent or to the built-in routing workflow.");
        sb.AppendLine("- Agents are reusable workflow definitions stored locally.");
        sb.AppendLine("- MCP servers expose local tools such as command execution, document operations, Git, browser automation, and code assistance.");
        sb.AppendLine("- LLM, embedding, MCP, and agent settings are persisted locally, with secrets stored encrypted through KeyVault.");
        sb.AppendLine("- Trace buttons on assistant messages open execution details for debugging and observability.");
        sb.AppendLine();
        sb.Append("Type a regular message to start working, or use one of the commands above.");
        return sb.ToString();
    }

    /// <summary>
    /// Non-streaming complete: runs the workflow and returns the final answer.
    /// </summary>
    public async Task<string> CompleteAsync(string task, CancellationToken ct)
    {
        string answer = "";
        await foreach (var evt in ExecuteAsync(task, agentName: null, ct))
        {
            if (evt.Type is "answer")
                answer = evt.Text ?? "";
            else if (evt.Type is "error")
                throw new InvalidOperationException(evt.Text);
        }
        return answer;
    }

    public async Task<string> CompleteAsync(string task, string? agentName, CancellationToken ct)
    {
        string answer = "";
        await foreach (var evt in ExecuteAsync(task, agentName, ct))
        {
            if (evt.Type is "answer")
                answer = evt.Text ?? "";
            else if (evt.Type is "error")
                throw new InvalidOperationException(evt.Text);
        }
        return answer;
    }

    public async Task<string> CompleteAsync(string task, string? agentName, IReadOnlyList<string>? filesIds, CancellationToken ct)
    {
        string answer = "";
        await foreach (var evt in ExecuteAsync(task, correlationId: null, agentName: agentName, filesIds: filesIds, ct))
        {
            if (evt.Type is "answer")
                answer = evt.Text ?? "";
            else if (evt.Type is "error")
                throw new InvalidOperationException(evt.Text);
        }
        return answer;
    }

    public async Task<WorkflowInputSchema> GetActiveWorkflowInputSchemaAsync(
        string? agentName,
        CancellationToken ct)
    {
        await using var runtime = await _runtimeFactory.CreateAsync(ct);

        try
        {
            var resolved = await ResolveWorkflowAsync(runtime, agentName, ct);
            return WorkflowInputComposer.FromWorkflow(resolved.AgentName, resolved.Workflow.Source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve active workflow input schema.");
            return new WorkflowInputSchema(agentName, Array.Empty<WorkflowInputFieldSchema>(), ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken ct)
    {
        try
        {
            await using var runtime = await _runtimeFactory.CreateAsync(ct);
            await using var session = await runtime.McpClientFactory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
            var call = await session.CallToolAsync("agent_list", null, ct);

            if (call.IsError)
                return Array.Empty<string>();

            var response = call.Content as JsonObject;
            if (response is null || (response["success"]?.GetValue<bool>() ?? false) != true)
                return Array.Empty<string>();

            var names = (response["agents"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(agent => agent["name"]?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list configured agents from Agent MCP.");
            return Array.Empty<string>();
        }
    }

    private async Task<ResolvedWorkflow> ResolveWorkflowAsync(
        SecureWorkflowRuntimeSession runtime,
        string? requestedAgentName,
        CancellationToken ct)
    {
        var selectedAgentName = await ResolveAgentNameAsync(requestedAgentName, ct);
        if (!string.IsNullOrWhiteSpace(selectedAgentName))
        {
            var workflowResult = await LoadAgentWorkflowAsync(runtime, selectedAgentName, ct);
            if (workflowResult.Workflow is not null)
                return new ResolvedWorkflow(workflowResult.Workflow, selectedAgentName, workflowResult.Agent);

            throw new InvalidOperationException(
                workflowResult.ErrorMessage
                ?? $"Selected agent '{selectedAgentName}' could not be loaded from {AgentMcpHostingExtensions.ServerName}.");
        }

        return new ResolvedWorkflow(CompileRoutingWorkflow(), null, null);
    }

    private async Task<string?> ResolveAgentNameAsync(string? requestedAgentName, CancellationToken ct)
    {
        var normalizedRequestedAgentName = string.IsNullOrWhiteSpace(requestedAgentName)
            ? null
            : requestedAgentName.Trim();

        var snapshot = await TryGetUserConfigSnapshotAsync(ct);
        if (snapshot is not null)
        {
            var persistedDefaultAgent = string.IsNullOrWhiteSpace(snapshot.DefaultAgent)
                ? null
                : snapshot.DefaultAgent.Trim();

            if (!string.IsNullOrWhiteSpace(persistedDefaultAgent))
            {
                if (!string.IsNullOrWhiteSpace(normalizedRequestedAgentName)
                    && !string.Equals(persistedDefaultAgent, normalizedRequestedAgentName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Ignoring requested agent '{RequestedAgentName}' because persisted default agent '{PersistedDefaultAgent}' is active.",
                        normalizedRequestedAgentName,
                        persistedDefaultAgent);
                }

                return persistedDefaultAgent;
            }
        }

        return normalizedRequestedAgentName;
    }

    private async Task<AgentUserConfigSnapshot?> TryGetUserConfigSnapshotAsync(CancellationToken ct)
    {
        AgentUserConfigSnapshot? snapshot = null;

        if (_userConfigClient is not null)
        {
            snapshot = await _userConfigClient.GetAsync(ct);
            if (!string.IsNullOrWhiteSpace(snapshot.DefaultAgent))
                return snapshot;
        }

        if (_scopeFactory is null)
            return snapshot;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IUserConfigRepository>();
        if (repository is null)
            return snapshot;

        return ToAgentUserConfigSnapshot(await repository.GetAsync(ct: ct));
    }

    private static AgentUserConfigSnapshot ToAgentUserConfigSnapshot(UserConfigSnapshot snapshot)
        => new(
            snapshot.DefaultLlmProvider,
            snapshot.DefaultLlmModel,
            snapshot.DefaultEmbeddingConfig,
            snapshot.DefaultAgent,
            NormalizeModelOverrides(snapshot.ModelOverrides),
            snapshot.UpdatedAt);

    private static IReadOnlyDictionary<string, LLMModelMetadata> NormalizeModelOverrides(
        IReadOnlyDictionary<string, LLMModelMetadata>? modelOverrides)
        => modelOverrides is null
            ? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LLMModelMetadata>(modelOverrides, StringComparer.OrdinalIgnoreCase);

    private async Task<AgentWorkflowLoadResult> LoadAgentWorkflowAsync(
        SecureWorkflowRuntimeSession runtime,
        string agentName,
        CancellationToken ct)
    {
        await using var session = await runtime.McpClientFactory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var result = await session.CallToolAsync("agent_get_by_name", new JsonObject
        {
            ["name"] = agentName
        }, ct);

        if (result.IsError)
        {
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' could not be loaded because the mounted {AgentMcpHostingExtensions.ServerName} call failed.");
        }

        var payload = result.Content as JsonObject;
        if (payload is null)
        {
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' could not be loaded because {AgentMcpHostingExtensions.ServerName} returned an unexpected payload.");
        }

        if ((payload["success"]?.GetValue<bool>()).GetValueOrDefault() != true)
        {
            var errorMessage = payload["error_message"]?.GetValue<string>();
            var errorCode = payload["error_code"]?.GetValue<string>();
            var detail = !string.IsNullOrWhiteSpace(errorMessage)
                ? errorMessage
                : !string.IsNullOrWhiteSpace(errorCode)
                    ? errorCode
                    : "Unknown error.";

            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' could not be loaded from {AgentMcpHostingExtensions.ServerName}. {detail}");
        }

        var agentObject = payload["agent"] as JsonObject;
        var workflowText = agentObject?["workflow"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workflowText))
        {
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' does not contain a workflow definition in {AgentMcpHostingExtensions.ServerName}.");
        }

        var agent = new AgentDto(
            agentObject?["id"]?.GetValue<string>() ?? "",
            agentObject?["name"]?.GetValue<string>() ?? agentName,
            workflowText,
            agentObject?["original_prompt"]?.GetValue<string>(),
            agentObject?["created_at"]?.GetValue<string>() ?? "",
            agentObject?["updated_at"]?.GetValue<string>() ?? "");

        try
        {
            var document = WorkflowParser.Parse(workflowText);
            var compiled = new WorkflowCompiler().Compile(document);
            var entrypoint = compiled.Entrypoint;
            if (entrypoint is null || !compiled.Workflows.TryGetValue(entrypoint, out var workflow))
                throw new InvalidOperationException($"Agent '{agentName}' does not expose a valid entrypoint workflow.");

            return AgentWorkflowLoadResult.Success(workflow, agent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compile workflow for agent '{AgentName}'.", agentName);
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' has an invalid workflow definition. {ex.Message}");
        }
    }

    private async IAsyncEnumerable<SmartFlowEvent> OfferWorkflowRepairAsync(
        SecureWorkflowRuntimeSession runtime,
        ResolvedWorkflow resolvedWorkflow,
        string task,
        WorkflowFailure failure,
        Activity? parentActivity,
        Action<bool> setRepaired,
        [EnumeratorCancellation] CancellationToken ct,
        bool handledFailure = false)
    {
        setRepaired(false);

        var agent = resolvedWorkflow.Agent;
        if (agent is null || string.IsNullOrWhiteSpace(agent.Id) || string.IsNullOrWhiteSpace(agent.Workflow))
            yield break;

        var request = new HumanInputRequest
        {
            RunId = $"repair-{Guid.NewGuid():N}",
            StepId = "agent_workflow_repair",
            Mode = HumanInputContract.ModeChoice,
            Prompt = handledFailure
                ? $"The selected agent '{agent.Name}' handled an MCP error while running. Do you want GnOuGo to improve and save this workflow using the error details?"
                : $"The selected agent '{agent.Name}' failed while running. Do you want GnOuGo to improve and save this workflow using the error details?",
            Choices = ["improve", "skip"],
            TimeoutMs = HumanInputContract.DefaultTimeoutMs,
            Context = new JsonObject
            {
                ["agent"] = agent.Name,
                ["handled"] = handledFailure,
                ["error_code"] = failure.Code,
                ["error_message"] = failure.Message,
                ["error_type"] = failure.Type,
                ["details"] = failure.Details?.DeepClone()
            }
        };

        JsonNode? decision = null;
        await foreach (var evt in EmitHumanInputRequestAsync(request, response => decision = response, ct))
            yield return evt;

        if (!IsImproveDecision(decision))
            yield break;

        yield return new SmartFlowEvent("thinking:info", $"Repairing workflow for agent '{agent.Name}' from the latest execution error...");

        var repairWorkflow = CompileEmbeddedWorkflow(BuildRepairWorkflowYaml(), "agent-workflow-repair.yaml");
        var repairInputs = new JsonObject
        {
            ["agent_id"] = agent.Id,
            ["agent_name"] = agent.Name,
            ["original_prompt"] = agent.OriginalPrompt ?? "",
            ["current_workflow"] = agent.Workflow,
            ["user_prompt"] = task,
            ["error_code"] = failure.Code,
            ["error_type"] = failure.Type ?? "",
            ["error_message"] = failure.Message,
            ["error_details"] = failure.Details?.DeepClone()
        };

        var channel = Channel.CreateUnbounded<SmartFlowEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var telemetry = new CompositeWorkflowTelemetry(
            new AgentStreamingTelemetry(evt => channel.Writer.TryWrite(evt)),
            _otel);

        var repairEngine = new WorkflowEngine
        {
            LLMClient = runtime.LlmClient,
            LLMCapabilities = runtime.LlmCapabilityResolver,
            LlmDefaults = new LlmRuntimeDefaults
            {
                Provider = runtime.Options.DefaultProvider,
                Model = runtime.Options.DefaultModel
            },
            McpClientFactory = runtime.McpClientFactory,
            McpCache = _mcpCache,
            McpCacheSlidingExpiration = _mcpCacheSlidingExpiration,
            HumanInputProvider = _humanInput,
            WorkflowCallResolver = CreateWorkflowCallResolver(),
            WorkflowCandidateProvider = _candidateProvider,
            Telemetry = telemetry,
            Logger = _logger,
            Limits = new ExecutionLimits
            {
                LogStepContent = true,
                RunId = $"repair-{Guid.NewGuid():N}"
            }
        };

        RunResult? repairResult = null;
        Exception? repairError = null;
        var executionTask = Task.Run(async () =>
        {
            var previousTaskActivity = Activity.Current;
            if (parentActivity is not null)
                Activity.Current = parentActivity;

            try
            {
                repairResult = await repairEngine.ExecuteAsync(repairWorkflow, repairInputs, ct);
            }
            catch (Exception ex)
            {
                repairError = ex;
            }
            finally
            {
                Activity.Current = previousTaskActivity;
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            yield return evt;

        await executionTask;

        if (repairError is not null)
        {
            _logger.LogWarning(repairError, "Could not repair workflow for agent '{AgentName}'.", agent.Name);
            yield return new SmartFlowEvent(
                "error",
                $"Workflow execution failed, and the automatic repair could not be saved. Original error: {failure.Message}. Repair error: {repairError.Message}");
            yield break;
        }

        if (repairResult is not { Success: true })
        {
            var repairMessage = repairResult?.Error?.Message ?? "Unknown repair error.";
            yield return new SmartFlowEvent(
                "error",
                $"Workflow execution failed, and the automatic repair could not be saved. Original error: {failure.Message}. Repair error: {repairMessage}");
            yield break;
        }

        var attempts = repairResult.Outputs?["attempt"]?.GetValue<int>() ?? 1;
        var repairedWorkflow = repairResult.Outputs?["updated_yaml"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(repairedWorkflow))
        {
            yield return new SmartFlowEvent(
                "error",
                $"Workflow execution failed, and the automatic repair did not produce a replacement workflow. Original error: {failure.Message}.");
            yield break;
        }

        yield return new SmartFlowEvent(
            "thinking:response",
            $"📝 Proposed repaired workflow for '{agent.Name}':\n\n```yaml\n{repairedWorkflow}\n```");

        var saveRequest = new HumanInputRequest
        {
            RunId = $"repair-save-{Guid.NewGuid():N}",
            StepId = "agent_workflow_repair_save",
            Mode = HumanInputContract.ModeChoice,
            Prompt = $"Save this repaired workflow for '{agent.Name}'?",
            Choices = ["save", "discard"],
            TimeoutMs = HumanInputContract.DefaultTimeoutMs,
            Context = new JsonObject
            {
                ["agent"] = agent.Name,
                ["handled"] = handledFailure,
                ["attempts"] = attempts,
                ["workflow"] = repairedWorkflow
            }
        };

        JsonNode? saveDecision = null;
        await foreach (var evt in EmitHumanInputRequestAsync(saveRequest, response => saveDecision = response, ct))
            yield return evt;

        if (!IsSaveDecision(saveDecision))
        {
            setRepaired(true);
            yield return new SmartFlowEvent(
                "answer",
                $"Workflow repair for agent '{agent.Name}' was discarded. Original error: {failure.Message}");
            yield break;
        }

        var saveError = await TrySaveAgentWorkflowAsync(runtime, agent, repairedWorkflow, ct);
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            yield return new SmartFlowEvent(
                "error",
                $"Workflow execution failed, and the repaired workflow could not be saved. Original error: {failure.Message}. Save error: {saveError}");
            yield break;
        }

        setRepaired(true);
        yield return new SmartFlowEvent(
            "answer",
            handledFailure
                ? $"The workflow for agent '{agent.Name}' handled an MCP error on this run, and I repaired and saved the agent workflow through {AgentMcpHostingExtensions.ServerName}. Please retry the request. Repair planning attempts: {attempts}."
                : $"The workflow for agent '{agent.Name}' failed on this run, but I repaired and saved the agent workflow through {AgentMcpHostingExtensions.ServerName}. Please retry the request. Repair planning attempts: {attempts}.");
    }

    private async IAsyncEnumerable<SmartFlowEvent> EmitHumanInputRequestAsync(
        HumanInputRequest request,
        Action<JsonNode?> captureResponse,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SmartFlowEvent("human_input_request", BuildHumanInputPayload(request).ToJsonString());
        var response = await _humanInput.RequestInputAsync(request, ct);
        captureResponse(response);
    }

    private static JsonObject BuildHumanInputPayload(HumanInputRequest request)
    {
        var payload = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["mode"] = request.Mode,
            ["run_id"] = request.RunId,
            ["step_id"] = request.StepId,
            ["timeout_ms"] = request.TimeoutMs
        };

        if (request.Context is not null)
            payload["context"] = request.Context.DeepClone();

        if (request.Choices is not null)
            payload["choices"] = new JsonArray(request.Choices.Select(choice => (JsonNode?)JsonValue.Create(choice)).ToArray());

        if (request.Fields is not null)
        {
            payload["fields"] = new JsonArray(request.Fields.Select(field =>
            {
                var fieldObject = new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type,
                    ["required"] = field.Required
                };
                if (!string.IsNullOrWhiteSpace(field.Description))
                    fieldObject["description"] = field.Description;
                if (field.Options is not null)
                    fieldObject["options"] = new JsonArray(field.Options.Select(option => (JsonNode?)JsonValue.Create(option)).ToArray());
                if (!string.IsNullOrWhiteSpace(field.Default))
                    fieldObject["default"] = field.Default;
                return (JsonNode?)fieldObject;
            }).ToArray());
        }

        return payload;
    }

    private static bool IsImproveDecision(JsonNode? response)
    {
        var value = response switch
        {
            JsonObject obj => obj["response"]?.GetValue<string>(),
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            _ => null
        };

        return string.Equals(value, "improve", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "approve", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSaveDecision(JsonNode? response)
    {
        var value = response switch
        {
            JsonObject obj => obj["response"]?.GetValue<string>(),
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            _ => null
        };

        return string.Equals(value, "save", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "approve", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TrySaveAgentWorkflowAsync(
        SecureWorkflowRuntimeSession runtime,
        AgentDto agent,
        string repairedWorkflow,
        CancellationToken ct)
    {
        await using var session = await runtime.McpClientFactory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var result = await session.CallToolAsync("agent_update", new JsonObject
        {
            ["id"] = agent.Id,
            ["name"] = agent.Name,
            ["workflow"] = repairedWorkflow,
            ["originalPrompt"] = agent.OriginalPrompt ?? ""
        }, ct);

        if (result.IsError)
            return $"The mounted {AgentMcpHostingExtensions.ServerName} update call failed.";

        var payload = result.Content as JsonObject;
        if (payload is null)
            return $"{AgentMcpHostingExtensions.ServerName} returned an unexpected update payload.";

        if ((payload["success"]?.GetValue<bool>()).GetValueOrDefault() == true)
            return null;

        return payload["error_message"]?.GetValue<string>()
               ?? payload["error_code"]?.GetValue<string>()
               ?? "Unknown update error.";
    }

    private async Task<RoutedWorkflowRepair?> TryResolveRoutedWorkflowRepairAsync(
        SecureWorkflowRuntimeSession runtime,
        RunResult result,
        CancellationToken ct)
    {
        foreach (var stepResult in result.StepResults)
        {
            if (!string.Equals(stepResult.StepType, "workflow.route", StringComparison.OrdinalIgnoreCase)
                || stepResult.Output is not JsonObject routeOutput
                || routeOutput["results"] is not JsonArray routeResults)
            {
                continue;
            }

            foreach (var routeResultNode in routeResults)
            {
                if (routeResultNode is not JsonObject routeResult)
                    continue;

                var agentName = ExtractRoutedDatabaseAgentName(routeResult);
                if (string.IsNullOrWhiteSpace(agentName))
                    continue;

                var failure = ExtractRoutedWorkflowFailure(routeResult, out var handledFailure);
                if (failure is null)
                    continue;

                var workflowResult = await LoadAgentWorkflowAsync(runtime, agentName, ct);
                if (workflowResult.Workflow is null || workflowResult.Agent is null)
                {
                    _logger.LogWarning(
                        "Could not load routed agent '{AgentName}' for workflow repair. {Error}",
                        agentName,
                        workflowResult.ErrorMessage);
                    continue;
                }

                return new RoutedWorkflowRepair(
                    new ResolvedWorkflow(workflowResult.Workflow, agentName, workflowResult.Agent),
                    failure,
                    handledFailure);
            }
        }

        return null;
    }

    private static WorkflowFailure? ExtractRoutedWorkflowFailure(JsonObject routeResult, out bool handledFailure)
    {
        handledFailure = false;

        if (TryGetBoolean(routeResult["success"]) == false)
        {
            var errorCode = GetString(routeResult["error_code"]);
            if (IsRepairableMcpErrorCode(errorCode))
            {
                return new WorkflowFailure(
                    errorCode!,
                    GetString(routeResult["error"]) ?? "Routed workflow execution failed.",
                    GetString(routeResult["error_type"]),
                    EnrichRoutedFailureDetails(routeResult, routeResult["error_details"]));
            }
        }

        if (routeResult["handled_errors"] is not JsonArray handledErrors)
            return null;

        foreach (var handledErrorNode in handledErrors)
        {
            if (handledErrorNode is not JsonObject handledError)
                continue;

            var errorCode = GetString(handledError["code"]);
            if (!IsRepairableMcpErrorCode(errorCode))
                continue;

            handledFailure = true;
            return new WorkflowFailure(
                errorCode!,
                GetString(handledError["message"]) ?? "Routed workflow handled an MCP error.",
                GetString(handledError["type"]),
                EnrichRoutedFailureDetails(routeResult, handledError["details"], handledError));
        }

        return null;
    }

    private static JsonNode? EnrichRoutedFailureDetails(
        JsonObject routeResult,
        JsonNode? details,
        JsonObject? handledError = null)
    {
        var enriched = details?.DeepClone() as JsonObject ?? new JsonObject();
        enriched["routed"] = true;
        enriched["route_result_id"] = GetString(routeResult["id"]);
        enriched["route_result_name"] = GetString(routeResult["name"]);
        enriched["workflow"] = GetString(routeResult["workflow"]);

        if (handledError is not null)
        {
            enriched["handled"] = true;
            enriched["step_id"] = GetString(handledError["step_id"]);
            enriched["step_type"] = GetString(handledError["step_type"]);
            enriched["step_status"] = GetString(handledError["status"]);
        }

        return enriched;
    }

    private static string? ExtractRoutedDatabaseAgentName(JsonObject routeResult)
    {
        if (routeResult["ref"] is JsonObject refObj
            && string.Equals(GetString(refObj["kind"]), "database", StringComparison.OrdinalIgnoreCase))
        {
            return GetString(refObj["agent"])
                   ?? GetString(refObj["name"])
                   ?? GetString(routeResult["name"]);
        }

        var id = GetString(routeResult["id"]);
        if (id is not null && id.StartsWith("database:", StringComparison.OrdinalIgnoreCase))
            return GetString(routeResult["name"]) ?? id["database:".Length..];

        return null;
    }

    private static WorkflowFailure? FindRepairableHandledFailure(RunResult result)
    {
        foreach (var stepResult in result.StepResults)
        {
            var error = stepResult.Error;
            if (error is null || !IsRepairableHandledError(error))
                continue;

            var details = error.Details?.DeepClone() as JsonObject ?? new JsonObject();
            details["step_id"] = stepResult.StepId;
            details["step_type"] = stepResult.StepType;
            details["handled"] = true;

            return new WorkflowFailure(
                error.Code,
                error.Message,
                string.IsNullOrWhiteSpace(error.Type) ? stepResult.StepType : error.Type,
                details);
        }

        return null;
    }

    private static bool IsRepairableHandledError(WorkflowError error)
        => IsRepairableMcpErrorCode(error.Code);

    private static bool IsRepairableMcpErrorCode(string? errorCode)
        => errorCode is ErrorCodes.McpCallError
            or ErrorCodes.McpPromptError
            or ErrorCodes.McpConnectionError
            or ErrorCodes.McpServerNotFound
            or ErrorCodes.McpTimeout;

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            ? string.IsNullOrWhiteSpace(text) ? null : text
            : null;

    private static bool? TryGetBoolean(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var boolean)
            ? boolean
            : null;

    private static string BuildRepairWorkflowYaml() => """
        version: 1
        name: agent-workflow-repair
        workflows:
          main:
            inputs:
              agent_id:
                type: string
                required: true
              agent_name:
                type: string
                required: true
              original_prompt:
                type: string
                required: false
                default: ""
              current_workflow:
                type: string
                required: true
              user_prompt:
                type: string
                required: true
              error_code:
                type: string
                required: false
                default: ""
              error_type:
                type: string
                required: false
                default: ""
              error_message:
                type: string
                required: true
              error_details:
                type: object
                required: false

            steps:
              - id: plan_repair
                type: workflow.plan
                input:
                  mode: repair
                  generator:
                    reasoning: medium
                    instruction: |
                      Keep the same agent name and preserve the chat-agent contract:
                      - It must be executable by GnOuGo.Agent.Server for a user chat message.
                      - It must accept a user task/prompt input such as `task`.
                      - It must expose an `answer` output string.
                      - It must remain self-contained and must not ask the user to review its own YAML.
                      - Prefer fixing the smallest root cause that explains the latest runtime error.
                      - For MCP failures, update the MCP request shape, output access, error handling, or tool choice based on the error details.
                      - `mcp.call` raises workflow errors by default; use `on_error` only when the workflow can recover intentionally.
                    context: |
                      This repair is being triggered after a real execution failure in GnOuGo.Agent.Server.
                      The replacement YAML will be shown to the user and persisted through GnOuGo.Agent.Mcp `agent_update` only after explicit confirmation.
                      Keep the generated workflow compatible with the available DSL and MCP tool contracts.
                      Agent name: ${data.inputs.agent_name}
                      Original agent prompt: ${data.inputs.original_prompt}
                  repair:
                    existing_yaml: "${data.inputs.current_workflow}"
                    failed_input: "${data.inputs.user_prompt}"
                    error:
                      code: "${data.inputs.error_code}"
                      type: "${data.inputs.error_type}"
                      message: "${data.inputs.error_message}"
                      details: "${data.inputs.error_details}"
                  policy:
                    allowed_step_types:
                      - emit
                      - set
                      - template.render
                      - llm.call
                      - mcp.list
                      - mcp.call
                      - sequence
                      - parallel
                      - loop.sequential
                      - loop.parallel
                      - switch
                      - human.input
                    denied_step_types:
                      - workflow.plan
                      - workflow.execute
                      - workflow.call
                    allow_remote_workflow_refs: false
                  limits:
                    max_steps_total: 100
                  validate:
                    compile: true
                    dry_run: true
                  on_invalid:
                    action: reprompt
                    max_attempts: 10

            outputs:
              answer:
                expr: "${'Planned repaired workflow for agent ' + data.inputs.agent_name}"
                type: string
              attempt:
                expr: "${data.steps.plan_repair.meta.attempt}"
                type: number
              updated_yaml:
                expr: "${data.steps.plan_repair.yaml}"
                type: string
        """;

    private CompiledWorkflow CompileRoutingWorkflow()
        => CompileEmbeddedWorkflow(_routingWorkflowYaml, "main-routing-agent.yaml");

    private static CompiledWorkflow CompileEmbeddedWorkflow(string yaml, string resourceName)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException($"No entrypoint workflow found in {resourceName}");

        return compiled.Workflows[entrypoint];
    }

    private static string LoadEmbeddedWorkflowYaml(string fileName)
    {
        var asm = typeof(SmartFlowService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded resource '{fileName}' not found. " +
                "Available: " + string.Join(", ", asm.GetManifestResourceNames()));

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private IWorkflowCallResolver CreateWorkflowCallResolver()
    {
        var workspaceRoot = DiscoverWorkspaceRoot(Directory.GetCurrentDirectory())
            ?? DiscoverWorkspaceRoot(AppContext.BaseDirectory);

        return _scopeFactory is not null
            ? new AgentDatabaseWorkflowCallResolver(_scopeFactory, workspaceRoot)
            : new DefaultWorkflowCallResolver(workspaceRoot);
    }

    private static string? DiscoverWorkspaceRoot(string startPath)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(startPath));
            while (current is not null)
            {
                if (current.GetFiles("*.sln").Length != 0 || Directory.Exists(Path.Combine(current.FullName, ".git")))
                    return current.FullName;

                current = current.Parent;
            }
        }
        catch
        {
            // Best effort only; workspace calls will fail closed when no root is configured.
        }

        return null;
    }

    private static JsonObject BuildWorkflowInputs(
        string task,
        string? agentName,
        string correlationId,
        IReadOnlyList<string>? filesIds,
        JsonObject? workflowInputs)
    {
        var inputs = new JsonObject
        {
            ["task"] = task,
            ["prompt"] = task,
            ["query"] = task,
            ["request"] = task,
            ["input"] = task,
            ["message"] = task,
            ["correlation_id"] = correlationId
        };

        if (workflowInputs is not null)
        {
            foreach (var (key, value) in workflowInputs)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    inputs[key] = value?.DeepClone();
            }
        }

        if (!string.IsNullOrWhiteSpace(agentName))
            inputs["agent_name"] = agentName;

        if (filesIds is { Count: > 0 })
        {
            var camelCaseIds = new JsonArray();
            var snakeCaseIds = new JsonArray();
            foreach (var id in filesIds.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                JsonNode? camelCaseId = JsonValue.Create(id);
                JsonNode? snakeCaseId = JsonValue.Create(id);
                camelCaseIds.Add(camelCaseId);
                snakeCaseIds.Add(snakeCaseId);
            }

            if (camelCaseIds.Count > 0)
            {
                inputs["filesIds"] = camelCaseIds;
                inputs["files_ids"] = snakeCaseIds;
            }
        }

        return inputs;
    }

    private JsonArray LoadConversationHistory(string conversationId, int topK)
    {
        var history = new JsonArray();
        if (_historyStore is null || string.IsNullOrWhiteSpace(conversationId))
            return history;

        var messages = _historyStore.GetMessages(conversationId, topK).Messages;
        foreach (var message in messages)
        {
            history.Add((JsonNode)new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
                ["created_at"] = message.CreatedAt.ToString("o")
            });
        }

        return history;
    }

    private static JsonObject MergeWorkflowInputsWithConversation(
        JsonObject? workflowInputs,
        string conversationId,
        JsonArray history)
    {
        var merged = workflowInputs?.DeepClone() as JsonObject ?? new JsonObject();
        merged["conversation_id"] = conversationId;
        merged["conversationId"] = conversationId;
        merged["history"] = history.DeepClone();
        return merged;
    }

    private void AppendConversationTurn(
        string conversationId,
        string userPrompt,
        string assistantAnswer,
        string correlationId)
    {
        if (_historyStore is null || string.IsNullOrWhiteSpace(conversationId))
            return;

        var now = DateTimeOffset.UtcNow;
        _historyStore.AppendMessages(conversationId, new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = userPrompt,
                CreatedAt = now,
                Meta = CreateMessageMeta(correlationId)
            },
            new()
            {
                Role = "assistant",
                Content = assistantAnswer,
                CreatedAt = now,
                Meta = CreateMessageMeta(correlationId)
            }
        });
    }

    private static System.Text.Json.JsonElement CreateMessageMeta(string correlationId)
    {
        using var document = System.Text.Json.JsonDocument.Parse($$"""{"correlation_id":"{{correlationId}}"}""");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Generates a short title for the conversation using a direct LLM call.
    /// </summary>
    public async Task<string> SuggestTitleAsync(
        IReadOnlyList<ChatMessageDto> messages,
        CancellationToken ct)
    {
        var firstUser = messages.FirstOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
        firstUser = (firstUser ?? string.Empty).Trim();
        if (firstUser.Length > 280)
            firstUser = firstUser[..280];

        var prompt =
            $"You generate concise chat titles. Output ONLY the title, 2 to 6 words, no quotes, no punctuation at the end.\n\nConversation starts with: {firstUser}\nTitle:";

        // Leave Model empty so DynamicRoutingLLMClientAdapter resolves the
        // current default model from LLMRuntimeOptionsStore (configured via /llm).
        var response = await _llm.CallAsync(new LLMRequest
        {
            Model = string.Empty,
            Prompt = prompt,
            Temperature = 0.3
        }, ct);

        var raw = response.Text.Trim().Trim('"', '\'', '\u201C', '\u201D');
        if (raw.Length > 60)
            raw = raw[..60].Trim();
        return raw;
    }

    private sealed record ResolvedWorkflow(CompiledWorkflow Workflow, string? AgentName, AgentDto? Agent);

    private sealed record RoutedWorkflowRepair(ResolvedWorkflow ResolvedWorkflow, WorkflowFailure Failure, bool HandledFailure);

    private sealed record WorkflowFailure(string Code, string Message, string? Type, JsonNode? Details)
    {
        public static WorkflowFailure FromResult(RunResult result)
            => new(
                result.Error?.Code ?? "WORKFLOW_EXECUTION_ERROR",
                result.Error?.Message ?? "Workflow execution failed.",
                result.Error?.Type,
                result.Error?.Details?.DeepClone());
    }

    private sealed record AgentWorkflowLoadResult(CompiledWorkflow? Workflow, AgentDto? Agent, string? ErrorMessage)
    {
        public static AgentWorkflowLoadResult Success(CompiledWorkflow workflow, AgentDto agent) => new(workflow, agent, null);
        public static AgentWorkflowLoadResult Fail(string errorMessage) => new(null, null, errorMessage);
    }
}
