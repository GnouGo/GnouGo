using System.Runtime.CompilerServices;
using System.Text;
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
/// Runs the configure-agents-agent workflow to interactively manage agents
/// (create, edit, remove, select) via the /agent slash commands.
/// </summary>
public sealed class ConfigureAgentsService
{
    private readonly ILLMClient _llm;
    private readonly IMcpClientFactory _mcpFactory;
    private readonly IMemoryCache _mcpCache;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<ConfigureAgentsService> _logger;
    private readonly string _workflowYaml;

    public ConfigureAgentsService(
        ILLMClient llm,
        IMcpClientFactory mcpFactory,
        IMemoryCache mcpCache,
        AgentHumanInputProvider humanInput,
        LLMRuntimeOptionsStore optionsStore,
        AgentOTelTelemetry otel,
        ILogger<ConfigureAgentsService> logger)
    {
        _llm = llm;
        _mcpFactory = mcpFactory;
        _mcpCache = mcpCache;
        _humanInput = humanInput;
        _optionsStore = optionsStore;
        _otel = otel;
        _logger = logger;

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
    /// event when the user runs /agent select.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var trimmedCommand = command.Trim();

        if (string.Equals(trimmedCommand, "/agent list", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("answer", await RenderAgentListAsync(ct));
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

        if (string.Equals(trimmedCommand, "/agent add", StringComparison.OrdinalIgnoreCase))
        {
            var selection = await ResolveAgentLlmSelectionAsync(ct);
            if (selection is null)
            {
                yield return new SmartFlowEvent(
                    "answer",
                    "❌ No LLM provider is configured yet. Use `/llm add` first, then retry `/agent add`.");
                yield break;
            }

            inputs["agent_llm_provider"] = selection.Provider;
            inputs["agent_llm_model"] = selection.Model;

            yield return new SmartFlowEvent(
                "thinking:info",
                $"🤖 Using configured LLM provider '{selection.Provider}' with model '{selection.Model}' for agent creation.");
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

        // Check for agent_selected output
        if (result is { Success: true, Outputs: not null })
        {
            var agentName = result.Outputs["agent_selected"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                yield return new SmartFlowEvent("agent_selected", agentName);
            }
        }

        if (result is { Success: false })
        {
            var errMsg = result.Error?.Message ?? "Agent configuration workflow failed";
            yield return new SmartFlowEvent("error", errMsg);
        }
    }

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
            return "# 🤖 Configured Agents\n\nNo agents configured yet. Use `/agent add` to create one.";

        var sb = new StringBuilder();
        sb.AppendLine("# 🤖 Configured Agents");
        sb.AppendLine();
        sb.AppendLine("| Name | ID | Schedules | Updated |");
        sb.AppendLine("|------|----|-----------|---------|");

        foreach (var agent in agents.OfType<JsonObject>().OrderBy(a => a["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var name = agent["name"]?.GetValue<string>() ?? "";
            var id = agent["id"]?.GetValue<string>() ?? "";
            var schedules = (agent["schedules"] as JsonArray)?.Count ?? 0;
            var updated = agent["updated_at"]?.GetValue<string>() ?? "";
            var shortId = id.Length > 8 ? id[..8] : id;
            sb.AppendLine($"| {EscapeMarkdownCell(name)} | `{EscapeBackticks(shortId)}` | {schedules} | {EscapeMarkdownCell(FormatTimestamp(updated))} |");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<AgentLlmSelection?> ResolveAgentLlmSelectionAsync(CancellationToken ct)
    {
        var providers = await LoadConfiguredLlmProvidersAsync(ct);
        if (providers.Count == 0)
            return null;

        var preferred = ResolvePreferredProvider(providers);
        return preferred;
    }

    private async Task<List<AgentLlmSelection>> LoadConfiguredLlmProvidersAsync(CancellationToken ct)
    {
        await using var session = await _mcpFactory.GetClientAsync("GnOuGo.KeyVault.Mcp", ct);
        var listCall = await session.CallToolAsync("keyvault_list_secrets", null, ct);

        if (listCall.IsError)
            throw new InvalidOperationException("KeyVault list_secrets MCP call failed.");

        var payload = listCall.Content as JsonObject
            ?? throw new InvalidOperationException("Unexpected KeyVault MCP response shape.");

        var success = payload["Success"]?.GetValue<bool>() ?? payload["success"]?.GetValue<bool>() ?? false;
        if (!success)
        {
            var error = payload["Error"]?.GetValue<string>() ?? payload["error"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"KeyVault list_secrets failed: {error}");
        }

        var secrets = payload["Data"] as JsonArray ?? payload["data"] as JsonArray ?? [];
        var providerKeys = secrets
            .OfType<JsonObject>()
            .Select(item => item["Key"]?.GetValue<string>() ?? item["key"]?.GetValue<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.StartsWith("gnougo_llm_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selections = new List<AgentLlmSelection>(providerKeys.Count);
        foreach (var secretKey in providerKeys)
        {
            if (string.IsNullOrWhiteSpace(secretKey))
                continue;

            var getCall = await session.CallToolAsync(
                "keyvault_get_secret",
                new JsonObject
                {
                    ["key"] = secretKey,
                    ["author"] = "GnOuGo.Agent"
                },
                ct);

            if (getCall.IsError || getCall.Content is not JsonObject getPayload)
                continue;

            var getSuccess = getPayload["Success"]?.GetValue<bool>() ?? getPayload["success"]?.GetValue<bool>() ?? false;
            if (!getSuccess)
                continue;

            var secretValue = getPayload["Data"]?["Value"]?.GetValue<string>()
                ?? getPayload["data"]?["value"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(secretValue))
                continue;

            JsonObject? config = null;
            try { config = JsonNode.Parse(secretValue) as JsonObject; }
            catch { }

            var normalizedSecretKey = secretKey;
            var provider = config?["provider"]?.GetValue<string>()
                ?? normalizedSecretKey["gnougo_llm_".Length..];

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

    private AgentLlmSelection ResolvePreferredProvider(IReadOnlyList<AgentLlmSelection> providers)
    {
        var defaultProvider = _optionsStore.Current.DefaultProvider;
        if (!string.IsNullOrWhiteSpace(defaultProvider))
        {
            var preferred = providers.FirstOrDefault(p =>
                string.Equals(p.Provider, defaultProvider, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        return providers
            .OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase)
            .First();
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

