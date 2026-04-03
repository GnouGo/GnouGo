using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using GnOuGo.Agent.Shared;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Event emitted during a SmartFlow workflow execution for streaming to the UI.
/// </summary>
public sealed record SmartFlowEvent(string Type, string? Text);

/// <summary>
/// Wraps the GnOuGo.Flow workflow engine to execute the dynamic-workflow-agent
/// and stream thinking + response events back to the Blazor UI.
/// </summary>
public sealed class SmartFlowService
{
    private readonly ILLMClient _llm;
    private readonly IMemoryCache _mcpCache;
    private readonly SecureWorkflowRuntimeFactory _runtimeFactory;
    private readonly ConfigureProvidersService _configureProviders;
    private readonly ConfigureAgentsService _configureAgents;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<SmartFlowService> _logger;
    private readonly string _workflowYaml;

    /// <summary>Slash commands that route to the configure-providers workflow.</summary>
    private static readonly string[] ProviderCommands = { "/llm", "/mcp", "/status" };

    public SmartFlowService(
        ILLMClient llm,
        IMemoryCache mcpCache,
        SecureWorkflowRuntimeFactory runtimeFactory,
        ConfigureProvidersService configureProviders,
        ConfigureAgentsService configureAgents,
        AgentOTelTelemetry otel,
        ILogger<SmartFlowService> logger)
    {
        _llm = llm;
        _mcpCache = mcpCache;
        _runtimeFactory = runtimeFactory;
        _configureProviders = configureProviders;
        _configureAgents = configureAgents;
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

    /// <summary>
    /// Executes the dynamic-workflow-agent with the given user task and streams events.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string task,
        [EnumeratorCancellation] CancellationToken ct)
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

        // ── Normal dynamic-workflow-agent flow ──
        // Parse and compile the workflow
        var doc = WorkflowParser.Parse(_workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException("No entrypoint workflow found in dynamic-workflow-agent.yaml");

        var workflow = compiled.Workflows[entrypoint];

        // Build inputs
        var inputs = new JsonObject { ["task"] = task };
        var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

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
            Telemetry = telemetry,
            Logger = _logger,
            Limits = new ExecutionLimits { LogStepContent = true }
        };

        RunResult? result = null;
        Exception? error = null;

        var executionTask = Task.Run(async () =>
        {
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

    /// <summary>
    /// Non-streaming complete: runs the workflow and returns the final answer.
    /// </summary>
    public async Task<string> CompleteAsync(string task, CancellationToken ct)
    {
        string answer = "";
        await foreach (var evt in ExecuteAsync(task, ct))
        {
            if (evt.Type is "answer")
                answer = evt.Text ?? "";
            else if (evt.Type is "error")
                throw new InvalidOperationException(evt.Text);
        }
        return answer;
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
}

