using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Interactively configures LLM providers and MCP servers using direct,
/// trusted KeyVault access from the server process.
/// </summary>
public sealed class ConfigureProvidersService
{
    private readonly ILLMClient _llm;
    private readonly AgentHumanInputProvider _humanInput;
    private readonly ILLMModelCatalog _modelCatalog;
    private readonly IKeyVaultRuntimeConfigStore _keyVaultStore;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentUserConfigMcpClient? _userConfigClient;
    private readonly AgentOTelTelemetry _otel;
    private readonly ILogger<ConfigureProvidersService> _logger;

    public ConfigureProvidersService(
        ILLMClient llm,
        AgentHumanInputProvider humanInput,
        ILLMModelCatalog modelCatalog,
        IKeyVaultRuntimeConfigStore keyVaultStore,
        LLMRuntimeOptionsStore optionsStore,
        AgentOTelTelemetry otel,
        ILogger<ConfigureProvidersService> logger,
        AgentUserConfigMcpClient? userConfigClient = null)
    {
        _llm = llm;
        _humanInput = humanInput;
        _modelCatalog = modelCatalog;
        _keyVaultStore = keyVaultStore;
        _optionsStore = optionsStore;
        _userConfigClient = userConfigClient;
        _otel = otel;
        _logger = logger;
    }

    /// <summary>
    /// Executes the configure-providers workflow with the given slash command.
    /// </summary>
    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var trimmedCommand = command.Trim();
        var traceDescriptor = DescribeCommand(trimmedCommand);
        using var commandTrace = StartCommandTrace(traceDescriptor, trimmedCommand);

        if (TryParseModelsCommand(trimmedCommand, out var requestedModelProvider))
        {
            yield return new SmartFlowEvent("thinking:thinking", "🧠 Loading live model catalog…");
            yield return new SmartFlowEvent("answer", await RenderModelsCommandResponseAsync(requestedModelProvider, ct));
            yield break;
        }

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

