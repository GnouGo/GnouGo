using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    private readonly ILLMModelCatalog _modelCatalog;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<ConfigureProvidersService> _logger;
    private readonly string _workflowYaml;

    public ConfigureProvidersService(
        ILLMClient llm,
        IMcpClientFactory mcpFactory,
        IMemoryCache mcpCache,
        AgentHumanInputProvider humanInput,
        ILLMModelCatalog modelCatalog,
        LLMRuntimeOptionsStore optionsStore,
        AgentOTelTelemetry otel,
        ILogger<ConfigureProvidersService> logger)
    {
        _llm = llm;
        _mcpFactory = mcpFactory;
        _mcpCache = mcpCache;
        _humanInput = humanInput;
        _modelCatalog = modelCatalog;
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
        var trimmedCommand = command.Trim();

        var directResponse = await TryHandleWithoutLlmAsync(trimmedCommand, ct);
        if (directResponse is not null)
        {
            yield return new SmartFlowEvent("answer", directResponse);
            yield break;
        }

        if (string.Equals(trimmedCommand, "/llm add", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var evt in ExecuteInteractiveLlmAddAsync(ct))
                yield return evt;
            yield break;
        }

        const string llmEditPrefix = "/llm edit";
        if (trimmedCommand.StartsWith(llmEditPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > llmEditPrefix.Length)
        {
            var requestedProvider = trimmedCommand[llmEditPrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveLlmEditAsync(requestedProvider, ct))
                yield return evt;
            yield break;
        }

        // Parse and compile the workflow
        var doc = WorkflowParser.Parse(_workflowYaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint is null || !compiled.Workflows.ContainsKey(entrypoint))
            throw new InvalidOperationException("No entrypoint workflow found in configure-providers-agent.yaml");

        var workflow = compiled.Workflows[entrypoint];

        // Build inputs
        var inputs = new JsonObject { ["command"] = trimmedCommand };
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

    private async Task<string?> TryHandleWithoutLlmAsync(string command, CancellationToken ct)
    {
        if (string.Equals(command, "/llm list", StringComparison.OrdinalIgnoreCase))
        {
            var secrets = await LoadKeyVaultSecretsAsync(ct);
            return RenderSecretTable(
                secrets,
                prefix: "gnougo_llm_",
                title: "# 🤖 Configured LLM Providers",
                emptyMessage: "No LLM providers configured yet. Use `/llm add` to get started.");
        }

        if (string.Equals(command, "/mcp list", StringComparison.OrdinalIgnoreCase))
        {
            var secrets = await LoadKeyVaultSecretsAsync(ct);
            return RenderSecretTable(
                secrets,
                prefix: "gnougo_mcp_",
                title: "# 🔌 Configured MCP Servers",
                emptyMessage: "No MCP servers configured yet. Use `/mcp add` to get started.");
        }

        if (string.Equals(command, "/status", StringComparison.OrdinalIgnoreCase))
        {
            var secrets = await LoadKeyVaultSecretsAsync(ct);
            return RenderStatus(secrets);
        }

        const string modelsPrefix = "/llm models";
        if (command.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var requestedProvider = command.Length > modelsPrefix.Length
                ? command[modelsPrefix.Length..].Trim()
                : string.Empty;

            var resolvedProvider = ResolveConfiguredProviderKey(requestedProvider);
            if (resolvedProvider is null)
                return RenderModelsUsage(requestedProvider);

            var models = await _modelCatalog.ListModelsAsync(resolvedProvider, ct);
            return RenderModelCatalog(resolvedProvider, models);
        }

        return null;
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveLlmAddAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SmartFlowEvent("thinking:thinking", "🤖 Starting LLM provider configuration…");

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;

        var providerRequest = CreateChoiceRequest(runId, "llm_add.provider", "Select the LLM provider to configure:", ["openai", "ollama", "copilot"]);
        await foreach (var evt in EmitHumanInputRequestAsync(providerRequest, r => response = r, ct))
            yield return evt;

        var provider = ReadChoiceResponse(response);
        if (string.IsNullOrWhiteSpace(provider))
            yield break;

        var defaults = GetProviderDefaults(provider);
        response = null;

        var connectionRequest = CreateFieldsRequest(
            runId,
            "llm_add.connection",
            $"Configure {provider} connection:",
            [
                new HumanInputFieldDef
                {
                    Name = "url",
                    Type = "string",
                    Required = true,
                    Description = "Provider endpoint URL",
                    Default = defaults.Url
                }
            ],
            JsonValue.Create($"Default URL: {defaults.Url}"));

        await foreach (var evt in EmitHumanInputRequestAsync(connectionRequest, r => response = r, ct))
            yield return evt;

        var connection = ReadFieldResponse(response, connectionRequest.Fields!);
        var url = connection.GetValueOrDefault("url") ?? defaults.Url;

        string authType;
        if (defaults.AuthModes.Count == 1)
        {
            authType = defaults.AuthModes[0];
        }
        else
        {
            response = null;
            var authRequest = CreateChoiceRequest(
                runId,
                "llm_add.auth_mode",
                $"Choose authentication mode for {provider}:",
                defaults.AuthModes,
                JsonValue.Create($"Available: {string.Join(", ", defaults.AuthModes)}"));
            await foreach (var evt in EmitHumanInputRequestAsync(authRequest, r => response = r, ct))
                yield return evt;
            authType = ReadChoiceResponse(response) ?? defaults.AuthModes[0];
        }

        LlmProviderConfig? auth = null;
        await foreach (var evt in CollectAuthConfigAsync(runId, provider, authType, null, cfg => auth = cfg, ct))
            yield return evt;

        if (auth is null)
            yield break;

        string? model = null;
        await foreach (var evt in CollectModelSelectionAsync(runId, provider, defaults.Model, BuildProviderOptions(provider, url, auth), selected => model = selected, ct))
            yield return evt;

        if (string.IsNullOrWhiteSpace(model))
            yield break;

        var summary = RenderLlmConfigSummary(provider, url, model, auth);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        var alreadyExists = await LoadLlmProviderConfigAsync(provider, ct) is not null;
        response = null;
        var confirmPrompt = alreadyExists
            ? "⚠️ A configuration for this provider already exists. Overwrite?"
            : "Save this LLM provider configuration?";
        var confirmRequest = CreateChoiceRequest(runId, "llm_add.confirm_save", confirmPrompt, ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ LLM configuration discarded.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:progress", "💾 Saving LLM provider configuration to KeyVault…");
        await SaveLlmProviderConfigAsync(provider, url, model, auth, ct);
        yield return new SmartFlowEvent("answer", $"✅ LLM provider '{provider}' saved to KeyVault as `gnougo_llm_{provider}`.");

        var outputs = BuildSavedLlmOutputs(provider, url, model, auth);
        TryApplyLLMConfig(outputs);
        await foreach (var evt in ValidateConfigAsync(outputs, ct))
            yield return evt;
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveLlmEditAsync(
        string requestedProvider,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = ResolveConfiguredProviderKey(requestedProvider) ?? requestedProvider.Trim();
        if (string.IsNullOrWhiteSpace(provider))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the provider to edit: `/llm edit <provider>`." );
            yield break;
        }

        yield return new SmartFlowEvent("thinking:thinking", $"🔍 Loading LLM provider '{provider}' from KeyVault…");
        var existing = await LoadLlmProviderConfigAsync(provider, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ LLM provider '{requestedProvider}' not found. Use `/llm list` to see available providers.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;

        var connectionRequest = CreateFieldsRequest(
            runId,
            "llm_edit.connection",
            $"Edit LLM provider '{provider}':",
            [
                new HumanInputFieldDef
                {
                    Name = "url",
                    Type = "string",
                    Required = true,
                    Description = "Provider endpoint URL",
                    Default = existing.Url
                }
            ]);
        await foreach (var evt in EmitHumanInputRequestAsync(connectionRequest, r => response = r, ct))
            yield return evt;
        var connection = ReadFieldResponse(response, connectionRequest.Fields!);
        var url = connection.GetValueOrDefault("url") ?? existing.Url;

        var authModeChoices = new List<string> { "keep_current" };
        foreach (var authMode in GetProviderDefaults(provider).AuthModes)
        {
            if (!authModeChoices.Any(choice => string.Equals(choice, authMode, StringComparison.OrdinalIgnoreCase)))
                authModeChoices.Add(authMode);
        }

        response = null;
        var authModeRequest = CreateChoiceRequest(
            runId,
            "llm_edit.auth_mode",
            $"Choose authentication mode for '{provider}':",
            authModeChoices,
            JsonValue.Create($"Current: {existing.AuthType}"));
        await foreach (var evt in EmitHumanInputRequestAsync(authModeRequest, r => response = r, ct))
            yield return evt;

        var authSelection = ReadChoiceResponse(response) ?? "keep_current";
        LlmProviderConfig? auth = existing;
        if (!string.Equals(authSelection, "keep_current", StringComparison.OrdinalIgnoreCase))
        {
            auth = null;
            await foreach (var evt in CollectAuthConfigAsync(runId, provider, authSelection, existing, cfg => auth = cfg, ct))
                yield return evt;
        }

        if (auth is null)
            yield break;

        string? model = null;
        await foreach (var evt in CollectModelSelectionAsync(runId, provider, existing.Model, BuildProviderOptions(provider, url, auth), selected => model = selected, ct))
            yield return evt;

        if (string.IsNullOrWhiteSpace(model))
            yield break;

        var summary = RenderLlmConfigSummary(provider, url, model, auth);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        response = null;
        var confirmRequest = CreateChoiceRequest(runId, "llm_edit.confirm_save", $"Save updated LLM provider '{provider}'?", ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Edit discarded.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:progress", "💾 Saving LLM provider configuration to KeyVault…");
        await SaveLlmProviderConfigAsync(provider, url, model, auth, ct);
        yield return new SmartFlowEvent("answer", $"✅ LLM provider '{provider}' updated.");

        var outputs = BuildSavedLlmOutputs(provider, url, model, auth);
        TryApplyLLMConfig(outputs);
        await foreach (var evt in ValidateConfigAsync(outputs, ct))
            yield return evt;
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectModelSelectionAsync(
        string runId,
        string provider,
        string defaultModel,
        ModelProviderOptions providerOptions,
        Action<string?> setModel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyList<LLMModelDescriptor>? models = null;
        try
        {
            models = await DiscoverModelsAsync(provider, providerOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Model discovery failed for provider '{Provider}', falling back to manual model entry.", provider);
        }

        if (models is { Count: > 0 })
        {
            JsonNode? response = null;
            var options = models.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            var request = CreateFieldsRequest(
                runId,
                $"llm_model.select.{provider}",
                $"Select the model for {provider}:",
                [
                    new HumanInputFieldDef
                    {
                        Name = "model",
                        Type = "select",
                        Required = true,
                        Description = "Available models returned by the provider API",
                        Options = options,
                        Default = options.Any(x => string.Equals(x, defaultModel, StringComparison.OrdinalIgnoreCase)) ? defaultModel : options[0]
                    }
                ],
                JsonValue.Create($"{models.Count} model(s) available."));
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            setModel(ReadFieldResponse(response, request.Fields!).GetValueOrDefault("model"));
            yield break;
        }

        JsonNode? fallbackResponse = null;
        var fallbackRequest = CreateFieldsRequest(
            runId,
            $"llm_model.manual.{provider}",
            $"Enter the default model for {provider}:",
            [
                new HumanInputFieldDef
                {
                    Name = "model",
                    Type = "string",
                    Required = true,
                    Description = "Default model name",
                    Default = defaultModel
                }
            ],
            JsonValue.Create("Live model discovery was unavailable, so please enter the model manually."));
        await foreach (var evt in EmitHumanInputRequestAsync(fallbackRequest, r => fallbackResponse = r, ct))
            yield return evt;
        setModel(ReadFieldResponse(fallbackResponse, fallbackRequest.Fields!).GetValueOrDefault("model"));
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectAuthConfigAsync(
        string runId,
        string provider,
        string authType,
        LlmProviderConfig? existing,
        Action<LlmProviderConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
        {
            setConfig(new LlmProviderConfig(provider, existing?.Url ?? "", existing?.Model ?? "", authType, "", "", "", "", ""));
            yield break;
        }

        JsonNode? response = null;
        if (string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(
                runId,
                "llm.auth.api_key",
                existing is null ? "Enter the API key:" : "Enter the new API key:",
                [
                    new HumanInputFieldDef
                    {
                        Name = "api_key",
                        Type = "string",
                        Required = true,
                        Description = "API key (stored encrypted in KeyVault)"
                    }
                ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var apiKey = ReadFieldResponse(response, request.Fields!).GetValueOrDefault("api_key") ?? "";
            setConfig(new LlmProviderConfig(provider, existing?.Url ?? "", existing?.Model ?? "", "api_key", apiKey, "", "", "", ""));
            yield break;
        }

        if (string.Equals(authType, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(
                runId,
                "llm.auth.oidc",
                existing is null ? "Configure OpenID Connect (client_credentials):" : "Configure OIDC:",
                [
                    new HumanInputFieldDef { Name = "issuer", Type = "string", Required = true, Description = "OIDC Issuer URL", Default = existing?.OidcIssuer ?? "" },
                    new HumanInputFieldDef { Name = "client_id", Type = "string", Required = true, Description = "OAuth2 Client ID", Default = existing?.OidcClientId ?? "" },
                    new HumanInputFieldDef { Name = "scopes", Type = "string", Required = true, Description = "Space-separated scopes", Default = existing?.OidcScopes ?? "" },
                    new HumanInputFieldDef { Name = "client_secret", Type = "string", Required = false, Description = "Client secret" }
                ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            setConfig(new LlmProviderConfig(
                provider,
                existing?.Url ?? "",
                existing?.Model ?? "",
                "oidc",
                "",
                fields.GetValueOrDefault("issuer") ?? "",
                fields.GetValueOrDefault("client_id") ?? "",
                fields.GetValueOrDefault("scopes") ?? "",
                fields.GetValueOrDefault("client_secret") ?? ""));
            yield break;
        }

        setConfig(new LlmProviderConfig(provider, existing?.Url ?? "", existing?.Model ?? "", authType, "", "", "", "", ""));
    }

    private static HumanInputRequest CreateChoiceRequest(string runId, string stepId, string prompt, IReadOnlyList<string> choices, JsonNode? context = null)
        => new()
        {
            RunId = runId,
            StepId = stepId,
            Prompt = prompt,
            Choices = choices.ToList(),
            Context = context,
            TimeoutMs = 300_000
        };

    private static HumanInputRequest CreateFieldsRequest(string runId, string stepId, string prompt, IReadOnlyList<HumanInputFieldDef> fields, JsonNode? context = null)
        => new()
        {
            RunId = runId,
            StepId = stepId,
            Prompt = prompt,
            Fields = fields.ToList(),
            Context = context,
            TimeoutMs = 300_000
        };

    private async IAsyncEnumerable<SmartFlowEvent> EmitHumanInputRequestAsync(
        HumanInputRequest request,
        Action<JsonNode?> captureResponse,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SmartFlowEvent("human_input_request", BuildHumanInputPayload(request).ToJsonString());
        captureResponse(await _humanInput.RequestInputAsync(request, ct));
    }

    private static JsonNode BuildHumanInputPayload(HumanInputRequest request)
        => new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["run_id"] = request.RunId,
            ["step_id"] = request.StepId,
            ["timeout_ms"] = request.TimeoutMs,
            ["context"] = request.Context?.DeepClone(),
            ["choices"] = request.Choices is null ? null : new JsonArray(request.Choices.Select(choice => (JsonNode?)JsonValue.Create(choice)).ToArray()),
            ["fields"] = request.Fields is null
                ? null
                : new JsonArray(request.Fields.Select(field => new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type,
                    ["required"] = field.Required,
                    ["description"] = field.Description,
                    ["default"] = field.Default,
                    ["options"] = field.Options is null ? null : new JsonArray(field.Options.Select(option => (JsonNode?)JsonValue.Create(option)).ToArray())
                }).ToArray())
        };

    private static string? ReadChoiceResponse(JsonNode? response)
        => response?["response"]?.GetValue<string>();

    private static Dictionary<string, string> ReadFieldResponse(JsonNode? response, IReadOnlyList<HumanInputFieldDef> fields)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obj = response as JsonObject;
        foreach (var field in fields)
            result[field.Name] = obj?[field.Name]?.GetValue<string>() ?? field.Default ?? "";
        return result;
    }

    private async Task<IReadOnlyList<LLMModelDescriptor>> DiscoverModelsAsync(string provider, ModelProviderOptions providerOptions, CancellationToken ct)
    {
        try
        {
            return await _modelCatalog.ListModelsAsync(provider, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Injected model catalog could not list models for '{Provider}', trying temporary provider settings.", provider);
        }

        using var http = new HttpClient();
        var options = new LLMOptions
        {
            DefaultProvider = provider,
            DefaultModel = ""
        };
        options.Models[provider] = providerOptions;
        var catalog = new RoutingLLMModelCatalog(http, options);
        return await catalog.ListModelsAsync(provider, ct);
    }

    private static ProviderDefaults GetProviderDefaults(string provider)
        => provider.ToLowerInvariant() switch
        {
            "ollama" => new ProviderDefaults("http://localhost:11434", "llama3", ["none"]),
            "copilot" => new ProviderDefaults("https://models.github.ai/inference", "gpt-4o", ["api_key", "copilot_env", "oidc"]),
            _ => new ProviderDefaults("https://api.openai.com/v1", "gpt-4o", ["api_key", "oidc"])
        };

    private static ModelProviderOptions BuildProviderOptions(string provider, string url, LlmProviderConfig auth)
        => new()
        {
            Url = url,
            Type = provider,
            ApiKey = string.Equals(auth.AuthType, "api_key", StringComparison.OrdinalIgnoreCase) ? auth.ApiKey : null,
            Issuer = auth.OidcIssuer,
            ClientId = auth.OidcClientId,
            Scopes = auth.OidcScopes,
            ClientSecret = auth.OidcClientSecret
        };

    private async Task SaveLlmProviderConfigAsync(string provider, string url, string model, LlmProviderConfig auth, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["provider"] = provider,
            ["url"] = url,
            ["model"] = model,
            ["auth_type"] = auth.AuthType,
            ["api_key"] = auth.ApiKey,
            ["oidc_issuer"] = auth.OidcIssuer,
            ["oidc_client_id"] = auth.OidcClientId,
            ["oidc_scopes"] = auth.OidcScopes,
            ["oidc_client_secret"] = auth.OidcClientSecret
        };

        await using var session = await _mcpFactory.GetClientAsync("GnOuGo.KeyVault.Mcp", ct);
        var result = await session.CallToolAsync(
            "keyvault_set_secret",
            new JsonObject
            {
                ["key"] = $"gnougo_llm_{provider}",
                ["value"] = payload.ToJsonString(),
                ["author"] = "GnOuGo.Agent"
            },
            ct);

        if (result.IsError)
            throw new InvalidOperationException("KeyVault set_secret MCP call failed.");
    }

    private async Task<LlmProviderConfig?> LoadLlmProviderConfigAsync(string provider, CancellationToken ct)
    {
        try
        {
            await using var session = await _mcpFactory.GetClientAsync("GnOuGo.KeyVault.Mcp", ct);
            var result = await session.CallToolAsync(
                "keyvault_get_secret",
                new JsonObject
                {
                    ["key"] = $"gnougo_llm_{provider}",
                    ["author"] = "GnOuGo.Agent"
                },
                ct);

            if (result.IsError || result.Content is not JsonObject payload)
                return null;

            var value = payload["Data"]?["Value"]?.GetValue<string>()
                ?? payload["data"]?["value"]?.GetValue<string>()
                ?? payload["Value"]?.GetValue<string>()
                ?? payload["value"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(value))
                return null;

            var config = JsonNode.Parse(value) as JsonObject;
            if (config is null)
                return null;

            return new LlmProviderConfig(
                Provider: config["provider"]?.GetValue<string>() ?? provider,
                Url: config["url"]?.GetValue<string>() ?? "",
                Model: config["model"]?.GetValue<string>() ?? "",
                AuthType: config["auth_type"]?.GetValue<string>() ?? "none",
                ApiKey: config["api_key"]?.GetValue<string>() ?? "",
                OidcIssuer: config["oidc_issuer"]?.GetValue<string>() ?? "",
                OidcClientId: config["oidc_client_id"]?.GetValue<string>() ?? "",
                OidcScopes: config["oidc_scopes"]?.GetValue<string>() ?? "",
                OidcClientSecret: config["oidc_client_secret"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex)
            {
            _logger.LogDebug(ex, "Could not load existing LLM config for provider '{Provider}'.", provider);
            return null;
        }
    }

    private static JsonObject BuildSavedLlmOutputs(string provider, string url, string model, LlmProviderConfig auth)
        => new()
        {
            ["llm_saved"] = true,
            ["llm_provider"] = provider,
            ["llm_url"] = url,
            ["llm_model"] = model,
            ["llm_auth_type"] = auth.AuthType,
            ["llm_api_key"] = auth.ApiKey,
            ["llm_oidc_issuer"] = auth.OidcIssuer,
            ["llm_oidc_client_id"] = auth.OidcClientId,
            ["llm_oidc_scopes"] = auth.OidcScopes,
            ["llm_oidc_client_secret"] = auth.OidcClientSecret
        };

    private static string RenderLlmConfigSummary(string provider, string url, string model, LlmProviderConfig auth)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📋 Review before saving");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Provider | {EscapeMarkdownCell(provider)} |");
        sb.AppendLine($"| URL | {EscapeMarkdownCell(url)} |");
        sb.AppendLine($"| Model | {EscapeMarkdownCell(model)} |");
        sb.AppendLine($"| Auth | {EscapeMarkdownCell(auth.AuthType)} |");
        if (!string.IsNullOrWhiteSpace(auth.ApiKey))
            sb.AppendLine("| API Key | •••••••• |");
        if (string.Equals(auth.AuthType, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| OIDC Issuer | {EscapeMarkdownCell(auth.OidcIssuer)} |");
            sb.AppendLine($"| OIDC Client ID | {EscapeMarkdownCell(auth.OidcClientId)} |");
            sb.AppendLine($"| OIDC Scopes | {EscapeMarkdownCell(auth.OidcScopes)} |");
            sb.AppendLine($"| OIDC Secret | {(string.IsNullOrWhiteSpace(auth.OidcClientSecret) ? "(none)" : "••••••••")} |");
        }
        sb.AppendLine();
        sb.Append($"Configuration will be saved as key: `gnougo_llm_{provider}`");
        return sb.ToString();
    }

    private sealed record ProviderDefaults(string Url, string Model, IReadOnlyList<string> AuthModes);
    private sealed record LlmProviderConfig(string Provider, string Url, string Model, string AuthType, string ApiKey, string OidcIssuer, string OidcClientId, string OidcScopes, string OidcClientSecret);

    private string? ResolveConfiguredProviderKey(string requestedProvider)
    {
        var configuredProviders = _optionsStore.Current.Models.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configuredProviders.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(requestedProvider))
        {
            if (configuredProviders.Count == 1)
                return configuredProviders[0];

            var defaultProvider = _optionsStore.Current.DefaultProvider;
            return configuredProviders.FirstOrDefault(k =>
                       string.Equals(k, defaultProvider, StringComparison.OrdinalIgnoreCase))
                   ?? null;
        }

        return configuredProviders.FirstOrDefault(k =>
            string.Equals(k, requestedProvider, StringComparison.OrdinalIgnoreCase));
    }

    private string RenderModelsUsage(string requestedProvider)
    {
        var configuredProviders = _optionsStore.Current.Models.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        if (configuredProviders.Count == 0)
        {
            sb.AppendLine("❌ No configured LLM provider is available yet. Use `/llm add` first.");
            return sb.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(requestedProvider))
            sb.AppendLine($"❌ LLM provider '{requestedProvider}' is not configured.");
        else
            sb.AppendLine("ℹ️ Please specify the provider: `/llm models <provider>`.");

        sb.AppendLine();
        sb.AppendLine("Configured providers:");
        foreach (var provider in configuredProviders)
            sb.AppendLine($"- `{provider}`");

        return sb.ToString().TrimEnd();
    }

    private static string RenderModelCatalog(string provider, IReadOnlyList<LLMModelDescriptor> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 🧠 Available Models for `{provider}`");
        sb.AppendLine();

        if (models.Count == 0)
        {
            sb.AppendLine("No models were returned by the provider.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("| Model | Display name | Owner |");
        sb.AppendLine("|-------|--------------|-------|");
        foreach (var model in models)
        {
            sb.AppendLine($"| `{EscapeBackticks(model.Id)}` | {EscapeMarkdownCell(model.DisplayName)} | {EscapeMarkdownCell(model.OwnedBy ?? "") } |");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<List<KeyVaultSecretSummary>> LoadKeyVaultSecretsAsync(CancellationToken ct)
    {
        await using var session = await _mcpFactory.GetClientAsync("GnOuGo.KeyVault.Mcp", ct);
        var call = await session.CallToolAsync("keyvault_list_secrets", null, ct);

        if (call.IsError)
            throw new InvalidOperationException("KeyVault list_secrets MCP call failed.");

        var payload = call.Content as JsonObject
            ?? throw new InvalidOperationException("Unexpected KeyVault MCP response shape.");

        var success = payload["Success"]?.GetValue<bool>() ?? payload["success"]?.GetValue<bool>() ?? false;
        if (!success)
        {
            var error = payload["Error"]?.GetValue<string>() ?? payload["error"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"KeyVault list_secrets failed: {error}");
        }

        var data = payload["Data"] as JsonArray ?? payload["data"] as JsonArray ?? [];
        var results = new List<KeyVaultSecretSummary>(data.Count);

        foreach (var item in data.OfType<JsonObject>())
        {
            var key = item["Key"]?.GetValue<string>() ?? item["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            results.Add(new KeyVaultSecretSummary(
                Key: key,
                CreatedAt: item["CreatedAt"]?.GetValue<string>() ?? item["createdAt"]?.GetValue<string>() ?? item["created_at"]?.GetValue<string>() ?? "",
                LatestVersion: item["LatestVersion"]?.GetValue<int>()
                    ?? item["latestVersion"]?.GetValue<int>()
                    ?? item["latest_version"]?.GetValue<int>()
                    ?? 0));
        }

        return results;
    }

    private static string RenderSecretTable(
        IReadOnlyList<KeyVaultSecretSummary> secrets,
        string prefix,
        string title,
        string emptyMessage)
    {
        var matching = secrets
            .Where(s => s.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matching.Count == 0)
            return $"{title}\n\n{emptyMessage}";

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("| Name | Key | Version | Stored |") ;
        sb.AppendLine("|------|-----|---------|--------|");

        foreach (var secret in matching)
        {
            var name = secret.Key[prefix.Length..];
            var stored = FormatTimestamp(secret.CreatedAt);
            sb.AppendLine($"| {EscapeMarkdownCell(name)} | `{EscapeBackticks(secret.Key)}` | {secret.LatestVersion} | {EscapeMarkdownCell(stored)} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderStatus(IReadOnlyList<KeyVaultSecretSummary> secrets)
    {
        var llms = secrets
            .Where(s => s.Key.StartsWith("gnougo_llm_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mcps = secrets
            .Where(s => s.Key.StartsWith("gnougo_mcp_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 📊 Current Configuration Status");
        sb.AppendLine();
        sb.AppendLine("## 🤖 LLM Providers");
        sb.AppendLine();

        if (llms.Count == 0)
        {
            sb.AppendLine("No LLM providers configured yet.");
        }
        else
        {
            sb.AppendLine("| Provider | KeyVault Key | Version | Stored |");
            sb.AppendLine("|----------|--------------|---------|--------|");
            foreach (var item in llms)
            {
                sb.AppendLine($"| {EscapeMarkdownCell(item.Key["gnougo_llm_".Length..])} | `{EscapeBackticks(item.Key)}` | {item.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(item.CreatedAt))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 🔌 MCP Servers");
        sb.AppendLine();

        if (mcps.Count == 0)
        {
            sb.AppendLine("No MCP servers configured yet.");
        }
        else
        {
            sb.AppendLine("| Server | KeyVault Key | Version | Stored |");
            sb.AppendLine("|--------|--------------|---------|--------|");
            foreach (var item in mcps)
            {
                sb.AppendLine($"| {EscapeMarkdownCell(item.Key["gnougo_mcp_".Length..])} | `{EscapeBackticks(item.Key)}` | {item.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(item.CreatedAt))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Use `/llm add` or `/mcp add` to get started.");
        return sb.ToString().TrimEnd();
    }

    private static string FormatTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var dto)
            ? dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;

    private static string EscapeMarkdownCell(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string EscapeBackticks(string value)
        => value.Replace("`", "\\`", StringComparison.Ordinal);

    private sealed record KeyVaultSecretSummary(string Key, string CreatedAt, int LatestVersion);

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

