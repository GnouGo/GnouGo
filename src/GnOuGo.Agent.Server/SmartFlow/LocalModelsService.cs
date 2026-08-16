using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

public interface ILlmModelCatalogCacheInvalidator
{
    void Invalidate(string provider);
}

/// <summary>Handles trusted, non-LLM slash commands for embedded model lifecycle.</summary>
public sealed class LocalModelsService
{
    private readonly ILocalModelManager _models;
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly AgentUserConfigMcpClient? _userConfigClient;
    private readonly ILlmModelCatalogCacheInvalidator? _catalogCache;
    private readonly ILogger<LocalModelsService> _logger;

    public LocalModelsService(
        ILocalModelManager models,
        LLMRuntimeOptionsStore optionsStore,
        ILogger<LocalModelsService> logger,
        AgentUserConfigMcpClient? userConfigClient = null,
        ILlmModelCatalogCacheInvalidator? catalogCache = null)
    {
        _models = models;
        _optionsStore = optionsStore;
        _logger = logger;
        _userConfigClient = userConfigClient;
        _catalogCache = catalogCache;
    }

    public async IAsyncEnumerable<SmartFlowEvent> ExecuteAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = parts.Length < 2 ? "list" : parts[1].ToLowerInvariant();
        var modelId = parts.Length >= 3 ? parts[2] : "qwen3:0.6b";
        var force = parts.Skip(3).Any(static part => string.Equals(part, "--force", StringComparison.OrdinalIgnoreCase));

