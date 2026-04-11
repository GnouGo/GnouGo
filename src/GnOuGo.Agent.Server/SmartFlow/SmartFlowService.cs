using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Shared;
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
    string? TraceId = null)
{
    public static SmartFlowEvent TraceStarted(string correlationId, string traceId)
        => new("trace.started", null, correlationId, traceId);

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
    private readonly IUserConfigRepository? _userConfigRepository;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<SmartFlowService> _logger;
    private readonly string _workflowYaml;

    /// <summary>Slash commands that route to the configure-providers workflow.</summary>
    private static readonly string[] ProviderCommands = { "/llm", "/mcp", "/status" };

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
        IUserConfigRepository? userConfigRepository = null)
    {
        _llm = llm;
        _mcpCache = mcpCache;
        _runtimeFactory = runtimeFactory;
        _configureProviders = configureProviders;
        _configureAgents = configureAgents;
        _humanInput = humanInput;
        _userConfigRepository = userConfigRepository;
        _userConfigClient = userConfigClient;
        _otel = otel;
        _logger = logger;

        // Load the embedded workflow YAML
        var asm = typeof(SmartFlowService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("dynamic-workflow-agent.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException(
                "Embedded resource 'dynamic-workflow-agent.yaml' not found. " +
                "Available: " + string.Join(", ", asm.GetManifestResourceNames()));

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        _workflowYaml = reader.ReadToEnd();
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
            userConfigRepository: null)
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
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? ActivityTraceId.CreateRandom().ToHexString()
            : correlationId.Trim();

        using var messageTrace = _otel.StartChatMessageActivity(effectiveCorrelationId, task);
        yield return SmartFlowEvent.TraceStarted(effectiveCorrelationId, messageTrace.TraceId);

        var hasError = false;

        await foreach (var evt in ExecuteCoreAsync(task, effectiveCorrelationId, agentName, messageTrace.Activity, ct))
        {
            hasError |= string.Equals(evt.Type, "error", StringComparison.OrdinalIgnoreCase);
            yield return evt.WithCorrelation(effectiveCorrelationId);
        }

        messageTrace.SetStatus(hasError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteCoreAsync(
        string task,
        string correlationId,
        string? requestedAgentName,
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

            // Route /agent commands to ConfigureAgentsService
            if (trimmed.StartsWith("/agent", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 6 || char.IsWhiteSpace(trimmed[6])))
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
                if (trimmed.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)
                    && (trimmed.Length == cmd.Length || char.IsWhiteSpace(trimmed[cmd.Length])))
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
                LlmDefaults = new LlmRuntimeDefaults
                {
                    Provider = runtime.Options.DefaultProvider,
                    Model = runtime.Options.DefaultModel
                },
                McpClientFactory = runtime.McpClientFactory,
                McpCache = _mcpCache,
                HumanInputProvider = _humanInput,
                Telemetry = telemetry,
                Logger = _logger,
                Limits = new ExecutionLimits
                {
                    LogStepContent = true,
                    RunId = correlationId
                }
            };

            RunResult? result = null;
            CompiledWorkflow workflow;
            string? selectedAgentName;
            Exception? resolveError = null;

            try
            {
                (workflow, selectedAgentName) = await ResolveWorkflowAsync(runtime, requestedAgentName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve workflow for chat execution.");
                resolveError = ex;
                workflow = null!;
                selectedAgentName = null;
            }

            if (resolveError is not null)
            {
                yield return new SmartFlowEvent("error", resolveError.Message);
                yield break;
            }

            var inputs = BuildWorkflowInputs(task, selectedAgentName, correlationId);
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
                yield return new SmartFlowEvent("error", error.Message);
                yield break;
            }

            // Extract the final answer from workflow outputs
            if (result is { Success: true, Outputs: not null })
            {
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
                yield return new SmartFlowEvent("error", errMsg);
            }
        }
        finally
        {
            Activity.Current = previousActivity;
        }
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

    private async Task<(CompiledWorkflow Workflow, string? AgentName)> ResolveWorkflowAsync(
        SecureWorkflowRuntimeSession runtime,
        string? requestedAgentName,
        CancellationToken ct)
    {
        var selectedAgentName = await ResolveAgentNameAsync(requestedAgentName, ct);
        if (!string.IsNullOrWhiteSpace(selectedAgentName))
        {
            var workflowResult = await LoadAgentWorkflowAsync(runtime, selectedAgentName, ct);
            if (workflowResult.Workflow is not null)
                return (workflowResult.Workflow, selectedAgentName);

            throw new InvalidOperationException(
                workflowResult.ErrorMessage
                ?? $"Selected agent '{selectedAgentName}' could not be loaded from {AgentMcpHostingExtensions.ServerName}.");
        }

        return (CompileEmbeddedWorkflow(), null);
    }

    private async Task<string?> ResolveAgentNameAsync(string? requestedAgentName, CancellationToken ct)
    {
        var normalizedRequestedAgentName = string.IsNullOrWhiteSpace(requestedAgentName)
            ? null
            : requestedAgentName.Trim();

        if (_userConfigRepository is not null || _userConfigClient is not null)
        {
            var snapshot = _userConfigRepository is not null
                ? ToAgentUserConfigSnapshot(await _userConfigRepository.GetAsync(ct: ct))
                : await _userConfigClient!.GetAsync(ct);
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

    private static AgentUserConfigSnapshot ToAgentUserConfigSnapshot(UserConfigSnapshot snapshot)
        => new(
            snapshot.DefaultLlmProvider,
            snapshot.DefaultLlmModel,
            snapshot.DefaultAgent,
            snapshot.UpdatedAt);

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

        var workflowText = payload["agent"]?["workflow"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workflowText))
        {
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' does not contain a workflow definition in {AgentMcpHostingExtensions.ServerName}.");
        }

        try
        {
            var document = WorkflowParser.Parse(workflowText);
            var compiled = new WorkflowCompiler().Compile(document);
            var entrypoint = compiled.Entrypoint;
            if (entrypoint is null || !compiled.Workflows.TryGetValue(entrypoint, out var workflow))
                throw new InvalidOperationException($"Agent '{agentName}' does not expose a valid entrypoint workflow.");

            return AgentWorkflowLoadResult.Success(workflow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compile workflow for agent '{AgentName}'.", agentName);
            return AgentWorkflowLoadResult.Fail(
                $"The selected agent '{agentName}' has an invalid workflow definition. {ex.Message}");
        }
    }

    private CompiledWorkflow CompileEmbeddedWorkflow()
    {
        var doc = WorkflowParser.Parse(_workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException("No entrypoint workflow found in dynamic-workflow-agent.yaml");

        return compiled.Workflows[entrypoint];
    }

    private static JsonObject BuildWorkflowInputs(string task, string? agentName, string correlationId)
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

        if (!string.IsNullOrWhiteSpace(agentName))
            inputs["agent_name"] = agentName;

        return inputs;
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

        var response = await _llm.CallAsync(new LLMRequest
        {
            Model = "gpt-4o-mini",
            Prompt = prompt,
            Temperature = 0.3
        }, ct);

        var raw = response.Text.Trim().Trim('"', '\'', '\u201C', '\u201D');
        if (raw.Length > 60)
            raw = raw[..60].Trim();
        return raw;
    }

    private sealed record AgentWorkflowLoadResult(CompiledWorkflow? Workflow, string? ErrorMessage)
    {
        public static AgentWorkflowLoadResult Success(CompiledWorkflow workflow) => new(workflow, null);
        public static AgentWorkflowLoadResult Fail(string errorMessage) => new(null, errorMessage);
    }
}

