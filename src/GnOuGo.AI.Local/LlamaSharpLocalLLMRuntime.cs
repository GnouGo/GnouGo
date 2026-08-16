using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.AI.Core;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.AI.Local;

/// <summary>In-process LLamaSharp/llama.cpp implementation of the local runtime contract.</summary>
public sealed partial class LlamaSharpLocalLLMRuntime : ILocalLLMRuntime, IAsyncDisposable
{
    private static readonly ActivitySource ActivitySource = new("GnOuGo.AI.Local.Inference");
    private static readonly Meter Meter = new("GnOuGo.AI.Local.Inference");
    private static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>(
        "gnougo.local_llm.inference.duration",
        "s");
    private static readonly Histogram<double> ModelLoadDuration = Meter.CreateHistogram<double>(
        "gnougo.local_llm.model_load.duration",
        "s");

    private readonly string _modelsDirectory;
    private readonly LocalLLMOptions _options;
    private readonly ILogger<LlamaSharpLocalLLMRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _modelParameters;
    private string? _loadedModelId;

    public LlamaSharpLocalLLMRuntime(
        string modelsDirectory,
        IOptions<LocalLLMOptions>? options = null,
        ILogger<LlamaSharpLocalLLMRuntime>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory))
            throw new ArgumentException("A model directory is required.", nameof(modelsDirectory));

        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _options = options?.Value ?? new LocalLLMOptions();
        _logger = logger ?? NullLogger<LlamaSharpLocalLLMRuntime>.Instance;
    }

    public async Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = LocalModelCatalog.Resolve(request.Model);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        long startedAt = 0;
        StatelessExecutor? callExecutor = null;
        try
        {
            var (weights, modelParameters) = await GetOrLoadModelAsync(entry, ct).ConfigureAwait(false);
            startedAt = Stopwatch.GetTimestamp();
            var prompt = BuildPrompt(request);
            callExecutor = new StatelessExecutor(weights, modelParameters, _logger)
            {
                ApplyTemplate = true,
                SystemMessage = BuildSystemMessage(request)
            };

            using var activity = ActivitySource.StartActivity("local_llm.infer");
            activity?.SetTag("gen_ai.provider.name", LocalLLMProvider.Type);
            activity?.SetTag("gen_ai.request.model", entry.Id);
            activity?.SetTag("gen_ai.request.max_tokens", ResolveMaxTokens(request));
            activity?.SetTag("gnougo.local.acceleration", ResolveAccelerationName());

            using var sampling = CreateSamplingPipeline(request);
            var inference = new InferenceParams
            {
                MaxTokens = ResolveMaxTokens(request),
                SamplingPipeline = sampling,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"]
            };

            var output = new StringBuilder();
            await foreach (var chunk in callExecutor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
                output.Append(chunk);

            var text = StripThinking(output.ToString()).Trim();
            var toolCalls = ParseToolCalls(text);
            var json = TryParseJson(text);
            if (request.StructuredOutputSchema is not null)
            {
                if (json is null)
                    throw new LocalLLMException(
                        LocalLLMFailureKind.InvalidStructuredOutput,
                        "The embedded model returned invalid JSON.",
                        validationErrors: ["$: response is not valid JSON"]);

                var errors = LLMStructuredOutputValidator.ValidateInstance(json, request.StructuredOutputSchema);
                if (errors.Count != 0)
                    throw new LocalLLMException(
                        LocalLLMFailureKind.InvalidStructuredOutput,
                        "The embedded model response did not match the requested JSON schema.",
                        validationErrors: errors);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return new LLMClientResponse
            {
                Text = text,
                Json = json,
                ToolCalls = toolCalls,
                Raw = new JsonObject
                {
                    ["provider"] = LocalLLMProvider.Type,
                    ["model"] = entry.Id
                }
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LocalLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalLLMException(
                _weights is null ? LocalLLMFailureKind.ModelLoad : LocalLLMFailureKind.Inference,
                _weights is null
                    ? "The embedded local model could not be loaded."
                    : "The embedded local model failed during inference.",
                ex);
        }
        finally
        {
            callExecutor?.Context?.Dispose();
            if (startedAt != 0)
            {
                InferenceDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    new KeyValuePair<string, object?>("model", entry.Id));
            }
            _gate.Release();
        }
    }

    public async Task UnloadAsync(string modelId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_loadedModelId, modelId, StringComparison.OrdinalIgnoreCase))
                UnloadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            UnloadCore();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<(LLamaWeights Weights, ModelParams Parameters)> GetOrLoadModelAsync(
        LocalModelCatalogEntry entry,
        CancellationToken ct)
    {
        if (_weights is not null
            && _modelParameters is not null
            && string.Equals(_loadedModelId, entry.Id, StringComparison.OrdinalIgnoreCase))
            return (_weights, _modelParameters);

        UnloadCore();
        var path = LocalModelCatalog.ResolveModelPath(_modelsDirectory, entry);
        if (!File.Exists(path))
            throw new LocalLLMException(
                LocalLLMFailureKind.ModelUnavailable,
                $"Local model '{entry.Id}' is not installed. Run /models install {entry.Id}.");

        var threads = _options.Threads > 0
            ? _options.Threads
            : Math.Max(1, Environment.ProcessorCount - 1);
        var modelParameters = new ModelParams(path)
        {
            ContextSize = _options.ContextSize,
            Threads = threads,
            BatchThreads = threads,
            GpuLayerCount = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? -1
                : 0
        };

        using var activity = ActivitySource.StartActivity("local_llm.load");
        var startedAt = Stopwatch.GetTimestamp();
        activity?.SetTag("gen_ai.request.model", entry.Id);
        activity?.SetTag("gnougo.local.acceleration", ResolveAccelerationName());

        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(modelParameters, ct, progressReporter: null).ConfigureAwait(false);
            _modelParameters = modelParameters;
            _loadedModelId = entry.Id;
            _logger.LogInformation("Loaded embedded local model {ModelId} using {Acceleration}.", entry.Id, ResolveAccelerationName());
            activity?.SetStatus(ActivityStatusCode.Ok);
            return (_weights, modelParameters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "model_load");
            throw;
        }
        finally
        {
            ModelLoadDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                new KeyValuePair<string, object?>("model", entry.Id));
        }
    }

    private ISamplingPipeline CreateSamplingPipeline(LLMClientRequest request)
    {
        Grammar? grammar = request.StructuredOutputSchema is null
            ? null
            : new Grammar(JsonSchemaGbnfConverter.Convert(request.StructuredOutputSchema), "root");

        if (request.Temperature is null or <= 0)
            return new GreedySamplingPipeline { Grammar = grammar };

        return new DefaultSamplingPipeline
        {
            Temperature = (float)Math.Clamp(request.Temperature.Value, 0.01, 2),
            Seed = _options.Seed,
            Grammar = grammar
        };
    }

    private string BuildPrompt(LLMClientRequest request)
    {
        var prompt = new StringBuilder(request.Prompt.Trim());
        if (request.StructuredOutputSchema is not null)
        {
            prompt.Append("\nReturn only JSON matching this schema:\n");
            prompt.Append(request.StructuredOutputSchema.ToJsonString());
        }
        if (!RequestsThinking(request.Reasoning))
            prompt.Append("\n/no_think");
        return prompt.ToString();
    }

    private static string BuildSystemMessage(LLMClientRequest request)
    {
        var system = new StringBuilder(
            "You are GnOuGo's embedded local planning model. Follow the user's request precisely and never reveal hidden reasoning.");
        if (request.Tools is { Count: > 0 })
        {
            system.Append("\nAvailable tools:\n");
            foreach (var tool in request.Tools)
            {
                system.Append("- ").Append(tool.Name);
                if (!string.IsNullOrWhiteSpace(tool.Description))
                    system.Append(": ").Append(tool.Description);
                system.Append(" input_schema=").Append(tool.InputSchema?.ToJsonString() ?? "{}").AppendLine();
            }
            system.Append("To call a tool, emit <tool_call>{\"name\":\"tool name\",\"arguments\":{}}</tool_call>.");
        }
        return system.ToString();
    }

    private int ResolveMaxTokens(LLMClientRequest request)
        => Math.Clamp(request.MaxOutputTokens ?? _options.MaxOutputTokens, 1, _options.MaxOutputTokens);

    private static bool RequestsThinking(string? reasoning)
        => reasoning?.Trim().ToLowerInvariant() is "minimal" or "low" or "medium" or "high" or "max";

    private static string ResolveAccelerationName()
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "metal"
            : "cpu";

    internal static string StripThinking(string value)
    {
        var stripped = ThinkingRegex().Replace(value, string.Empty);
        var unmatchedOpen = stripped.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (unmatchedOpen >= 0)
            stripped = stripped[..unmatchedOpen];

        var unmatchedClose = stripped.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (unmatchedClose >= 0)
            stripped = stripped[(unmatchedClose + "</think>".Length)..];
        return stripped;
    }

    internal static JsonNode? TryParseJson(string value)
    {
        try
        {
            return JsonNode.Parse(ExtractJson(value));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<ToolCallResult>? ParseToolCalls(string value)
    {
        var matches = ToolCallRegex().Matches(value);
        if (matches.Count == 0)
            return null;

        var calls = new List<ToolCallResult>(matches.Count);
        foreach (Match match in matches)
        {
            JsonObject? payload;
            try { payload = JsonNode.Parse(match.Groups[1].Value) as JsonObject; }
            catch (JsonException) { continue; }
            var name = payload?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            calls.Add(new ToolCallResult
            {
                Id = $"local_{Guid.NewGuid():N}",
                Name = name,
                Arguments = payload?["arguments"]?.DeepClone() ?? new JsonObject()
            });
        }
        return calls.Count == 0 ? null : calls;
    }

    private static string ExtractJson(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine
            ? trimmed[(firstLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private void UnloadCore()
    {
        _weights?.Dispose();
        _weights = null;
        _modelParameters = null;
        _loadedModelId = null;
    }

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThinkingRegex();

    [GeneratedRegex("<tool_call>\\s*(.*?)\\s*</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolCallRegex();
}