        const string llmDefaultPrefix = "/llm default";
        if (trimmedCommand.StartsWith(llmDefaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var requestedProvider = trimmedCommand.Length > llmDefaultPrefix.Length
                ? trimmedCommand[llmDefaultPrefix.Length..].Trim()
                : string.Empty;
            await foreach (var evt in ExecuteInteractiveLlmDefaultAsync(requestedProvider, ct))
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

        const string llmRemovePrefix = "/llm remove";
        if (trimmedCommand.StartsWith(llmRemovePrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > llmRemovePrefix.Length)
        {
            var requestedProvider = trimmedCommand[llmRemovePrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveLlmRemoveAsync(requestedProvider, ct))
                yield return evt;
            yield break;
        }

        if (string.Equals(trimmedCommand, "/embedding add", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var evt in ExecuteInteractiveEmbeddingAddAsync(ct))
                yield return evt;
            yield break;
        }

        const string embeddingDefaultPrefix = "/embedding default";
        if (trimmedCommand.StartsWith(embeddingDefaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var requestedName = trimmedCommand.Length > embeddingDefaultPrefix.Length
                ? trimmedCommand[embeddingDefaultPrefix.Length..].Trim()
                : string.Empty;
            await foreach (var evt in ExecuteInteractiveEmbeddingDefaultAsync(requestedName, ct))
                yield return evt;
            yield break;
        }

        const string embeddingEditPrefix = "/embedding edit";
        if (trimmedCommand.StartsWith(embeddingEditPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > embeddingEditPrefix.Length)
        {
            var requestedName = trimmedCommand[embeddingEditPrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveEmbeddingEditAsync(requestedName, ct))
                yield return evt;
            yield break;
        }

        const string embeddingRemovePrefix = "/embedding remove";
        if (trimmedCommand.StartsWith(embeddingRemovePrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > embeddingRemovePrefix.Length)
        {
            var requestedName = trimmedCommand[embeddingRemovePrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveEmbeddingRemoveAsync(requestedName, ct))
                yield return evt;
            yield break;
        }

        if (string.Equals(trimmedCommand, "/mcp add", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var evt in ExecuteInteractiveMcpAddAsync(ct))
                yield return evt;
            yield break;
        }

        const string mcpEditPrefix = "/mcp edit";
        if (trimmedCommand.StartsWith(mcpEditPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > mcpEditPrefix.Length)
        {
            var requestedServer = trimmedCommand[mcpEditPrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveMcpEditAsync(requestedServer, ct))
                yield return evt;
            yield break;
        }

        const string mcpRemovePrefix = "/mcp remove";
        if (trimmedCommand.StartsWith(mcpRemovePrefix, StringComparison.OrdinalIgnoreCase)
            && trimmedCommand.Length > mcpRemovePrefix.Length)
        {
            var requestedServer = trimmedCommand[mcpRemovePrefix.Length..].Trim();
            await foreach (var evt in ExecuteInteractiveMcpRemoveAsync(requestedServer, ct))
                yield return evt;
            yield break;
        }

        if (trimmedCommand.StartsWith("/llm", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("answer", RenderLlmHelp(trimmedCommand));
            yield break;
        }

        if (trimmedCommand.StartsWith("/embedding", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("answer", RenderEmbeddingHelp(trimmedCommand));
            yield break;
        }

        if (trimmedCommand.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("answer", RenderMcpHelp(trimmedCommand));
            yield break;
        }

        yield return new SmartFlowEvent("answer", RenderWizardHelp());
    }

    private AgentOTelTelemetry.ActivityScope StartCommandTrace(CommandTraceDescriptor descriptor, string command)
    {
        var scope = _otel.StartActivityScope(descriptor.SpanName);
        scope.SetTag("gnougo.agent.command.route", "configure_providers");
        scope.SetTag("gnougo.agent.command.name", command);
        scope.SetTag("gnougo.agent.command.mode", descriptor.Mode);

        if (!string.IsNullOrWhiteSpace(descriptor.Domain))
            scope.SetTag("gnougo.agent.command.domain", descriptor.Domain);
        if (!string.IsNullOrWhiteSpace(descriptor.Action))
            scope.SetTag("gnougo.agent.command.action", descriptor.Action);
        if (!string.IsNullOrWhiteSpace(descriptor.Argument))
            scope.SetTag("gnougo.agent.command.argument", descriptor.Argument);

        scope.AddEvent("gnougo.agent.command.received", [
            new KeyValuePair<string, object?>("gnougo.agent.command.route", "configure_providers"),
            new KeyValuePair<string, object?>("gnougo.agent.command.name", command),
            new KeyValuePair<string, object?>("gnougo.agent.command.mode", descriptor.Mode)
        ]);

        return scope;
    }

    private AgentOTelTelemetry.ActivityScope StartNestedTrace(string spanName, string operation, ActivityKind kind = ActivityKind.Internal)
    {
        var scope = _otel.StartActivityScope(spanName, kind);
        scope.SetTag("gnougo.agent.command.route", "configure_providers");
        scope.SetTag("gnougo.agent.command.operation", operation);
        return scope;
    }

    private AgentOTelTelemetry.ActivityScope StartConfigureFlowTrace(
        string spanName,
        string kind,
        string action,
        string runId,
        string? targetName = null)
    {
        var scope = StartNestedTrace(spanName, $"{kind}.{action}.interactive");
        scope.SetTag("gnougo.agent.configure.kind", kind);
        scope.SetTag("gnougo.agent.configure.action", action);
        scope.SetTag("gnougo.agent.configure.run_id", runId);
        if (!string.IsNullOrWhiteSpace(targetName))
            scope.SetTag("gnougo.agent.configure.target_name", targetName);
        return scope;
    }

    private static string GetHumanInputPromptKind(HumanInputRequest request)
        => request.Fields is { Count: > 0 }
            ? "fields"
            : request.Choices is { Count: > 0 }
                ? "choice"
                : "prompt";

    private static CommandTraceDescriptor DescribeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new CommandTraceDescriptor("configure.providers.unknown", null, null, null, "direct");

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return new CommandTraceDescriptor("configure.providers.unknown", null, null, null, "direct");

        var domain = parts[0].TrimStart('/').ToLowerInvariant();
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : domain is "llm" or "embedding" or "mcp" ? "help" : domain;
        var argument = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;
        var mode = action is "add" or "edit" or "remove" or "default" ? "interactive" : "direct";

        var spanName = domain switch
        {
            "llm" => $"configure.providers.llm.{action}",
            "embedding" => $"configure.providers.embedding.{action}",
            "mcp" => $"configure.providers.mcp.{action}",
            "status" => "configure.providers.status",
            _ => "configure.providers.help"
        };

        return new CommandTraceDescriptor(spanName, domain, action, argument, mode);
    }

    private sealed record CommandTraceDescriptor(string SpanName, string? Domain, string? Action, string? Argument, string Mode);

    private async Task<string?> TryHandleWithoutLlmAsync(string command, CancellationToken ct)
    {
        if (string.Equals(command, "/llm list", StringComparison.OrdinalIgnoreCase))
        {
            using var span = StartNestedTrace("configure.providers.llm.list.render", "llm.list");
            var result = await RenderConfiguredLlmProvidersAsync(ct);
            span.SetStatus(ActivityStatusCode.Ok);
            return result;
        }

        if (string.Equals(command, "/mcp list", StringComparison.OrdinalIgnoreCase))
        {
            using var span = StartNestedTrace("configure.providers.mcp.list.render", "mcp.list");
            var secrets = await LoadKeyVaultSecretsAsync(ct);
            var result = RenderSecretTable(
                secrets,
                kind: KeyVaultConfigSecretKind.McpServer,
                title: "# 🔌 Configured MCP Servers",
                emptyMessage: "No MCP servers configured yet. Use `/mcp add` to get started.");
            span.SetTag("gnougo.agent.keyvault.secret_count", secrets.Count);
            span.SetStatus(ActivityStatusCode.Ok);
            return result;
        }

        if (string.Equals(command, "/embedding list", StringComparison.OrdinalIgnoreCase))
        {
            using var span = StartNestedTrace("configure.providers.embedding.list.render", "embedding.list");
            var result = await RenderConfiguredEmbeddingConfigsAsync(ct);
            span.SetStatus(ActivityStatusCode.Ok);
            return result;
        }

        if (string.Equals(command, "/status", StringComparison.OrdinalIgnoreCase))
        {
            using var span = StartNestedTrace("configure.providers.status.render", "status");
            var secrets = await LoadKeyVaultSecretsAsync(ct);
            var result = RenderStatus(secrets);
            span.SetTag("gnougo.agent.keyvault.secret_count", secrets.Count);
            span.SetStatus(ActivityStatusCode.Ok);
            return result;
        }

        return null;
    }

    private static bool TryParseModelsCommand(string command, out string requestedProvider)
    {
        const string modelsPrefix = "/llm models";
        if (command.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            requestedProvider = command.Length > modelsPrefix.Length
                ? command[modelsPrefix.Length..].Trim()
                : string.Empty;
            return true;
        }

        requestedProvider = string.Empty;
        return false;
    }

    private async Task<string> RenderModelsCommandResponseAsync(string requestedProvider, CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.llm.models.render", "llm.models");
        span.SetTag("gnougo.agent.command.argument", requestedProvider);

        var resolvedProvider = ResolveConfiguredProviderKey(requestedProvider);
        if (resolvedProvider is null)
        {
            span.SetStatus(ActivityStatusCode.Error, "Configured provider not found.");
            return RenderModelsUsage(requestedProvider);
        }

        span.SetTag("gen_ai.system", resolvedProvider);

        try
        {
            using var discoverySpan = StartNestedTrace("llm.model_catalog.list", "llm.model_catalog.list", ActivityKind.Client);
            discoverySpan.SetTag("gen_ai.system", resolvedProvider);

            var models = await _modelCatalog.ListModelsAsync(resolvedProvider, ct);

            discoverySpan.SetTag("gnougo.agent.llm.model_count", models.Count);
            discoverySpan.SetStatus(ActivityStatusCode.Ok);
            span.SetTag("gnougo.agent.llm.model_count", models.Count);
            span.SetStatus(ActivityStatusCode.Ok);
            return RenderModelCatalog(resolvedProvider, models);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load live model catalog for provider '{Provider}'.", resolvedProvider);
            span.SetStatus(ActivityStatusCode.Error, ex.Message);
            span.SetTag("error.type", ex.GetType().FullName);
            span.SetTag("error.message", ex.Message);
            return RenderModelCatalogError(resolvedProvider, ex);
        }
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveLlmAddAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var flowSpan = StartConfigureFlowTrace("configure.providers.llm.add.interactive", "llm", "add", runId);
        yield return new SmartFlowEvent("thinking:thinking", "🤖 Starting LLM provider configuration…");
        JsonNode? response = null;

        var providerRequest = CreateChoiceRequest(runId, "llm_add.provider", "Select the LLM provider to configure:", ["openai", "ollama", "copilot"]);
        await foreach (var evt in EmitHumanInputRequestAsync(providerRequest, r => response = r, ct))
            yield return evt;

        var provider = ReadChoiceResponse(response);
        if (string.IsNullOrWhiteSpace(provider))
            yield break;

        flowSpan.SetTag("gnougo.agent.configure.target_name", provider);
        flowSpan.SetTag("gen_ai.system", provider);

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

        flowSpan.SetTag("gnougo.agent.llm.auth_type", authType);

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

        flowSpan.SetTag("gen_ai.request.model", model);

        var summary = RenderLlmConfigSummary(provider, url, model, auth);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        var configuredProvidersBeforeSave = await LoadConfiguredLlmProvidersAsync(ct);
        var alreadyExists = configuredProvidersBeforeSave.Any(existing =>
            string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase));
        var hasValidConfiguredDefault = configuredProvidersBeforeSave.Any(existing =>
            string.Equals(existing.Provider, _optionsStore.Current.DefaultProvider, StringComparison.OrdinalIgnoreCase));
        var shouldPromoteAsDefault = !hasValidConfiguredDefault;
        flowSpan.SetTag("gnougo.agent.configure.overwrite", alreadyExists);
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
        yield return new SmartFlowEvent(
            "answer",
            $"✅ LLM provider '{provider}' saved to KeyVault as `{KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, provider)}`.");

        var outputs = BuildSavedLlmOutputs(provider, url, model, auth);
        TryApplyLlmConfig(outputs);

        if (shouldPromoteAsDefault && _optionsStore.SetDefaultProvider(provider, model))
        {
            await PersistRuntimeDefaultLlmAsync(ct);
            yield return new SmartFlowEvent(
                "thinking:response",
                $"⭐ Provider '{provider}' was set as the default LLM with model '{model}'.");
        }

        await foreach (var evt in ValidateConfigAsync(outputs, ct))
            yield return evt;

        flowSpan.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveLlmDefaultAsync(
        string requestedProvider,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var configuredProviders = await LoadConfiguredLlmProvidersAsync(ct);
        if (configuredProviders.Count == 0)
        {
            yield return new SmartFlowEvent(
                "answer",
                "❌ No LLM provider is configured yet. Use `/llm add` first, then run `/llm default`." );
            yield break;
        }

        var initialProvider = ResolveDefaultSelectionProvider(configuredProviders, requestedProvider);
        if (initialProvider is null)
        {
            yield return new SmartFlowEvent(
                "answer",
                $"❌ LLM provider '{requestedProvider}' is not configured. Use `/llm list` to see available providers.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:thinking", "⭐ Configuring the default LLM provider…");

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;
        var providerRequest = CreateFieldsRequest(
            runId,
            "llm_default.provider",
            "Select the default LLM provider:",
            [
                new HumanInputFieldDef
                {
                    Name = "provider",
                    Type = "select",
                    Required = true,
                    Description = "Configured providers stored in KeyVault",
                    Options = configuredProviders.Select(p => p.Provider).ToList(),
                    Default = initialProvider.Provider
                }
            ],
            JsonValue.Create($"Current default: {_optionsStore.Current.DefaultProvider} / {_optionsStore.Current.DefaultModel}"));
        await foreach (var evt in EmitHumanInputRequestAsync(providerRequest, r => response = r, ct))
            yield return evt;

        var selectedProvider = ReadFieldResponse(response, providerRequest.Fields!).GetValueOrDefault("provider") ?? initialProvider.Provider;
        var selectedConfig = configuredProviders.FirstOrDefault(p =>
            string.Equals(p.Provider, selectedProvider, StringComparison.OrdinalIgnoreCase));
        if (selectedConfig is null)
        {
            yield return new SmartFlowEvent(
                "answer",
                $"❌ LLM provider '{selectedProvider}' is not configured. Use `/llm list` to see available providers.");
            yield break;
        }

        string? model = null;
        await foreach (var evt in CollectModelSelectionAsync(
                           runId,
                           selectedConfig.Provider,
                           string.IsNullOrWhiteSpace(selectedConfig.Config.Model)
                               ? _optionsStore.Current.DefaultModel
                               : selectedConfig.Config.Model,
                           BuildProviderOptions(selectedConfig.Provider, selectedConfig.Config.Url, selectedConfig.Config),
                           selected => model = selected,
                           ct))
            yield return evt;

        if (string.IsNullOrWhiteSpace(model))
            yield break;

        var summary = RenderDefaultProviderSummary(selectedConfig.Provider, model);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        response = null;
        var confirmRequest = CreateChoiceRequest(
            runId,
            "llm_default.confirm_save",
            $"Set '{selectedConfig.Provider}' as the default provider?",
            ["save", "discard"],
            JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Default provider update discarded.");
            yield break;
        }

        await SaveLlmProviderConfigAsync(selectedConfig.Provider, selectedConfig.Config.Url, model, selectedConfig.Config, ct);
        _optionsStore.UpdateProvider(
            providerKey: selectedConfig.Provider,
            url: selectedConfig.Config.Url,
            model: model,
            apiKey: selectedConfig.Config.ApiKey,
            authType: selectedConfig.Config.AuthType,
            oidcIssuer: selectedConfig.Config.OidcIssuer,
            oidcClientId: selectedConfig.Config.OidcClientId,
            oidcScopes: selectedConfig.Config.OidcScopes,
            oidcClientSecret: selectedConfig.Config.OidcClientSecret);

        if (!_optionsStore.SetDefaultProvider(selectedConfig.Provider, model))
        {
            yield return new SmartFlowEvent(
                "error",
                $"Could not set '{selectedConfig.Provider}' as the default provider in runtime settings.");
            yield break;
        }

        await PersistRuntimeDefaultLlmAsync(ct);

        yield return new SmartFlowEvent(
            "answer",
            $"✅ Default LLM provider set to '{selectedConfig.Provider}' with model '{model}'.");

        var outputs = BuildSavedLlmOutputs(selectedConfig.Provider, selectedConfig.Config.Url, model, selectedConfig.Config);
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

        var runId = Guid.NewGuid().ToString("N");
        using var flowSpan = StartConfigureFlowTrace("configure.providers.llm.edit.interactive", "llm", "edit", runId, provider);
        flowSpan.SetTag("gen_ai.system", provider);
        yield return new SmartFlowEvent("thinking:thinking", $"🔍 Loading LLM provider '{provider}' from KeyVault…");
        var existing = await LoadLlmProviderConfigAsync(provider, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ LLM provider '{requestedProvider}' not found. Use `/llm list` to see available providers.");
            yield break;
        }

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
        flowSpan.SetTag("gnougo.agent.llm.auth_type", authSelection);
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

        flowSpan.SetTag("gen_ai.request.model", model);

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
        TryApplyLlmConfig(outputs);
        await foreach (var evt in ValidateConfigAsync(outputs, ct))
            yield return evt;

        flowSpan.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveLlmRemoveAsync(
        string requestedProvider,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = ResolveConfiguredProviderKey(requestedProvider) ?? requestedProvider.Trim();
        if (string.IsNullOrWhiteSpace(provider))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the provider to remove: `/llm remove <provider>`.");
            yield break;
        }

        var existing = await LoadLlmProviderConfigAsync(provider, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ LLM provider '{requestedProvider}' not found. Use `/llm list` to see available providers.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;
        var request = CreateChoiceRequest(
            runId,
            "llm_remove.confirm",
            $"⚠️ Remove LLM provider '{provider}'?",
            ["confirm", "cancel"]);

        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Removal cancelled.");
            yield break;
        }

        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.LlmProvider, provider, ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, provider);
        var deleted = await _keyVaultStore.DeleteSecretAsync(secretKey, ct);
        if (!deleted)
        {
            yield return new SmartFlowEvent("answer", $"❌ LLM provider '{provider}' could not be removed because it no longer exists.");
            yield break;
        }

        _optionsStore.RemoveProvider(provider);
        await PersistRuntimeDefaultLlmAsync(ct);
        yield return new SmartFlowEvent("answer", $"✅ LLM provider '{provider}' removed.");
    }

    private async Task PersistRuntimeDefaultLlmAsync(CancellationToken ct)
    {
        if (_userConfigClient is null)
            return;

        var current = _optionsStore.Current;
        var clearDefaultLlm = string.IsNullOrWhiteSpace(current.DefaultProvider);
        await _userConfigClient.SetAsync(
            defaultLlmProvider: clearDefaultLlm ? null : current.DefaultProvider,
            defaultLlmModel: clearDefaultLlm ? null : current.DefaultModel,
            clearDefaultLlm: clearDefaultLlm,
            ct: ct);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveEmbeddingAddAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var flowSpan = StartConfigureFlowTrace("configure.providers.embedding.add.interactive", "embedding", "add", runId);
        yield return new SmartFlowEvent("thinking:thinking", "🧬 Starting embedding model configuration…");

        JsonNode? response = null;
        var baseRequest = CreateFieldsRequest(
            runId,
            "embedding_add.base",
            "Configure the embedding model:",
            [
                new HumanInputFieldDef { Name = "name", Type = "string", Required = true, Description = "Embedding config name, for example openai-small or local-nomic" },
                new HumanInputFieldDef { Name = "provider", Type = "select", Required = true, Description = "Embedding provider", Options = ["openai", "openai-compatible", "ollama", "hash"], Default = "openai" }
            ]);
        await foreach (var evt in EmitHumanInputRequestAsync(baseRequest, r => response = r, ct))
            yield return evt;

        var fields = ReadFieldResponse(response, baseRequest.Fields!);
        var name = fields.GetValueOrDefault("name")?.Trim() ?? string.Empty;
        var provider = fields.GetValueOrDefault("provider")?.Trim() ?? "openai";
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new SmartFlowEvent("answer", "❌ Embedding config name is required.");
            yield break;
        }

        flowSpan.SetTag("gnougo.agent.configure.target_name", name);
        flowSpan.SetTag("gen_ai.system", provider);

        EmbeddingProviderConfig? config = null;
        await foreach (var evt in CollectEmbeddingConfigAsync(runId, name, provider, null, cfg => config = cfg, ct))
            yield return evt;
        if (config is null)
            yield break;

        var summary = RenderEmbeddingConfigSummary(config);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        var existing = await LoadEmbeddingConfigAsync(name, ct);
        response = null;
        var confirmRequest = CreateChoiceRequest(
            runId,
            "embedding_add.confirm_save",
            existing is null ? "Save this embedding configuration?" : "⚠️ An embedding configuration with this name already exists. Overwrite?",
            ["save", "discard"],
            JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Embedding configuration discarded.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:progress", "💾 Saving embedding configuration to KeyVault…");
        await SaveEmbeddingConfigAsync(config, ct);
        yield return new SmartFlowEvent("answer", $"✅ Embedding config '{config.Name}' saved to KeyVault as `{KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, config.Name)}`.");
        flowSpan.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveEmbeddingDefaultAsync(
        string requestedName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var configs = await LoadConfiguredEmbeddingConfigsAsync(ct);
        if (configs.Count == 0)
        {
            yield return new SmartFlowEvent("answer", "❌ No embedding config is configured yet. Use `/embedding add` first, then run `/embedding default`.");
            yield break;
        }

        var currentDefault = await LoadDefaultEmbeddingNameAsync(ct);
        var initial = ResolveEmbeddingSelection(configs, requestedName, currentDefault);
        if (initial is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ Embedding config '{requestedName}' is not configured. Use `/embedding list` to see available configs.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;
        var request = CreateFieldsRequest(
            runId,
            "embedding_default.name",
            "Select the default embedding config:",
            [
                new HumanInputFieldDef
                {
                    Name = "name",
                    Type = "select",
                    Required = true,
                    Description = "Configured embedding configs stored in KeyVault",
                    Options = configs.Select(c => c.Name).ToList(),
                    Default = initial.Name
                }
            ],
            JsonValue.Create(string.IsNullOrWhiteSpace(currentDefault) ? "No default embedding config is currently set." : $"Current default: {currentDefault}"));
        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;

        var selectedName = ReadFieldResponse(response, request.Fields!).GetValueOrDefault("name") ?? initial.Name;
        var selected = configs.FirstOrDefault(c => string.Equals(c.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ Embedding config '{selectedName}' is not configured. Use `/embedding list` to see available configs.");
            yield break;
        }

        var summary = RenderDefaultEmbeddingSummary(selected.Config);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        response = null;
        var confirmRequest = CreateChoiceRequest(runId, "embedding_default.confirm_save", $"Set '{selected.Name}' as the default embedding config?", ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Default embedding update discarded.");
            yield break;
        }

        await SaveDefaultEmbeddingNameAsync(selected.Name, ct);
        await PersistRuntimeDefaultEmbeddingAsync(selected.Name, ct);
        yield return new SmartFlowEvent("answer", $"✅ Default embedding config set to '{selected.Name}'.");
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveEmbeddingEditAsync(
        string requestedName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var name = await ResolveConfiguredEmbeddingNameAsync(requestedName, ct) ?? requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the embedding config to edit: `/embedding edit <name>`.");
            yield break;
        }

        var existing = await LoadEmbeddingConfigAsync(name, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ Embedding config '{requestedName}' not found. Use `/embedding list` to see available configs.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        EmbeddingProviderConfig? updated = null;
        await foreach (var evt in CollectEmbeddingConfigAsync(runId, existing.Name, existing.Provider, existing, cfg => updated = cfg, ct))
            yield return evt;
        if (updated is null)
            yield break;

        var summary = RenderEmbeddingConfigSummary(updated);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        JsonNode? response = null;
        var confirmRequest = CreateChoiceRequest(runId, "embedding_edit.confirm_save", $"Save updated embedding config '{updated.Name}'?", ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Edit discarded.");
            yield break;
        }

        await SaveEmbeddingConfigAsync(updated, ct);
        yield return new SmartFlowEvent("answer", $"✅ Embedding config '{updated.Name}' updated.");
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveEmbeddingRemoveAsync(
        string requestedName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var name = await ResolveConfiguredEmbeddingNameAsync(requestedName, ct) ?? requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the embedding config to remove: `/embedding remove <name>`.");
            yield break;
        }

        var existing = await LoadEmbeddingConfigAsync(name, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ Embedding config '{requestedName}' not found. Use `/embedding list` to see available configs.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;
        var request = CreateChoiceRequest(runId, "embedding_remove.confirm", $"⚠️ Remove embedding config '{existing.Name}'?", ["confirm", "cancel"]);
        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Removal cancelled.");
            yield break;
        }

        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.EmbeddingConfig, existing.Name, ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, existing.Name);
        var deleted = await _keyVaultStore.DeleteSecretAsync(secretKey, ct);
        if (!deleted)
        {
            yield return new SmartFlowEvent("answer", $"❌ Embedding config '{existing.Name}' could not be removed because it no longer exists.");
            yield break;
        }

        var currentDefault = await LoadDefaultEmbeddingNameAsync(ct);
        if (string.Equals(currentDefault, existing.Name, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteDefaultEmbeddingNameAsync(ct);
            await PersistRuntimeDefaultEmbeddingAsync(null, ct);
        }

        yield return new SmartFlowEvent("answer", $"✅ Embedding config '{existing.Name}' removed.");
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectEmbeddingConfigAsync(
        string runId,
        string name,
        string provider,
        EmbeddingProviderConfig? existing,
        Action<EmbeddingProviderConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        JsonNode? response = null;
        provider = string.IsNullOrWhiteSpace(provider) ? "openai" : provider.Trim().ToLowerInvariant();

        if (string.Equals(provider, "hash", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(runId, existing is null ? "embedding_add.hash" : "embedding_edit.hash", $"Configure hash embedding '{name}':", [
                new HumanInputFieldDef { Name = "dimensions", Type = "number", Required = true, Description = "Vector dimensions", Default = (existing?.Dimensions ?? 384).ToString() }
            ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            setConfig(new EmbeddingProviderConfig(name, "hash", null, null, null, null, null, ReadInt(fields.GetValueOrDefault("dimensions"), existing?.Dimensions ?? 384)));
            yield break;
        }

        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(runId, existing is null ? "embedding_add.ollama" : "embedding_edit.ollama", $"Configure Ollama embedding '{name}':", [
                new HumanInputFieldDef { Name = "base_url", Type = "string", Required = true, Description = "Ollama base URL", Default = existing?.BaseUrl ?? "http://localhost:11434" },
                new HumanInputFieldDef { Name = "model", Type = "string", Required = true, Description = "Embedding model name", Default = existing?.Model ?? "nomic-embed-text" },
                new HumanInputFieldDef { Name = "dimensions", Type = "number", Required = true, Description = "Vector dimensions", Default = (existing?.Dimensions ?? 768).ToString() }
            ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            setConfig(new EmbeddingProviderConfig(name, "ollama", fields.GetValueOrDefault("model"), null, fields.GetValueOrDefault("base_url"), null, null, ReadInt(fields.GetValueOrDefault("dimensions"), existing?.Dimensions ?? 768)));
            yield break;
        }

        var openAiRequest = CreateFieldsRequest(runId, existing is null ? "embedding_add.openai" : "embedding_edit.openai", $"Configure {provider} embedding '{name}':", [
            new HumanInputFieldDef { Name = "endpoint_url", Type = "string", Required = true, Description = "OpenAI-compatible base endpoint URL", Default = existing?.EndpointUrl ?? "https://api.openai.com/v1" },
            new HumanInputFieldDef { Name = "model", Type = "string", Required = true, Description = "Embedding model name", Default = existing?.Model ?? "text-embedding-3-small" },
            new HumanInputFieldDef { Name = "dimensions", Type = "number", Required = true, Description = "Vector dimensions", Default = (existing?.Dimensions ?? 1536).ToString() },
            new HumanInputFieldDef { Name = "api_key", Type = "string", Required = existing is null, Description = "API key stored encrypted in KeyVault. Leave empty during edit to keep current key." }
        ]);
        await foreach (var evt in EmitHumanInputRequestAsync(openAiRequest, r => response = r, ct))
            yield return evt;
        var openAiFields = ReadFieldResponse(response, openAiRequest.Fields!);
        var apiKey = openAiFields.GetValueOrDefault("api_key");
        if (string.IsNullOrWhiteSpace(apiKey) && existing is not null)
            apiKey = existing.ApiKey;
        setConfig(new EmbeddingProviderConfig(name, provider, openAiFields.GetValueOrDefault("model"), openAiFields.GetValueOrDefault("endpoint_url"), null, apiKey, null, ReadInt(openAiFields.GetValueOrDefault("dimensions"), existing?.Dimensions ?? 1536)));
    }

    private async Task PersistRuntimeDefaultEmbeddingAsync(string? name, CancellationToken ct)
    {
        if (_userConfigClient is null)
            return;

        await _userConfigClient.SetAsync(
            defaultEmbeddingConfig: string.IsNullOrWhiteSpace(name) ? null : name,
            clearDefaultEmbedding: string.IsNullOrWhiteSpace(name),
            ct: ct);
    }

    private async Task SaveEmbeddingConfigAsync(EmbeddingProviderConfig config, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["provider"] = config.Provider,
            ["name"] = config.Name,
            ["model"] = config.Model,
            ["endpointUrl"] = config.EndpointUrl,
            ["baseUrl"] = config.BaseUrl,
            ["apiKey"] = config.ApiKey,
            ["apiKeySecretKey"] = config.ApiKeySecretKey,
            ["dimensions"] = config.Dimensions
        };

        var preferredSecretKey = KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, config.Name);
        var existingSecretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.EmbeddingConfig, config.Name, ct);
        await _keyVaultStore.SaveSecretValueAsync(preferredSecretKey, payload.ToJsonString(), ct);

        if (!string.IsNullOrWhiteSpace(existingSecretKey)
            && !string.Equals(existingSecretKey, preferredSecretKey, StringComparison.OrdinalIgnoreCase))
        {
            await _keyVaultStore.DeleteSecretAsync(existingSecretKey, ct);
        }
    }

    private async Task<EmbeddingProviderConfig?> LoadEmbeddingConfigAsync(string name, CancellationToken ct)
    {
        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.EmbeddingConfig, name, ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, name);
        var value = await _keyVaultStore.GetSecretValueAsync(secretKey, ct);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var config = JsonNode.Parse(value) as JsonObject;
        if (config is null)
            return null;

        return new EmbeddingProviderConfig(
            Name: ReadConfigString(config, "name") ?? name,
            Provider: ReadConfigString(config, "provider") ?? "openai",
            Model: ReadConfigString(config, "model"),
            EndpointUrl: ReadConfigString(config, "endpointUrl", "endpoint_url"),
            BaseUrl: ReadConfigString(config, "baseUrl", "base_url"),
            ApiKey: ReadConfigString(config, "apiKey", "api_key"),
            ApiKeySecretKey: ReadConfigString(config, "apiKeySecretKey", "api_key_secret_key"),
            Dimensions: config["dimensions"]?.GetValue<int>() ?? config["Dimensions"]?.GetValue<int>() ?? 0);
    }

    private async Task<List<ConfiguredEmbeddingConfig>> LoadConfiguredEmbeddingConfigsAsync(CancellationToken ct)
    {
        var secrets = await LoadKeyVaultSecretsAsync(ct);
        var configs = new List<ConfiguredEmbeddingConfig>();
        foreach (var secret in KeyVaultConfigNaming.SelectPreferredSecrets(secrets, KeyVaultConfigSecretKind.EmbeddingConfig))
        {
            var logicalName = KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.EmbeddingConfig, secret.Key) ?? secret.Key;
            var config = await LoadEmbeddingConfigAsync(logicalName, ct);
            if (config is not null)
                configs.Add(new ConfiguredEmbeddingConfig(config.Name, secret, config));
        }

        return configs;
    }

    private async Task<string> RenderConfiguredEmbeddingConfigsAsync(CancellationToken ct)
    {
        var configs = await LoadConfiguredEmbeddingConfigsAsync(ct);
        if (configs.Count == 0)
            return "# 🧬 Configured Embedding Models\n\nNo embedding configs configured yet. Use `/embedding add` to get started.";

        var currentDefault = await LoadDefaultEmbeddingNameAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("# 🧬 Configured Embedding Models");
        sb.AppendLine();
        sb.AppendLine("| Name | Default | Provider | Model | Dimensions | Key | Version | Stored |");
        sb.AppendLine("|------|---------|----------|-------|------------|-----|---------|--------|");
        foreach (var config in configs
                     .OrderByDescending(c => string.Equals(c.Name, currentDefault, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isDefault = string.Equals(config.Name, currentDefault, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"| {EscapeMarkdownCell(config.Name)} | {(isDefault ? "✅ yes" : "")} | {EscapeMarkdownCell(config.Config.Provider)} | {EscapeMarkdownCell(config.Config.Model ?? "")} | {config.Config.Dimensions} | `{EscapeBackticks(config.Secret.Key)}` | {config.Secret.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(config.Secret.CreatedAt))} |");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> ResolveConfiguredEmbeddingNameAsync(string requestedName, CancellationToken ct)
    {
        var configs = await LoadConfiguredEmbeddingConfigsAsync(ct);
        return configs.FirstOrDefault(c => string.Equals(c.Name, requestedName, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static ConfiguredEmbeddingConfig? ResolveEmbeddingSelection(IReadOnlyList<ConfiguredEmbeddingConfig> configs, string requestedName, string? currentDefault)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
            return configs.FirstOrDefault(c => string.Equals(c.Name, requestedName, StringComparison.OrdinalIgnoreCase));

        return configs.FirstOrDefault(c => string.Equals(c.Name, currentDefault, StringComparison.OrdinalIgnoreCase))
               ?? configs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private async Task SaveDefaultEmbeddingNameAsync(string name, CancellationToken ct)
    {
        var payload = new JsonObject { ["defaultEmbeddingConfig"] = name };
        await _keyVaultStore.SaveSecretValueAsync(KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingDefault, "default"), payload.ToJsonString(), ct);
    }

    private async Task<string?> LoadDefaultEmbeddingNameAsync(CancellationToken ct)
    {
        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.EmbeddingDefault, "default", ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingDefault, "default");
        var value = await _keyVaultStore.GetSecretValueAsync(secretKey, ct);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var config = JsonNode.Parse(value) as JsonObject;
        return config?["defaultEmbeddingConfig"]?.GetValue<string>()
               ?? config?["name"]?.GetValue<string>();
    }

    private async Task DeleteDefaultEmbeddingNameAsync(CancellationToken ct)
    {
        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.EmbeddingDefault, "default", ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingDefault, "default");
        await _keyVaultStore.DeleteSecretAsync(secretKey, ct);
    }

    private static int ReadInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static string RenderEmbeddingConfigSummary(EmbeddingProviderConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📋 Review embedding configuration");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Name | {EscapeMarkdownCell(config.Name)} |");
        sb.AppendLine($"| Provider | {EscapeMarkdownCell(config.Provider)} |");
        sb.AppendLine($"| Model | {EscapeMarkdownCell(config.Model ?? "")} |");
        sb.AppendLine($"| Dimensions | {config.Dimensions} |");
        if (!string.IsNullOrWhiteSpace(config.EndpointUrl))
            sb.AppendLine($"| Endpoint URL | {EscapeMarkdownCell(config.EndpointUrl)} |");
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
            sb.AppendLine($"| Base URL | {EscapeMarkdownCell(config.BaseUrl)} |");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            sb.AppendLine("| API Key | •••••••• |");
        sb.AppendLine();
        sb.Append($"Configuration will be saved as key: `{KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, config.Name)}`");
        return sb.ToString();
    }

    private static string RenderDefaultEmbeddingSummary(EmbeddingProviderConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ⭐ Review default embedding config");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Name | {EscapeMarkdownCell(config.Name)} |");
        sb.AppendLine($"| Provider | {EscapeMarkdownCell(config.Provider)} |");
        sb.AppendLine($"| Model | {EscapeMarkdownCell(config.Model ?? "")} |");
        sb.AppendLine($"| Dimensions | {config.Dimensions} |");
        return sb.ToString().TrimEnd();
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveMcpAddAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var flowSpan = StartConfigureFlowTrace("configure.providers.mcp.add.interactive", "mcp", "add", runId);
        yield return new SmartFlowEvent("thinking:thinking", "🔌 Starting MCP server configuration…");
        JsonNode? response = null;

        var transportRequest = CreateChoiceRequest(runId, "mcp_add.transport", "What type of MCP server do you want to configure?", ["http", "stdio"]);
        await foreach (var evt in EmitHumanInputRequestAsync(transportRequest, r => response = r, ct))
            yield return evt;

        var transport = ReadChoiceResponse(response);
        if (string.IsNullOrWhiteSpace(transport))
            yield break;

        flowSpan.SetTag("mcp.transport", transport);

        McpServerConfig? config = null;
        if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var evt in CollectHttpMcpConfigAsync(runId, null, cfg => config = cfg, ct))
                yield return evt;
        }
        else
        {
            await foreach (var evt in CollectStdioMcpConfigAsync(runId, null, cfg => config = cfg, ct))
                yield return evt;
        }

        if (config is null)
            yield break;

        flowSpan.SetTag("gnougo.agent.configure.target_name", config.Name);
        flowSpan.SetTag("mcp.server.name", config.Name);

        var summary = RenderMcpConfigSummary(config);
        yield return new SmartFlowEvent("thinking:thinking", summary);

        var existing = await LoadMcpServerConfigAsync(config.Name, ct);
        flowSpan.SetTag("gnougo.agent.configure.overwrite", existing is not null);
        response = null;
        var confirmPrompt = existing is null
            ? "Save this MCP server configuration?"
            : "⚠️ An MCP server with this name already exists. Overwrite?";
        var confirmRequest = CreateChoiceRequest(runId, "mcp_add.confirm_save", confirmPrompt, ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ MCP server configuration discarded.");
            yield break;
        }

        yield return new SmartFlowEvent("thinking:progress", "💾 Saving MCP server configuration to KeyVault…");
        await SaveMcpServerConfigAsync(config, ct);
        yield return new SmartFlowEvent("answer", $"✅ MCP server '{config.Name}' saved to KeyVault.");
        flowSpan.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveMcpEditAsync(
        string requestedName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var name = requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the MCP server to edit: `/mcp edit <name>`.");
            yield break;
        }
        var runId = Guid.NewGuid().ToString("N");
        using var flowSpan = StartConfigureFlowTrace("configure.providers.mcp.edit.interactive", "mcp", "edit", runId, name);
        var existing = await LoadMcpServerConfigAsync(name, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ MCP server '{requestedName}' not found. Use `/mcp list`.");
            yield break;
        }
        JsonNode? response = null;
        flowSpan.SetTag("mcp.transport", existing.Transport);

        if (string.Equals(existing.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(
                runId,
                "mcp_edit.http",
                $"Edit MCP server '{existing.Name}':",
                [
                    new HumanInputFieldDef { Name = "description", Type = "string", Required = true, Description = "Short description", Default = existing.Description },
                    new HumanInputFieldDef { Name = "url", Type = "string", Required = true, Description = "HTTP endpoint URL", Default = existing.Url }
                ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            existing = existing with
            {
                Description = fields.GetValueOrDefault("description") ?? existing.Description,
                Url = fields.GetValueOrDefault("url") ?? existing.Url
            };
        }
        else
        {
            var request = CreateFieldsRequest(
                runId,
                "mcp_edit.stdio",
                $"Edit MCP server '{existing.Name}':",
                [
                    new HumanInputFieldDef { Name = "description", Type = "string", Required = true, Description = "Short description", Default = existing.Description },
                    new HumanInputFieldDef { Name = "command", Type = "string", Required = true, Description = "Command to execute", Default = existing.Command },
                    new HumanInputFieldDef { Name = "args", Type = "string", Required = false, Description = "Arguments (comma-separated)", Default = JoinArgs(existing.Args) }
                ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            existing = existing with
            {
                Description = fields.GetValueOrDefault("description") ?? existing.Description,
                Command = fields.GetValueOrDefault("command") ?? existing.Command,
                Args = ParseCommaSeparatedArgs(fields.GetValueOrDefault("args"))
            };
        }

        response = null;
        var summary = RenderMcpConfigSummary(existing);
        var confirmRequest = CreateChoiceRequest(runId, "mcp_edit.confirm_save", $"Save updated MCP server '{existing.Name}'?", ["save", "discard"], JsonValue.Create(summary));
        await foreach (var evt in EmitHumanInputRequestAsync(confirmRequest, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "save", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Edit discarded.");
            yield break;
        }

        await SaveMcpServerConfigAsync(existing, ct);
        yield return new SmartFlowEvent("answer", $"✅ MCP server '{existing.Name}' updated.");
        flowSpan.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> ExecuteInteractiveMcpRemoveAsync(
        string requestedName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var name = requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new SmartFlowEvent("answer", "❌ Please specify the MCP server to remove: `/mcp remove <name>`.");
            yield break;
        }

        var existing = await LoadMcpServerConfigAsync(name, ct);
        if (existing is null)
        {
            yield return new SmartFlowEvent("answer", $"❌ MCP server '{requestedName}' not found. Use `/mcp list`.");
            yield break;
        }

        var runId = Guid.NewGuid().ToString("N");
        JsonNode? response = null;
        var request = CreateChoiceRequest(runId, "mcp_remove.confirm", $"⚠️ Remove MCP server '{existing.Name}'?", ["confirm", "cancel"]);
        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;

        if (!string.Equals(ReadChoiceResponse(response), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SmartFlowEvent("thinking:thinking", "❌ Removal cancelled.");
            yield break;
        }

        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.McpServer, existing.Name, ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.McpServer, existing.Name);
        var deleted = await _keyVaultStore.DeleteSecretAsync(secretKey, ct);
        if (!deleted)
        {
            yield return new SmartFlowEvent("answer", $"❌ MCP server '{existing.Name}' could not be removed because it no longer exists.");
            yield break;
        }

        yield return new SmartFlowEvent("answer", $"✅ MCP server '{existing.Name}' removed.");
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectModelSelectionAsync(
        string runId,
        string provider,
        string defaultModel,
        ModelProviderOptions providerOptions,
        Action<string?> setModel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.llm.model.select", "llm.model.select");
        span.SetTag("gnougo.agent.configure.run_id", runId);
        span.SetTag("gen_ai.system", provider);
        IReadOnlyList<LLMModelDescriptor>? models = null;
        try
        {
            models = await DiscoverModelsAsync(provider, providerOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Model discovery failed for provider '{Provider}', falling back to manual model entry.", provider);
            span.AddEvent("gnougo.agent.llm.model.discovery_failed", [
                new KeyValuePair<string, object?>("error.type", ex.GetType().FullName),
                new KeyValuePair<string, object?>("error.message", ex.Message)
            ]);
        }

        if (models is { Count: > 0 })
        {
            span.SetTag("gnougo.agent.model.discovery.mode", "live");
            span.SetTag("gnougo.agent.llm.model_count", models.Count);
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
            var selectedModel = ReadFieldResponse(response, request.Fields!).GetValueOrDefault("model");
            setModel(selectedModel);
            span.SetTag("gen_ai.request.model", selectedModel);
            span.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        span.SetTag("gnougo.agent.model.discovery.mode", "manual");
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
        var manualModel = ReadFieldResponse(fallbackResponse, fallbackRequest.Fields!).GetValueOrDefault("model");
        setModel(manualModel);
        span.SetTag("gen_ai.request.model", manualModel);
        span.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectAuthConfigAsync(
        string runId,
        string provider,
        string authType,
        LlmProviderConfig? existing,
        Action<LlmProviderConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.llm.auth.collect", "llm.auth.collect");
        span.SetTag("gnougo.agent.configure.run_id", runId);
        span.SetTag("gen_ai.system", provider);
        span.SetTag("gnougo.agent.llm.auth_type", authType);
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
        {
            setConfig(new LlmProviderConfig(existing?.Url ?? "", existing?.Model ?? "", authType, "", "", "", "", ""));
            span.SetStatus(ActivityStatusCode.Ok);
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
            setConfig(new LlmProviderConfig(existing?.Url ?? "", existing?.Model ?? "", "api_key", apiKey, "", "", "", ""));
            span.SetStatus(ActivityStatusCode.Ok);
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
                existing?.Url ?? "",
                existing?.Model ?? "",
                "oidc",
                "",
                fields.GetValueOrDefault("issuer") ?? "",
                fields.GetValueOrDefault("client_id") ?? "",
                fields.GetValueOrDefault("scopes") ?? "",
                fields.GetValueOrDefault("client_secret") ?? ""));
            span.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        setConfig(new LlmProviderConfig(existing?.Url ?? "", existing?.Model ?? "", authType, "", "", "", "", ""));
        span.SetStatus(ActivityStatusCode.Ok);
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
        using var span = StartNestedTrace("configure.providers.human_input.request", "human_input.request");
        span.SetTag("gnougo.agent.configure.run_id", request.RunId);
        span.SetTag("gnougo.agent.configure.step_id", request.StepId);
        span.SetTag("gnougo.agent.configure.prompt_kind", GetHumanInputPromptKind(request));
        span.SetTag("gnougo.agent.configure.choice_count", request.Choices?.Count ?? 0);
        span.SetTag("gnougo.agent.configure.field_count", request.Fields?.Count ?? 0);
        span.SetTag("gnougo.agent.configure.timeout_ms", request.TimeoutMs);
        yield return new SmartFlowEvent("human_input_request", BuildHumanInputPayload(request).ToJsonString());
        var response = await _humanInput.RequestInputAsync(request, ct);
        captureResponse(response);
        span.SetTag("gnougo.agent.configure.response_received", response is not null);
        span.SetStatus(ActivityStatusCode.Ok);
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
        using var span = StartNestedTrace("llm.model_catalog.list", "llm.model_catalog.list", ActivityKind.Client);
        span.SetTag("gen_ai.system", provider);
        if (ShouldUseInjectedModelCatalog(provider, providerOptions))
        {
            try
            {
                var models = await _modelCatalog.ListModelsAsync(provider, ct);
                span.SetTag("gnougo.agent.model.discovery.source", "runtime_catalog");
                span.SetTag("gnougo.agent.llm.model_count", models.Count);
                span.SetStatus(ActivityStatusCode.Ok);
                return models;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Injected model catalog could not list models for '{Provider}', trying temporary provider settings.", provider);
                span.AddEvent("gnougo.agent.llm.model.runtime_catalog_failed", [
                    new KeyValuePair<string, object?>("error.type", ex.GetType().FullName),
                    new KeyValuePair<string, object?>("error.message", ex.Message)
                ]);
            }
        }
        else
        {
            _logger.LogDebug(
                "Skipping injected model catalog for '{Provider}' because runtime credentials do not match the interactive provider settings.",
                provider);
            span.SetTag("gnougo.agent.model.discovery.source", "temporary_catalog");
        }

        using var http = new HttpClient();
        var options = new LLMOptions
        {
            DefaultProvider = provider,
            DefaultModel = ""
        };
        options.Models[provider] = providerOptions;
        var catalog = new RoutingLLMModelCatalog(http, options);
        var discovered = await catalog.ListModelsAsync(provider, ct);
        span.SetTag("gnougo.agent.llm.model_count", discovered.Count);
        span.SetStatus(ActivityStatusCode.Ok);
        return discovered;
    }

    private bool ShouldUseInjectedModelCatalog(string provider, ModelProviderOptions providerOptions)
    {
        var runtimeProvider = _optionsStore.Current.ResolveProvider(provider);
        if (runtimeProvider is null)
            return true;

        if (!string.Equals(NormalizeUrl(runtimeProvider.Url), NormalizeUrl(providerOptions.Url), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!RequiresExplicitAuth(providerOptions))
            return true;

        return HasUsableAuth(runtimeProvider);
    }

    private static bool RequiresExplicitAuth(ModelProviderOptions providerOptions)
        => !string.Equals(providerOptions.ResolvedType, "ollama", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(providerOptions.ResolvedType, "copilot", StringComparison.OrdinalIgnoreCase);

    private static bool HasUsableAuth(ModelProviderOptions providerOptions)
        => !string.IsNullOrWhiteSpace(providerOptions.ApiKey)
           || (!string.IsNullOrWhiteSpace(providerOptions.Issuer)
               && !string.IsNullOrWhiteSpace(providerOptions.ClientId)
               && !string.IsNullOrWhiteSpace(providerOptions.Scopes));

    private static string NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');

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
        using var span = StartNestedTrace("configure.providers.llm.save", "llm.save", ActivityKind.Client);
        span.SetTag("gnougo.agent.configure.target_name", provider);
        span.SetTag("gen_ai.system", provider);
        span.SetTag("gen_ai.request.model", model);
        span.SetTag("gnougo.agent.llm.auth_type", auth.AuthType);
        var payload = new JsonObject
        {
            ["provider"] = provider,
            ["url"] = url,
            ["model"] = model,
            ["authType"] = auth.AuthType,
            ["apiKey"] = auth.ApiKey,
            ["oidcIssuer"] = auth.OidcIssuer,
            ["oidcClientId"] = auth.OidcClientId,
            ["oidcScopes"] = auth.OidcScopes,
            ["oidcClientSecret"] = auth.OidcClientSecret
        };

        var preferredSecretKey = KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, provider);
        var existingSecretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.LlmProvider, provider, ct);
        span.SetTag("keyvault.secret.key", preferredSecretKey);
        span.SetTag("gnougo.agent.configure.overwrite", !string.IsNullOrWhiteSpace(existingSecretKey));

        await _keyVaultStore.SaveSecretValueAsync(preferredSecretKey, payload.ToJsonString(), ct);

        if (!string.IsNullOrWhiteSpace(existingSecretKey)
            && !string.Equals(existingSecretKey, preferredSecretKey, StringComparison.OrdinalIgnoreCase))
        {
            await _keyVaultStore.DeleteSecretAsync(existingSecretKey, ct);
        }

        span.SetStatus(ActivityStatusCode.Ok);
    }

    private async Task<LlmProviderConfig?> LoadLlmProviderConfigAsync(string provider, CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.llm.load_existing", "llm.load_existing", ActivityKind.Client);
        span.SetTag("gnougo.agent.configure.target_name", provider);
        span.SetTag("gen_ai.system", provider);
        try
        {
            var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.LlmProvider, provider, ct)
                ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, provider);
            span.SetTag("keyvault.secret.key", secretKey);
            var value = await _keyVaultStore.GetSecretValueAsync(secretKey, ct);

            if (string.IsNullOrWhiteSpace(value))
            {
                span.SetTag("gnougo.agent.configure.found_existing", false);
                span.SetStatus(ActivityStatusCode.Ok);
                return null;
            }

            var config = JsonNode.Parse(value) as JsonObject;
            if (config is null)
            {
                span.SetTag("gnougo.agent.configure.found_existing", false);
                span.SetStatus(ActivityStatusCode.Error, "Stored secret is not a JSON object.");
                return null;
            }

            var result = new LlmProviderConfig(
                Url: ReadConfigString(config, "url") ?? "",
                Model: ReadConfigString(config, "model") ?? "",
                AuthType: ReadConfigString(config, "authType", "auth_type") ?? "none",
                ApiKey: ReadConfigString(config, "apiKey", "api_key") ?? "",
                OidcIssuer: ReadConfigString(config, "oidcIssuer", "oidc_issuer") ?? "",
                OidcClientId: ReadConfigString(config, "oidcClientId", "oidc_client_id") ?? "",
                OidcScopes: ReadConfigString(config, "oidcScopes", "oidc_scopes") ?? "",
                OidcClientSecret: ReadConfigString(config, "oidcClientSecret", "oidc_client_secret") ?? "");
            span.SetTag("gnougo.agent.configure.found_existing", true);
            span.SetTag("gnougo.agent.llm.auth_type", result.AuthType);
            span.SetTag("gen_ai.request.model", result.Model);
            span.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
            {
            _logger.LogDebug(ex, "Could not load existing LLM config for provider '{Provider}'.", provider);
            span.SetStatus(ActivityStatusCode.Error, ex.Message);
            span.SetTag("error.type", ex.GetType().FullName);
            span.SetTag("error.message", ex.Message);
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
        sb.Append($"Configuration will be saved as key: `{KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.LlmProvider, provider)}`");
        return sb.ToString();
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectHttpMcpConfigAsync(
        string runId,
        McpServerConfig? existing,
        Action<McpServerConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.mcp.http.collect", "mcp.http.collect");
        span.SetTag("gnougo.agent.configure.run_id", runId);
        span.SetTag("mcp.transport", "http");
        if (!string.IsNullOrWhiteSpace(existing?.Name))
            span.SetTag("gnougo.agent.configure.target_name", existing.Name);
        JsonNode? response = null;
        var request = CreateFieldsRequest(
            runId,
            existing is null ? "mcp_add.http" : "mcp_edit.http_setup",
            existing is null ? "Configure the HTTP MCP server:" : $"Configure MCP server '{existing.Name}':",
            [
                new HumanInputFieldDef { Name = "name", Type = "string", Required = true, Description = "Server name", Default = existing?.Name ?? "" },
                new HumanInputFieldDef { Name = "description", Type = "string", Required = true, Description = "Short description", Default = existing?.Description ?? "" },
                new HumanInputFieldDef { Name = "url", Type = "string", Required = true, Description = "HTTP endpoint URL", Default = existing?.Url ?? "https://api.githubcopilot.com/mcp/" }
            ]);
        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;
        var fields = ReadFieldResponse(response, request.Fields!);

        response = null;
        var authChoices = new List<string> { "api_key", "oidc", "none" };
        var authRequest = CreateChoiceRequest(runId, "mcp_add.http_auth", "Authentication for this MCP server:", authChoices);
        await foreach (var evt in EmitHumanInputRequestAsync(authRequest, r => response = r, ct))
            yield return evt;

        var authType = ReadChoiceResponse(response) ?? "none";
        span.SetTag("gnougo.agent.mcp.auth_type", authType);
        McpServerConfig? authConfig = null;
        await foreach (var evt in CollectMcpAuthConfigAsync(runId, authType, existing, cfg => authConfig = cfg, ct))
            yield return evt;
        if (authConfig is null)
        {
            setConfig(null);
            yield break;
        }

        setConfig(new McpServerConfig(
            Name: fields.GetValueOrDefault("name") ?? existing?.Name ?? "",
            Transport: "http",
            Description: fields.GetValueOrDefault("description") ?? existing?.Description ?? "",
            Url: fields.GetValueOrDefault("url") ?? existing?.Url ?? "",
            Command: string.Empty,
            Args: [],
            AuthType: authConfig.AuthType,
            ApiKey: authConfig.ApiKey,
            OidcIssuer: authConfig.OidcIssuer,
            OidcClientId: authConfig.OidcClientId,
            OidcScopes: authConfig.OidcScopes,
            OidcClientSecret: authConfig.OidcClientSecret));
        span.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectStdioMcpConfigAsync(
        string runId,
        McpServerConfig? existing,
        Action<McpServerConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.mcp.stdio.collect", "mcp.stdio.collect");
        span.SetTag("gnougo.agent.configure.run_id", runId);
        span.SetTag("mcp.transport", "stdio");
        if (!string.IsNullOrWhiteSpace(existing?.Name))
            span.SetTag("gnougo.agent.configure.target_name", existing.Name);
        JsonNode? response = null;
        var request = CreateFieldsRequest(
            runId,
            existing is null ? "mcp_add.stdio" : "mcp_edit.stdio_setup",
            existing is null ? "Configure the stdio MCP server:" : $"Configure MCP server '{existing.Name}':",
            [
                new HumanInputFieldDef { Name = "name", Type = "string", Required = true, Description = "Server name", Default = existing?.Name ?? "" },
                new HumanInputFieldDef { Name = "description", Type = "string", Required = true, Description = "Short description", Default = existing?.Description ?? "" },
                new HumanInputFieldDef { Name = "command", Type = "string", Required = true, Description = "Command to execute", Default = existing?.Command ?? "dotnet" },
                new HumanInputFieldDef { Name = "args", Type = "string", Required = false, Description = "Arguments (comma-separated)", Default = JoinArgs(existing?.Args) }
            ]);
        await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
            yield return evt;
        var fields = ReadFieldResponse(response, request.Fields!);

        setConfig(new McpServerConfig(
            Name: fields.GetValueOrDefault("name") ?? existing?.Name ?? "",
            Transport: "stdio",
            Description: fields.GetValueOrDefault("description") ?? existing?.Description ?? "",
            Url: string.Empty,
            Command: fields.GetValueOrDefault("command") ?? existing?.Command ?? "dotnet",
            Args: ParseCommaSeparatedArgs(fields.GetValueOrDefault("args")),
            AuthType: "none",
            ApiKey: string.Empty,
            OidcIssuer: string.Empty,
            OidcClientId: string.Empty,
            OidcScopes: string.Empty,
            OidcClientSecret: string.Empty));
        span.SetStatus(ActivityStatusCode.Ok);
    }

    private async IAsyncEnumerable<SmartFlowEvent> CollectMcpAuthConfigAsync(
        string runId,
        string authType,
        McpServerConfig? existing,
        Action<McpServerConfig?> setConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.mcp.auth.collect", "mcp.auth.collect");
        span.SetTag("gnougo.agent.configure.run_id", runId);
        span.SetTag("gnougo.agent.mcp.auth_type", authType);
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
        {
            setConfig(existing is null
                ? new McpServerConfig("", "http", "", "", "", [], "none", "", "", "", "", "")
                : existing with { AuthType = "none", ApiKey = "", OidcIssuer = "", OidcClientId = "", OidcScopes = "", OidcClientSecret = "" });
            span.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        JsonNode? response = null;
        if (string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(
                runId,
                "mcp.auth.api_key",
                "Enter the API key:",
                [ new HumanInputFieldDef { Name = "api_key", Type = "string", Required = true, Description = "API key / bearer token" } ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var apiKey = ReadFieldResponse(response, request.Fields!).GetValueOrDefault("api_key") ?? "";
            setConfig(existing is null
                ? new McpServerConfig("", "http", "", "", "", [], "api_key", apiKey, "", "", "", "")
                : existing with { AuthType = "api_key", ApiKey = apiKey, OidcIssuer = "", OidcClientId = "", OidcScopes = "", OidcClientSecret = "" });
            span.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        if (string.Equals(authType, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateFieldsRequest(
                runId,
                "mcp.auth.oidc",
                "Configure OIDC for this MCP server:",
                [
                    new HumanInputFieldDef { Name = "issuer", Type = "string", Required = true, Description = "OIDC Issuer URL", Default = existing?.OidcIssuer ?? "" },
                    new HumanInputFieldDef { Name = "client_id", Type = "string", Required = true, Description = "Client ID", Default = existing?.OidcClientId ?? "" },
                    new HumanInputFieldDef { Name = "scopes", Type = "string", Required = true, Description = "Scopes", Default = existing?.OidcScopes ?? "" },
                    new HumanInputFieldDef { Name = "client_secret", Type = "string", Required = false, Description = "Client secret" }
                ]);
            await foreach (var evt in EmitHumanInputRequestAsync(request, r => response = r, ct))
                yield return evt;
            var fields = ReadFieldResponse(response, request.Fields!);
            setConfig(existing is null
                ? new McpServerConfig("", "http", "", "", "", [], "oidc", "", fields.GetValueOrDefault("issuer") ?? "", fields.GetValueOrDefault("client_id") ?? "", fields.GetValueOrDefault("scopes") ?? "", fields.GetValueOrDefault("client_secret") ?? "")
                : existing with
                {
                    AuthType = "oidc",
                    ApiKey = "",
                    OidcIssuer = fields.GetValueOrDefault("issuer") ?? "",
                    OidcClientId = fields.GetValueOrDefault("client_id") ?? "",
                    OidcScopes = fields.GetValueOrDefault("scopes") ?? "",
                    OidcClientSecret = fields.GetValueOrDefault("client_secret") ?? ""
                });
            span.SetStatus(ActivityStatusCode.Ok);
            yield break;
        }

        setConfig(null);
        span.SetStatus(ActivityStatusCode.Error, $"Unsupported MCP auth type '{authType}'.");
    }

    private async Task SaveMcpServerConfigAsync(McpServerConfig config, CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.mcp.save", "mcp.save", ActivityKind.Client);
        span.SetTag("gnougo.agent.configure.target_name", config.Name);
        span.SetTag("mcp.server.name", config.Name);
        span.SetTag("mcp.transport", config.Transport);
        span.SetTag("gnougo.agent.mcp.auth_type", config.AuthType);
        var payload = new JsonObject
        {
            ["name"] = config.Name,
            ["transport"] = config.Transport,
            ["description"] = config.Description,
            ["url"] = config.Url,
            ["command"] = config.Command,
            ["args"] = new JsonArray(config.Args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["authType"] = config.AuthType,
            ["apiKey"] = config.ApiKey,
            ["oidcIssuer"] = config.OidcIssuer,
            ["oidcClientId"] = config.OidcClientId,
            ["oidcScopes"] = config.OidcScopes,
            ["oidcClientSecret"] = config.OidcClientSecret
        };

        var preferredSecretKey = KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.McpServer, config.Name);
        var existingSecretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.McpServer, config.Name, ct);
        span.SetTag("keyvault.secret.key", preferredSecretKey);
        span.SetTag("gnougo.agent.configure.overwrite", !string.IsNullOrWhiteSpace(existingSecretKey));

        await _keyVaultStore.SaveSecretValueAsync(preferredSecretKey, payload.ToJsonString(), ct);

        if (!string.IsNullOrWhiteSpace(existingSecretKey)
            && !string.Equals(existingSecretKey, preferredSecretKey, StringComparison.OrdinalIgnoreCase))
        {
            await _keyVaultStore.DeleteSecretAsync(existingSecretKey, ct);
        }

        span.SetStatus(ActivityStatusCode.Ok);
    }

    private async Task<McpServerConfig?> LoadMcpServerConfigAsync(string name, CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.mcp.load_existing", "mcp.load_existing", ActivityKind.Client);
        span.SetTag("gnougo.agent.configure.target_name", name);
        var secretKey = await ResolveSecretKeyAsync(KeyVaultConfigSecretKind.McpServer, name, ct)
            ?? KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.McpServer, name);
        span.SetTag("keyvault.secret.key", secretKey);
        var value = await _keyVaultStore.GetSecretValueAsync(secretKey, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            span.SetTag("gnougo.agent.configure.found_existing", false);
            span.SetStatus(ActivityStatusCode.Ok);
            return null;
        }

        JsonObject? config;
        try
        {
            config = JsonNode.Parse(value) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse MCP server configuration '{Name}'.", name);
            span.SetStatus(ActivityStatusCode.Error, ex.Message);
            span.SetTag("error.type", ex.GetType().FullName);
            span.SetTag("error.message", ex.Message);
            return null;
        }

        if (config is null)
        {
            span.SetTag("gnougo.agent.configure.found_existing", false);
            span.SetStatus(ActivityStatusCode.Error, "Stored secret is not a JSON object.");
            return null;
        }

        var result = new McpServerConfig(
            Name: ReadConfigString(config, "name") ?? name,
            Transport: ReadConfigString(config, "transport") ?? "http",
            Description: ReadConfigString(config, "description") ?? "",
            Url: ReadConfigString(config, "url") ?? "",
            Command: ReadConfigString(config, "command") ?? "",
            Args: ParseJsonArgs(config["args"]),
            AuthType: ReadConfigString(config, "authType", "auth_type") ?? "none",
            ApiKey: ReadConfigString(config, "apiKey", "api_key") ?? "",
            OidcIssuer: ReadConfigString(config, "oidcIssuer", "oidc_issuer") ?? "",
            OidcClientId: ReadConfigString(config, "oidcClientId", "oidc_client_id") ?? "",
            OidcScopes: ReadConfigString(config, "oidcScopes", "oidc_scopes") ?? "",
            OidcClientSecret: ReadConfigString(config, "oidcClientSecret", "oidc_client_secret") ?? "");
        span.SetTag("gnougo.agent.configure.found_existing", true);
        span.SetTag("mcp.transport", result.Transport);
        span.SetTag("gnougo.agent.mcp.auth_type", result.AuthType);
        span.SetStatus(ActivityStatusCode.Ok);
        return result;
    }

    private static string RenderMcpConfigSummary(McpServerConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ✅ MCP Server Configured");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Name | {EscapeMarkdownCell(config.Name)} |");
        sb.AppendLine($"| Transport | {EscapeMarkdownCell(config.Transport)} |");
        sb.AppendLine($"| Description | {EscapeMarkdownCell(config.Description)} |");
        if (!string.IsNullOrWhiteSpace(config.Url))
            sb.AppendLine($"| URL | {EscapeMarkdownCell(config.Url)} |");
        if (!string.IsNullOrWhiteSpace(config.Command))
        {
            sb.AppendLine($"| Command | {EscapeMarkdownCell(config.Command)} |");
            sb.AppendLine($"| Args | {EscapeMarkdownCell(JoinArgs(config.Args))} |");
        }
        sb.AppendLine($"| Auth | {EscapeMarkdownCell(config.AuthType)} |");
        sb.AppendLine();
        sb.Append($"Configuration will be saved as key: `{KeyVaultConfigNaming.BuildSecretKey(KeyVaultConfigSecretKind.McpServer, config.Name)}`");
        return sb.ToString();
    }

    private static List<string> ParseCommaSeparatedArgs(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

    private static List<string> ParseJsonArgs(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
        }

        return ParseCommaSeparatedArgs(node?.GetValue<string>());
    }

    private static string JoinArgs(IReadOnlyList<string>? args)
        => args is { Count: > 0 } ? string.Join(",", args) : string.Empty;

    private async Task<string?> ResolveSecretKeyAsync(KeyVaultConfigSecretKind kind, string logicalName, CancellationToken ct)
    {
        var secrets = await _keyVaultStore.ListSecretsAsync(ct);
        return KeyVaultConfigNaming.ResolveExistingSecretKey(secrets, kind, logicalName);
    }

    private sealed record ProviderDefaults(string Url, string Model, IReadOnlyList<string> AuthModes);
    private sealed record LlmProviderConfig(string Url, string Model, string AuthType, string ApiKey, string OidcIssuer, string OidcClientId, string OidcScopes, string OidcClientSecret);
    private sealed record McpServerConfig(string Name, string Transport, string Description, string Url, string Command, IReadOnlyList<string> Args, string AuthType, string ApiKey, string OidcIssuer, string OidcClientId, string OidcScopes, string OidcClientSecret);
    private sealed record EmbeddingProviderConfig(string Name, string Provider, string? Model, string? EndpointUrl, string? BaseUrl, string? ApiKey, string? ApiKeySecretKey, int Dimensions);
    private sealed record ConfiguredLlmProvider(string Provider, KeyVaultSecretSummary Secret, LlmProviderConfig Config);
    private sealed record ConfiguredEmbeddingConfig(string Name, KeyVaultSecretSummary Secret, EmbeddingProviderConfig Config);

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
        var defaultProvider = _optionsStore.Current.DefaultProvider;
        foreach (var provider in configuredProviders)
            sb.AppendLine($"- `{provider}`{(string.Equals(provider, defaultProvider, StringComparison.OrdinalIgnoreCase) ? " (default)" : "")}");

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

    private static string RenderWizardHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 🔧 GnOuGo Configuration Wizard");
        sb.AppendLine();
        sb.AppendLine("Available commands:");
        sb.AppendLine();
        sb.AppendLine("| Command | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `/llm` | Show LLM command documentation |");
        sb.AppendLine("| `/llm list` | List configured LLM providers |");
        sb.AppendLine("| `/llm models <name>` | List available models for a configured LLM provider |");
        sb.AppendLine("| `/llm add` | Configure a new LLM provider |");
        sb.AppendLine("| `/llm default [name]` | Set or change the default LLM provider/model |");
        sb.AppendLine("| `/llm edit <name>` | Edit an existing LLM provider |");
        sb.AppendLine("| `/llm remove <name>` | Remove an LLM provider |");
        sb.AppendLine("| `/embedding` | Show embedding model command documentation |");
        sb.AppendLine("| `/embedding list` | List configured embedding models |");
        sb.AppendLine("| `/embedding add` | Configure a new embedding model |");
        sb.AppendLine("| `/embedding default [name]` | Set or change the default embedding model |");
        sb.AppendLine("| `/embedding edit <name>` | Edit an embedding model configuration |");
        sb.AppendLine("| `/embedding remove <name>` | Remove an embedding model configuration |");
        sb.AppendLine("| `/mcp` | Show MCP command documentation |");
        sb.AppendLine("| `/mcp list` | List configured MCP servers |");
        sb.AppendLine("| `/mcp add` | Add a new MCP server |");
        sb.AppendLine("| `/mcp edit <name>` | Edit an existing MCP server |");
        sb.AppendLine("| `/mcp remove <name>` | Remove an MCP server |");
        sb.AppendLine("| `/status` | Display current configuration summary |");
        sb.AppendLine();
        sb.Append("Type a command in the chat to get started.");
        return sb.ToString();
    }

    private static string RenderLlmHelp(string command)
    {
        var sb = new StringBuilder();
        if (!string.Equals(command, "/llm", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"❌ Unknown `/llm` command: `{EscapeBackticks(command)}`");
            sb.AppendLine();
        }

        sb.AppendLine("# 🤖 LLM Provider Commands");
        sb.AppendLine();
        sb.AppendLine("| Command | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `/llm list` | List all configured LLM providers |");
        sb.AppendLine("| `/llm models <name>` | Fetch the live model list for a configured provider |");
        sb.AppendLine("| `/llm add` | Configure a new LLM provider |");
        sb.AppendLine("| `/llm default [name]` | Set the default provider/model used by workflows |");
        sb.AppendLine("| `/llm edit <name>` | Edit an existing LLM provider configuration |");
        sb.AppendLine("| `/llm remove <name>` | Remove an LLM provider |");
        sb.AppendLine();
        sb.AppendLine("**Authentication modes:**");
        sb.AppendLine("- **API Key** — static secret");
        sb.AppendLine("- **OpenID Connect** — OAuth2 client_credentials flow");
        sb.AppendLine("- **Copilot / Env** — resolved from environment variables");
        sb.AppendLine();
        sb.Append($"All configurations are stored encrypted in KeyVault using the .NET convention `{KeyVaultConfigNaming.GetDisplayConvention(KeyVaultConfigSecretKind.LlmProvider)}`.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderMcpHelp(string command)
    {
        var sb = new StringBuilder();
        if (!string.Equals(command, "/mcp", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"❌ Unknown `/mcp` command: `{EscapeBackticks(command)}`");
            sb.AppendLine();
        }

        sb.AppendLine("# 🔌 MCP Server Commands");
        sb.AppendLine();
        sb.AppendLine("| Command | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `/mcp list` | List all configured MCP servers |");
        sb.AppendLine("| `/mcp add` | Add a new MCP server |");
        sb.AppendLine("| `/mcp edit <name>` | Edit an existing MCP server |");
        sb.AppendLine("| `/mcp remove <name>` | Remove an MCP server |");
        sb.AppendLine();
        sb.AppendLine("**Transport types:**");
        sb.AppendLine("- **HTTP** — connect to an HTTP MCP endpoint with optional auth");
        sb.AppendLine("- **stdio** — launch a local process (for example `dotnet run`)");
        sb.AppendLine();
        sb.Append($"All configurations are stored encrypted in KeyVault using the .NET convention `{KeyVaultConfigNaming.GetDisplayConvention(KeyVaultConfigSecretKind.McpServer)}`.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderEmbeddingHelp(string command)
    {
        var sb = new StringBuilder();
        if (!string.Equals(command, "/embedding", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"❌ Unknown `/embedding` command: `{EscapeBackticks(command)}`");
            sb.AppendLine();
        }

        sb.AppendLine("# 🧬 Embedding Model Commands");
        sb.AppendLine();
        sb.AppendLine("| Command | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `/embedding list` | List all configured embedding models |");
        sb.AppendLine("| `/embedding add` | Configure a new embedding model |");
        sb.AppendLine("| `/embedding default [name]` | Set the default embedding model used by document ingestion/search |");
        sb.AppendLine("| `/embedding edit <name>` | Edit an embedding model configuration |");
        sb.AppendLine("| `/embedding remove <name>` | Remove an embedding model configuration |");
        sb.AppendLine();
        sb.AppendLine("**Providers:** `openai`, `openai-compatible`, `ollama`, `hash`.");
        sb.AppendLine();
        sb.Append($"All configurations are stored encrypted in KeyVault using the .NET convention `{KeyVaultConfigNaming.GetDisplayConvention(KeyVaultConfigSecretKind.EmbeddingConfig)}`.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderModelCatalogError(string provider, Exception error)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 🧠 Available Models for `{provider}`");
        sb.AppendLine();
        sb.AppendLine("❌ Live model discovery failed.");
        sb.AppendLine();
        foreach (var line in FormatModelCatalogErrorLines(error))
            sb.AppendLine($"> {line}");
        sb.AppendLine();
        sb.Append("Check the provider URL and credentials, then retry `/llm models <provider>`.");
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> FormatModelCatalogErrorLines(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            var lines = aggregate.Flatten().InnerExceptions
                .Select(ex => SanitizeErrorLine(ex.Message))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();

            if (lines.Count > 0)
                return lines;
        }

        return [SanitizeErrorLine(error.Message)];
    }

    private static string SanitizeErrorLine(string message)
        => message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private async Task<List<KeyVaultSecretSummary>> LoadKeyVaultSecretsAsync(CancellationToken ct)
    {
        using var span = StartNestedTrace("keyvault.list_secrets", "keyvault.list_secrets", ActivityKind.Client);
        var secrets = (await _keyVaultStore.ListSecretsAsync(ct)).ToList();
        span.SetTag("gnougo.agent.keyvault.secret_count", secrets.Count);
        span.SetStatus(ActivityStatusCode.Ok);
        return secrets;
    }

    private async Task<List<ConfiguredLlmProvider>> LoadConfiguredLlmProvidersAsync(CancellationToken ct)
    {
        using var span = StartNestedTrace("configure.providers.llm.load_configured", "llm.load_configured");
        var secrets = await LoadKeyVaultSecretsAsync(ct);
        var providers = new List<ConfiguredLlmProvider>();

        foreach (var secret in KeyVaultConfigNaming.SelectPreferredSecrets(secrets, KeyVaultConfigSecretKind.LlmProvider))
        {
            using var secretSpan = StartNestedTrace("keyvault.get_secret", "keyvault.get_secret", ActivityKind.Client);
            secretSpan.SetTag("keyvault.secret.key", secret.Key);

            var raw = await _keyVaultStore.GetSecretValueAsync(secret.Key, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                secretSpan.SetTag("keyvault.secret.empty", true);
                secretSpan.SetStatus(ActivityStatusCode.Ok);
                continue;
            }

            JsonObject? configJson;
            try
            {
                configJson = JsonNode.Parse(raw) as JsonObject;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse configured LLM provider secret '{SecretKey}'.", secret.Key);
                secretSpan.SetStatus(ActivityStatusCode.Error, ex.Message);
                secretSpan.SetTag("error.type", ex.GetType().FullName);
                secretSpan.SetTag("error.message", ex.Message);
                continue;
            }

            if (configJson is null)
            {
                secretSpan.SetTag("keyvault.secret.invalid_shape", true);
                secretSpan.SetStatus(ActivityStatusCode.Error, "Secret payload is not a JSON object.");
                continue;
            }

            var provider = ReadConfigString(configJson, "provider")
                ?? KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.LlmProvider, secret.Key)
                ?? string.Empty;
            var config = new LlmProviderConfig(
                Url: ReadConfigString(configJson, "url") ?? string.Empty,
                Model: ReadConfigString(configJson, "model") ?? string.Empty,
                AuthType: ReadConfigString(configJson, "authType", "auth_type") ?? "none",
                ApiKey: ReadConfigString(configJson, "apiKey", "api_key") ?? string.Empty,
                OidcIssuer: ReadConfigString(configJson, "oidcIssuer", "oidc_issuer") ?? string.Empty,
                OidcClientId: ReadConfigString(configJson, "oidcClientId", "oidc_client_id") ?? string.Empty,
                OidcScopes: ReadConfigString(configJson, "oidcScopes", "oidc_scopes") ?? string.Empty,
                OidcClientSecret: ReadConfigString(configJson, "oidcClientSecret", "oidc_client_secret") ?? string.Empty);

            providers.Add(new ConfiguredLlmProvider(provider, secret, config));
            secretSpan.SetTag("gnougo.agent.llm.provider", provider);
            secretSpan.SetStatus(ActivityStatusCode.Ok);
        }

        span.SetTag("gnougo.agent.llm.provider_count", providers.Count);
        span.SetStatus(ActivityStatusCode.Ok);
        return providers;
    }

    private async Task<string> RenderConfiguredLlmProvidersAsync(CancellationToken ct)
    {
        var providers = await LoadConfiguredLlmProvidersAsync(ct);
        if (providers.Count == 0)
            return "# 🤖 Configured LLM Providers\n\nNo LLM providers configured yet. Use `/llm add` to get started.";

        var currentDefaultProvider = _optionsStore.Current.DefaultProvider;
        var currentDefaultModel = _optionsStore.Current.DefaultModel;
        var sb = new StringBuilder();
        sb.AppendLine("# 🤖 Configured LLM Providers");
        sb.AppendLine();
        sb.AppendLine("| Provider | Default | Model | Key | Version | Stored |");
        sb.AppendLine("|----------|---------|-------|-----|---------|--------|");

        foreach (var provider in providers
                     .OrderByDescending(p => string.Equals(p.Provider, currentDefaultProvider, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(p => p.Provider, StringComparer.OrdinalIgnoreCase))
        {
            var isDefault = string.Equals(provider.Provider, currentDefaultProvider, StringComparison.OrdinalIgnoreCase);
            var model = string.IsNullOrWhiteSpace(provider.Config.Model)
                ? isDefault ? currentDefaultModel : ""
                : provider.Config.Model;
            sb.AppendLine(
                $"| {EscapeMarkdownCell(provider.Provider)} | {(isDefault ? "✅ yes" : "") } | {EscapeMarkdownCell(model)} | `{EscapeBackticks(provider.Secret.Key)}` | {provider.Secret.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(provider.Secret.CreatedAt))} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderSecretTable(
        IReadOnlyList<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind,
        string title,
        string emptyMessage)
    {
        var matching = KeyVaultConfigNaming.SelectPreferredSecrets(secrets, kind);

        if (matching.Count == 0)
            return $"{title}\n\n{emptyMessage}";

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("| Name | Key | Version | Stored |") ;
        sb.AppendLine("|------|-----|---------|--------|");

        foreach (var secret in matching)
        {
            var name = KeyVaultConfigNaming.TryGetLogicalName(kind, secret.Key) ?? secret.Key;
            var stored = FormatTimestamp(secret.CreatedAt);
            sb.AppendLine($"| {EscapeMarkdownCell(name)} | `{EscapeBackticks(secret.Key)}` | {secret.LatestVersion} | {EscapeMarkdownCell(stored)} |");
        }

        return sb.ToString().TrimEnd();
    }

    private string RenderStatus(IReadOnlyList<KeyVaultSecretSummary> secrets)
    {
        var llms = secrets
            .Where(s => KeyVaultConfigNaming.MatchesSecretKey(KeyVaultConfigSecretKind.LlmProvider, s.Key))
            .ToList();

        var mcps = secrets
            .Where(s => KeyVaultConfigNaming.MatchesSecretKey(KeyVaultConfigSecretKind.McpServer, s.Key))
            .ToList();

        var embeddings = secrets
            .Where(s => KeyVaultConfigNaming.MatchesSecretKey(KeyVaultConfigSecretKind.EmbeddingConfig, s.Key))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 📊 Current Configuration Status");
        sb.AppendLine();
        sb.AppendLine("## 🤖 LLM Providers");
        sb.AppendLine();
        sb.AppendLine($"Default provider: `{EscapeBackticks(_optionsStore.Current.DefaultProvider)}`");
        sb.AppendLine($"Default model: `{EscapeBackticks(_optionsStore.Current.DefaultModel)}`");
        sb.AppendLine();

        if (llms.Count == 0)
        {
            sb.AppendLine("No LLM providers configured yet.");
        }
        else
        {
            sb.AppendLine("| Provider | KeyVault Key | Version | Stored |");
            sb.AppendLine("|----------|--------------|---------|--------|");
            foreach (var item in KeyVaultConfigNaming.SelectPreferredSecrets(llms, KeyVaultConfigSecretKind.LlmProvider))
            {
                var provider = KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.LlmProvider, item.Key) ?? item.Key;
                sb.AppendLine($"| {EscapeMarkdownCell(provider)} | `{EscapeBackticks(item.Key)}` | {item.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(item.CreatedAt))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 🧬 Embedding Models");
        sb.AppendLine();

        if (embeddings.Count == 0)
        {
            sb.AppendLine("No embedding models configured yet.");
        }
        else
        {
            sb.AppendLine("| Name | KeyVault Key | Version | Stored |");
            sb.AppendLine("|------|--------------|---------|--------|");
            foreach (var item in KeyVaultConfigNaming.SelectPreferredSecrets(embeddings, KeyVaultConfigSecretKind.EmbeddingConfig))
            {
                var embedding = KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.EmbeddingConfig, item.Key) ?? item.Key;
                sb.AppendLine($"| {EscapeMarkdownCell(embedding)} | `{EscapeBackticks(item.Key)}` | {item.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(item.CreatedAt))} |");
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
            foreach (var item in KeyVaultConfigNaming.SelectPreferredSecrets(mcps, KeyVaultConfigSecretKind.McpServer))
            {
                var server = KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.McpServer, item.Key) ?? item.Key;
                sb.AppendLine($"| {EscapeMarkdownCell(server)} | `{EscapeBackticks(item.Key)}` | {item.LatestVersion} | {EscapeMarkdownCell(FormatTimestamp(item.CreatedAt))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Use `/llm add`, `/embedding add` or `/mcp add` to get started.");
        return sb.ToString().TrimEnd();
    }

    private ConfiguredLlmProvider? ResolveDefaultSelectionProvider(
        IReadOnlyList<ConfiguredLlmProvider> providers,
        string? requestedProvider)
    {
        if (!string.IsNullOrWhiteSpace(requestedProvider))
        {
            return providers.FirstOrDefault(p =>
                string.Equals(p.Provider, requestedProvider, StringComparison.OrdinalIgnoreCase));
        }

        var defaultProvider = _optionsStore.Current.DefaultProvider;
        return providers.FirstOrDefault(p =>
                   string.Equals(p.Provider, defaultProvider, StringComparison.OrdinalIgnoreCase))
               ?? providers.OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string RenderDefaultProviderSummary(string provider, string model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ⭐ Review default LLM provider");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Provider | {EscapeMarkdownCell(provider)} |");
        sb.AppendLine($"| Model | {EscapeMarkdownCell(model)} |");
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

    private static string? ReadConfigString(JsonObject config, string propertyName, string? legacyPropertyName = null)
        => TryGetString(config, propertyName)
           ?? (legacyPropertyName is null ? null : TryGetString(config, legacyPropertyName));

    private static string? TryGetString(JsonObject config, string propertyName)
        => config[propertyName]?.GetValue<string>();


    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool TryApplyLlmConfig(JsonNode outputs)
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
        using var span = StartNestedTrace("configure.providers.llm.validate", "llm.validate", ActivityKind.Client);
        span.SetTag("gen_ai.system", provider);
        span.SetTag("gen_ai.request.model", model);
        span.SetTag("gnougo.agent.llm.auth_type", authType);

        // Skip validation for providers that don't require a key
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
        {
            span.SetTag("gnougo.agent.validation.skipped", true);
            span.SetTag("gnougo.agent.validation.result", "skipped");
            span.SetStatus(ActivityStatusCode.Ok);
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
            span.SetTag("error.type", ex.GetType().FullName);
            span.SetTag("error.message", ex.Message);
        }

        if (validationError is null)
        {
            span.SetTag("gnougo.agent.validation.result", "success");
            span.SetStatus(ActivityStatusCode.Ok);
            yield return new SmartFlowEvent("thinking:response",
                $"✅ Credentials validated. Provider '{provider}' is ready.");
        }
        else
        {
            span.SetTag("gnougo.agent.validation.result", "failed");
            span.SetStatus(ActivityStatusCode.Error, validationError);
            yield return new SmartFlowEvent("thinking:response",
                $"⚠️ Validation failed for '{provider}': {validationError}\n\nThe configuration was saved — run `/llm` again to correct it.");
        }
    }
}

