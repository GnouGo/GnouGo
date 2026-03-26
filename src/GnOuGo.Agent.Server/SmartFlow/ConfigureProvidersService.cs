using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Runs the configure-providers-agent workflow to interactively configure
/// LLM providers and MCP servers, persisting secrets to KeyVault via MCP.
/// </summary>
public sealed class ConfigureProvidersService
{
    private readonly ILLMClient _llm;
    private readonly IMcpClientFactory _mcpFactory;
    private readonly IMemoryCache _mcpCache;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly ILogger<ConfigureProvidersService> _logger;
    private readonly string _workflowYaml;

    public ConfigureProvidersService(
        ILLMClient llm,
        IMcpClientFactory mcpFactory,
        IMemoryCache mcpCache,
        AgentHumanInputProvider humanInput,
        ILogger<ConfigureProvidersService> logger)
    {
        _llm = llm;
        _mcpFactory = mcpFactory;
        _mcpCache = mcpCache;
        _humanInput = humanInput;
        _logger = logger;

        // Load the embedded workflow YAML
        var asm = typeof(ConfigureProvidersService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("configure-providers-agent.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException(
                "Embedded resource 'configure-providers-agent.yaml' not found. " +
                "Available: " + string.Join(", ", asm.GetManifestResourceNames()));

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        _workflowYaml = reader.ReadToEnd();
    }

    /// <summary>
    /// Executes the configure-providers workflow with the given slash command.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Parse and compile the workflow
        var doc = WorkflowParser.Parse(_workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException("No entrypoint workflow found in configure-providers-agent.yaml");

        var workflow = compiled.Workflows[entrypoint];

        // Build inputs
        var inputs = new JsonObject { ["command"] = command.Trim() };
        var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

        // Channel for streaming telemetry events
        var channel = Channel.CreateUnbounded<SmartFlowEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var telemetry = new AgentStreamingTelemetry(evt => channel.Writer.TryWrite(evt));

        var engine = new WorkflowEngine
        {
            LLMClient = _llm,
            McpClientFactory = _mcpFactory,
            McpCache = _mcpCache,
            Telemetry = telemetry,
            HumanInputProvider = _humanInput,
            Logger = _logger,
            Limits = new ExecutionLimits
            {
                LogStepContent = true,
                RunId = Guid.NewGuid().ToString("N")
            }
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

        if (result is { Success: false })
        {
            var errMsg = result.Error?.Message ?? "Configuration workflow failed";
            yield return new SmartFlowEvent("error", errMsg);
        }
    }
}

