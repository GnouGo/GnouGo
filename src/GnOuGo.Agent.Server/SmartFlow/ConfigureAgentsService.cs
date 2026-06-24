using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Runs the configure-agents-agent workflow to interactively manage agents
/// (create, edit, remove, select) via the /gnougo slash commands.
/// </summary>
public sealed class ConfigureAgentsService
{
    private readonly ILLMClient _llm;
    private readonly IMcpClientFactory _mcpFactory;
    private readonly IMemoryCache _mcpCache;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly IKeyVaultRuntimeConfigStore _keyVaultStore;
    private readonly SecureWorkflowRuntimeFactory _runtimeFactory;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentUserConfigMcpClient? _userConfigClient;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<ConfigureAgentsService> _logger;
    private readonly string _workflowYaml;
    private readonly TimeSpan _mcpCacheSlidingExpiration;

    public ConfigureAgentsService(
        ILLMClient llm,
        IMcpClientFactory mcpFactory,
        IMemoryCache mcpCache,
        AgentHumanInputProvider humanInput,
        IKeyVaultRuntimeConfigStore keyVaultStore,
        SecureWorkflowRuntimeFactory runtimeFactory,
        LLMRuntimeOptionsStore optionsStore,
        AgentOTelTelemetry otel,
        ILogger<ConfigureAgentsService> logger,
        AgentUserConfigMcpClient? userConfigClient = null,
        IOptions<McpCapabilityCacheSettings>? mcpCapabilityCacheSettings = null)
    {
        _llm = llm;
        _mcpFactory = mcpFactory;
        _mcpCache = mcpCache;
        _humanInput = humanInput;
        _keyVaultStore = keyVaultStore;
        _runtimeFactory = runtimeFactory;
        _optionsStore = optionsStore;
        _userConfigClient = userConfigClient;
        _otel = otel;
        _logger = logger;
        _mcpCacheSlidingExpiration = (mcpCapabilityCacheSettings?.Value ?? new McpCapabilityCacheSettings()).SlidingExpiration;

        // Load the embedded workflow YAML
        var asm = typeof(ConfigureAgentsService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("configure-agents-agent.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException(
                "Embedded resource 'configure-agents-agent.yaml' not found. " +
                "Available: " + string.Join(", ", asm.GetManifestResourceNames()));

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        _workflowYaml = reader.ReadToEnd();
    }

    /// <summary>
    /// Executes the configure-agents workflow with the given slash command.
    /// Yields <see cref="SmartFlowEvent"/> including a special "agent_selected"
    /// event when the user runs /gnougo select.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var trimmedCommand = command.Trim();

        // Wrap the whole /gnougo command in a dedicated trace span so that workflow spans,
        // GenAI llm.call spans and MCP calls all appear under a single, well-named parent
        // — mirroring what ConfigureProvidersService does for /llm, /mcp, /status.
        var descriptor = DescribeCommand(trimmedCommand);
        using var commandTrace = _otel.StartActivityScope(descriptor.SpanName);
        commandTrace.SetTag("gnougo.agent.command.route", "configure_agents");
        commandTrace.SetTag("gnougo.agent.command.name", trimmedCommand);
        commandTrace.SetTag("gnougo.agent.command.mode", descriptor.Mode);
        if (!string.IsNullOrWhiteSpace(descriptor.Action))
            commandTrace.SetTag("gnougo.agent.command.action", descriptor.Action);
        if (!string.IsNullOrWhiteSpace(descriptor.Argument))
            commandTrace.SetTag("gnougo.agent.command.argument", descriptor.Argument);

        // Capture the command activity so we can restore it inside Task.Run continuations
        // (thread-pool threads do not inherit Activity.Current from the calling async flow).
        var parentActivity = commandTrace.Activity;

        if (string.Equals(trimmedCommand, "/gnougo list", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("answer", await RenderAgentListAsync(ct));
            commandTrace.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        // Parse and compile the workflow
        var doc = WorkflowParser.Parse(_workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException("No entrypoint workflow found in configure-agents-agent.yaml");

        var workflow = compiled.Workflows[entrypoint];

        // Build inputs
        var inputs = new JsonObject { ["command"] = trimmedCommand };

        if (string.Equals(trimmedCommand, "/gnougo add", StringComparison.OrdinalIgnoreCase))
        {
            var selection = await ResolveAgentLlmSelectionAsync(ct);
            if (selection is null)
            {
                yield return new SmartFlowEvent(
                    "answer",
                    "❌ Configure a default LLM provider first. Use `/llm add` to create one, then `/llm default` before retrying `/gnougo add`." );
                yield break;
            }

            inputs["agent_llm_provider"] = selection.Provider;
            inputs["agent_llm_model"] = selection.Model;

            yield return new SmartFlowEvent(
                "thinking:info",
                $"🤖 Using default LLM provider '{selection.Provider}' with model '{selection.Model}' for agent creation.");
        }

        var resolvedInputs = WorkflowInputDefaults.Apply(workflow.Source, inputs);

        // Channel for streaming telemetry events
        var channel = Channel.CreateUnbounded<SmartFlowEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

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
            McpCacheSlidingExpiration = _mcpCacheSlidingExpiration,
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
            // Restore the command activity as Activity.Current on the thread-pool thread so that
            // downstream instrumentation (ILLMClient GenAI spans, MCP calls, workflow steps) is
            // correctly parented to the /gnougo command span — and therefore to the chat trace.
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
            commandTrace.SetStatus(ActivityStatusCode.Error, error.Message);
            commandTrace.SetTag("error.type", error.GetType().FullName);
            commandTrace.SetTag("error.message", error.Message);
            yield return new SmartFlowEvent("error", error.Message);
            yield break;
        }

        // Check for agent_selected output
        if (result is { Success: true, Outputs: not null })
        {
            var agentName = result.Outputs["agent_selected"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                if (_userConfigClient is not null)
                    await _userConfigClient.SetAsync(defaultAgent: agentName, ct: ct);

                yield return new SmartFlowEvent("agent_selected", agentName);
            }
        }

        if (result is { Success: false })
        {
            var errMsg = result.Error?.Message ?? "Agent configuration workflow failed";
            commandTrace.SetStatus(ActivityStatusCode.Error, errMsg);
            yield return new SmartFlowEvent("error", errMsg);
        }
        else
        {
            commandTrace.SetStatus(ActivityStatusCode.Ok);
        }
    }

    private static CommandTraceDescriptor DescribeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new CommandTraceDescriptor("configure.agents.unknown", null, null, "direct");

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // parts[0] == "/gnougo"
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";
        var argument = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;
        var mode = action is "add" or "edit" or "reprompt" or "remove" or "select" ? "interactive" : "direct";
        var spanName = $"configure.agents.{action}";
        return new CommandTraceDescriptor(spanName, action, argument, mode);
    }

    private sealed record CommandTraceDescriptor(string SpanName, string? Action, string? Argument, string Mode);

    private async Task<string> RenderAgentListAsync(CancellationToken ct)
    {
        await using var session = await _mcpFactory.GetClientAsync("GnOuGo.Agent.Mcp", ct);
        var call = await session.CallToolAsync("agent_list", null, ct);

        if (call.IsError)
            throw new InvalidOperationException("Agent list MCP call failed.");

        var response = call.Content as JsonObject
            ?? throw new InvalidOperationException("Unexpected Agent MCP response shape.");

        var success = response["success"]?.GetValue<bool>() ?? false;
        if (!success)
        {
            var error = response["error_message"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"agent_list failed: {error}");
        }

        var agents = response["agents"] as JsonArray ?? [];
        if (agents.Count == 0)
            return "# 🤖 Configured Agents\n\nNo agents configured yet. Use `/gnougo add` to create one.";

        var sb = new StringBuilder();
        sb.AppendLine("# 🤖 Configured Agents");
        sb.AppendLine();
        sb.AppendLine("| Name | ID | Updated |");
        sb.AppendLine("|------|----|---------|");

        foreach (var agent in agents.OfType<JsonObject>().OrderBy(a => a["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var name = agent["name"]?.GetValue<string>() ?? "";
            var id = agent["id"]?.GetValue<string>() ?? "";
            var updated = agent["updated_at"]?.GetValue<string>() ?? "";
            var shortId = id.Length > 8 ? id[..8] : id;
            sb.AppendLine($"| {EscapeMarkdownCell(name)} | `{EscapeBackticks(shortId)}` | {EscapeMarkdownCell(FormatTimestamp(updated))} |");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<AgentLlmSelection?> ResolveAgentLlmSelectionAsync(CancellationToken ct)
    {
        var providers = await LoadConfiguredLlmProvidersAsync(ct);
        if (providers.Count == 0)
            return null;

        var defaultProvider = _optionsStore.Current.DefaultProvider;
        if (string.IsNullOrWhiteSpace(defaultProvider))
            return null;

        return providers.FirstOrDefault(p =>
            string.Equals(p.Provider, defaultProvider, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<AgentLlmSelection>> LoadConfiguredLlmProvidersAsync(CancellationToken ct)
    {
        var secrets = await _keyVaultStore.ListSecretsAsync(ct);
        var providerSecrets = KeyVaultConfigNaming.SelectPreferredSecrets(secrets, KeyVaultConfigSecretKind.LlmProvider);

        var selections = new List<AgentLlmSelection>(providerSecrets.Count);
        foreach (var secretKey in providerSecrets.Select(secret => secret.Key))
        {
            if (string.IsNullOrWhiteSpace(secretKey))
                continue;

            var secretValue = await _keyVaultStore.GetSecretValueAsync(secretKey, ct);

            if (string.IsNullOrWhiteSpace(secretValue))
                continue;

            var normalizedSecretKey = secretKey;
            JsonObject? config = null;
            try { config = JsonNode.Parse(secretValue) as JsonObject; }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Stored LLM provider secret '{SecretKey}' is not valid JSON; falling back to key-based provider resolution.", normalizedSecretKey);
            }

            var provider = config?["provider"]?.GetValue<string>()
                ?? KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.LlmProvider, normalizedSecretKey)
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(provider))
                continue;

            var model = config?["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = string.Equals(provider, _optionsStore.Current.DefaultProvider, StringComparison.OrdinalIgnoreCase)
                    ? _optionsStore.Current.DefaultModel
                    : provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                        ? "llama3"
                        : "gpt-4o-mini";
            }

            selections.Add(new AgentLlmSelection(provider, model!, normalizedSecretKey));
        }

        return selections;
    }


    private static string FormatTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var dto)
            ? dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;

    private static string EscapeMarkdownCell(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string EscapeBackticks(string value)
        => value.Replace("`", "\\`", StringComparison.Ordinal);

    private sealed record AgentLlmSelection(string Provider, string Model, string SecretKey);
}
