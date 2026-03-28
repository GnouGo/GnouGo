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
/// Also applies the resulting LLM provider config to <see cref="LLMRuntimeOptionsStore"/>
/// so subsequent calls use the updated credentials without a restart.
/// </summary>
public sealed class ConfigureProvidersService
{
    private readonly ILLMClient _llm;
    private readonly IMcpClientFactory _mcpFactory;
    private readonly IMemoryCache _mcpCache;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<ConfigureProvidersService> _logger;
    private readonly string _workflowYaml;

    public ConfigureProvidersService(
        ILLMClient llm,
        IMcpClientFactory mcpFactory,
        IMemoryCache mcpCache,
        AgentHumanInputProvider humanInput,
        LLMRuntimeOptionsStore optionsStore,
        AgentOTelTelemetry otel,
        ILogger<ConfigureProvidersService> logger)
    {
        _llm = llm;
        _mcpFactory = mcpFactory;
        _mcpCache = mcpCache;
        _humanInput = humanInput;
        _optionsStore = optionsStore;
        _otel = otel;
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

        // ── Apply LLM config to the runtime store (no restart needed) ──
        if (result is { Success: true, Outputs: not null })
        {
            var applied = TryApplyLLMConfig(result.Outputs);
            if (applied)
            {
                // Immediately validate the new credentials with a real API call
                await foreach (var evt in ValidateConfigAsync(result.Outputs, ct))
                    yield return evt;
            }
        }

        if (result is { Success: false })
        {
            var errMsg = result.Error?.Message ?? "Configuration workflow failed";
            yield return new SmartFlowEvent("error", errMsg);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool TryApplyLLMConfig(JsonNode outputs)
    {
        try
        {
            var saved = outputs["llm_saved"]?.GetValue<bool>() ?? false;
            if (!saved)
            {
                _logger.LogInformation(
                    "LLM config apply skipped: llm_saved=false. Outputs keys: {Keys}",
                    string.Join(", ", outputs.AsObject().Select(k => k.Key)));
                return false;
            }

            var provider = outputs["llm_provider"]?.GetValue<string>();
            var url = outputs["llm_url"]?.GetValue<string>();
            var model = outputs["llm_model"]?.GetValue<string>();
            var authType = outputs["llm_auth_type"]?.GetValue<string>();
            var apiKey = outputs["llm_api_key"]?.GetValue<string>();
            var oidcIssuer = outputs["llm_oidc_issuer"]?.GetValue<string>();
            var oidcClientId = outputs["llm_oidc_client_id"]?.GetValue<string>();
            var oidcScopes = outputs["llm_oidc_scopes"]?.GetValue<string>();
            var oidcClientSecret = outputs["llm_oidc_client_secret"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning(
                    "LLM config apply skipped: missing provider/url (provider='{Provider}', url='{Url}').",
                    provider ?? "<null>",
                    url ?? "<null>");
                return false;
            }

            _optionsStore.UpdateProvider(
                providerKey: provider,
                url: url,
                model: model ?? "",
                apiKey: apiKey,
                authType: authType,
                oidcIssuer: oidcIssuer,
                oidcClientId: oidcClientId,
                oidcScopes: oidcScopes,
                oidcClientSecret: oidcClientSecret);

            _logger.LogInformation(
                "LLM provider '{Provider}' applied to runtime options (no restart required).", provider);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply LLM config from workflow outputs.");
            return false;
        }
    }

    private async IAsyncEnumerable<SmartFlowEvent> ValidateConfigAsync(
        JsonNode outputs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var provider  = outputs["llm_provider"]?.GetValue<string>() ?? "";
        var model     = outputs["llm_model"]?.GetValue<string>() ?? "";
        var authType  = outputs["llm_auth_type"]?.GetValue<string>() ?? "none";

        // Skip validation for providers that don't require a key
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:info",
                $"✅ Provider '{provider}' saved. No API key required — skipping validation.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:info",
            $"🔍 Validating '{provider}' credentials with a test call…");

        // Run the test call outside try/catch so we can yield the result safely
        string? validationError = null;
        try
        {
            await _llm.CallAsync(new GnOuGo.Flow.Core.Runtime.LLMRequest
            {
                Provider    = provider,
                Model       = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                Prompt      = "Reply with the single word: ok",
                Temperature = 0.0,
            }, ct);
        }
        catch (Exception ex)
        {
            validationError = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized")
                ? "The API key appears to be invalid or revoked."
                : ex.Message.Contains("404") || ex.Message.Contains("model")
                    ? $"Authentication succeeded but model '{model}' was not found. You can change the model with `/llm`."
                    : ex.Message;

            _logger.LogWarning("LLM provider '{Provider}' validation failed: {Error}", provider, ex.Message);
        }

        if (validationError is null)
        {
            yield return new SmartFlowEvent("thinking:response",
                $"✅ Credentials validated. Provider '{provider}' is ready.");
        }
        else
        {
            yield return new SmartFlowEvent("thinking:response",
                $"⚠️ Validation failed for '{provider}': {validationError}\n\nThe configuration was saved — run `/llm` again to correct it.");
        }
    }
}