        switch (action)
        {
            case "list":
                yield return new SmartFlowEvent("answer", RenderList(await _models.ListAsync(ct).ConfigureAwait(false)));
                yield break;
            case "install":
                await foreach (var evt in InstallAsync(modelId, ct).ConfigureAwait(false))
                    yield return evt;
                yield break;
            case "remove":
                yield return new SmartFlowEvent("answer", await RemoveAsync(modelId, force, ct).ConfigureAwait(false));
                yield break;
            default:
                yield return new SmartFlowEvent("answer", RenderUsage());
                yield break;
        }
    }

    private async IAsyncEnumerable<SmartFlowEvent> InstallAsync(
        string modelId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SmartFlowEvent("thinking:thinking", $"Downloading and verifying `{modelId}`…");

        var progressChannel = Channel.CreateUnbounded<LocalModelProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var lastReportedBucket = -1;
        var progress = new InlineProgress<LocalModelProgress>(value =>
        {
            var bucket = Math.Min(20, (int)(value.Percentage / 5));
            if (bucket <= lastReportedBucket && value.Percentage < 100)
                return;
            lastReportedBucket = bucket;
            progressChannel.Writer.TryWrite(value);
        });

        var installTask = RunInstallAsync(modelId, progress, progressChannel.Writer, ct);
        await foreach (var value in progressChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return new SmartFlowEvent(
                "thinking:thinking",
                $"⬇️ `{value.ModelId}` {value.Percentage:F0}% ({FormatBytes(value.DownloadedBytes)} / {FormatBytes(value.TotalBytes)})");
        }

        var outcome = await installTask.ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            _logger.LogWarning(outcome.Error, "Local model installation failed for {ModelId}.", modelId);
            yield return new SmartFlowEvent("answer", $"❌ Could not install `{modelId}`: {outcome.Error.Message}");
            yield break;
        }

        _catalogCache?.Invalidate(ResolveLocalProviderKey() ?? "Local");
        var activated = await ActivateWhenCurrentDefaultIsUnusableAsync(outcome.Info!, ct).ConfigureAwait(false);
        yield return new SmartFlowEvent(
            "answer",
            $"✅ Installed `{outcome.Info!.Id}` ({FormatBytes(outcome.Info.TotalBytes)}), license {outcome.Info.License}." +
            (activated
                ? " It is now the active default because the previous provider was not usable."
                : " The existing usable default was preserved; use `/llm default local qwen3:0.6b` to switch."));
    }

    private async Task<string> RemoveAsync(string modelId, bool force, CancellationToken ct)
    {
        var options = _optionsStore.Current;
        var activeProvider = options.ResolveProvider(options.DefaultProvider);
        var isActive = activeProvider is not null
                       && string.Equals(activeProvider.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(options.DefaultModel, modelId, StringComparison.OrdinalIgnoreCase);
        if (isActive && !force)
            return $"❌ `{modelId}` is the active default. Switch providers first or run `/models remove {modelId} --force`.";

        if (isActive)
            await RestoreNonLocalDefaultAsync(options, ct).ConfigureAwait(false);

        var removed = await _models.RemoveAsync(modelId, ct).ConfigureAwait(false);
        _catalogCache?.Invalidate(ResolveLocalProviderKey() ?? "Local");
        return removed
            ? $"✅ Removed `{modelId}` and any partial download."
            : $"ℹ️ `{modelId}` is not installed.";
    }

    private async Task<bool> ActivateWhenCurrentDefaultIsUnusableAsync(LocalModelInfo installed, CancellationToken ct)
    {
        var current = _optionsStore.Current;
        if (await IsDefaultUsableAsync(current, ct).ConfigureAwait(false))
            return false;

        var providerKey = ResolveLocalProviderKey();
        if (providerKey is null || !_optionsStore.SetDefaultProvider(providerKey, installed.Id))
            return false;

        if (_userConfigClient is not null)
            await _userConfigClient.SetAsync(providerKey, installed.Id, ct: ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsDefaultUsableAsync(LLMOptions options, CancellationToken ct)
    {
        var provider = options.ResolveProvider(options.DefaultProvider);
        if (provider is null || string.IsNullOrWhiteSpace(options.DefaultModel))
            return false;
        if (string.Equals(provider.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase))
        {
            var models = await _models.ListAsync(ct).ConfigureAwait(false);
            return models.Any(model =>
                model.Status == LocalModelStatus.Installed
                && string.Equals(model.Id, options.DefaultModel, StringComparison.OrdinalIgnoreCase));
        }

        return HasUsableRemoteConfiguration(provider);
    }

    private static bool HasUsableRemoteConfiguration(ModelProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Url))
            return false;
        if (string.Equals(provider.ResolvedType, "ollama", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            return true;
        if (!string.IsNullOrWhiteSpace(provider.Issuer)
            && !string.IsNullOrWhiteSpace(provider.ClientId)
            && !string.IsNullOrWhiteSpace(provider.Scopes))
            return true;

        return provider.ResolvedType switch
        {
            "openai" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            "anthropic" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            "copilot" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"))
                         || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_API_KEY")),
            _ => false
        };
    }

    private async Task RestoreNonLocalDefaultAsync(LLMOptions options, CancellationToken ct)
    {
        var replacementKey = options.Fallback is { Provider.Length: > 0 }
            && options.ResolveProvider(options.Fallback.Provider) is { } fallbackProvider
            && !string.Equals(fallbackProvider.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase)
                ? options.Models.Keys.First(key => string.Equals(key, options.Fallback.Provider, StringComparison.OrdinalIgnoreCase))
                : options.Models.FirstOrDefault(entry =>
                    !string.Equals(entry.Value.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase)).Key;

        if (string.IsNullOrWhiteSpace(replacementKey))
            return;

        var replacement = options.ResolveProvider(replacementKey)!;
        var replacementModel = options.Fallback is { Model.Length: > 0 }
                               && string.Equals(options.Fallback.Provider, replacementKey, StringComparison.OrdinalIgnoreCase)
            ? options.Fallback.Model
            : replacement.ResolvedType switch
            {
                "anthropic" => "claude-sonnet-4-20250514",
                "ollama" => "llama3",
                _ => "gpt-4o-mini"
            };

        _optionsStore.SetDefaultProvider(replacementKey, replacementModel);
        if (_userConfigClient is not null)
        {
            if (HasUsableRemoteConfiguration(replacement))
                await _userConfigClient.SetAsync(replacementKey, replacementModel, ct: ct).ConfigureAwait(false);
            else
                await _userConfigClient.SetAsync(clearDefaultLlm: true, ct: ct).ConfigureAwait(false);
        }
    }

    private string? ResolveLocalProviderKey()
        => _optionsStore.Current.Models.FirstOrDefault(entry =>
            string.Equals(entry.Value.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase)).Key;

    private async Task<InstallOutcome> RunInstallAsync(
        string modelId,
        IProgress<LocalModelProgress> progress,
        ChannelWriter<LocalModelProgress> writer,
        CancellationToken ct)
    {
        try
        {
            return new InstallOutcome(await _models.InstallAsync(modelId, progress, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new InstallOutcome(null, ex);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static string RenderList(IReadOnlyList<LocalModelInfo> models)
    {
        var lines = new List<string>
        {
            "# Embedded Local Models",
            "",
            "| Model | Status | Downloaded | Size | License |",
            "|---|---|---:|---:|---|"
        };
        lines.AddRange(models.Select(model =>
            $"| `{model.Id}` | {model.Status} | {FormatBytes(model.DownloadedBytes)} | {FormatBytes(model.TotalBytes)} | {model.License} |"));
        lines.Add("");
        lines.Add("Use `/models install qwen3:0.6b` to install the portable model.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderUsage()
        => "Use `/models list`, `/models install qwen3:0.6b`, or `/models remove qwen3:0.6b`.";

    private static string FormatBytes(long bytes)
        => $"{bytes / 1024d / 1024d:F1} MiB";

    private sealed record InstallOutcome(LocalModelInfo? Info, Exception? Error);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
