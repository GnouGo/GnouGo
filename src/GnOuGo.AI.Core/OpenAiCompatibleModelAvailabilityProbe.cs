using System.Net;
using System.Text.Json;

namespace GnOuGo.AI.Core;

internal static class OpenAiCompatibleModelAvailabilityProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const int DefaultMaxConcurrentProbes = 6;
    private const int GithubModelsMaxConcurrentProbes = 2;

    public static async Task<IReadOnlyList<LLMModelDescriptor>> FilterUsableModelsAsync(
        HttpClient http,
        string chatCompletionsUrl,
        IReadOnlyList<LLMModelDescriptor> models,
        string? bearerToken,
        Func<string, string>? normalizeModel,
        CancellationToken ct)
    {
        if (models.Count == 0)
            return models;

        var candidates = models
            .Where(model => IsProbablyChatCapable(model.Id))
            .ToArray();

        if (candidates.Length == 0)
            return [];

        var firstCandidate = candidates[0];
        var firstProbe = await ProbeAsync(
            http,
            chatCompletionsUrl,
            normalizeModel is null ? firstCandidate.Id : normalizeModel(firstCandidate.Id),
            bearerToken,
            ct);

        if (firstProbe.FatalError is not null)
            throw firstProbe.FatalError;

        var usable = new List<LLMModelDescriptor>(candidates.Length);
        if (firstProbe.IsUsable)
            usable.Add(firstCandidate);

        var remainingCandidates = candidates.Skip(1).ToArray();
        if (remainingCandidates.Length == 0)
            return usable;

        using var gate = new SemaphoreSlim(DetermineMaxConcurrentProbes(chatCompletionsUrl));
        var probeTasks = remainingCandidates.Select((model, index) => ProbeCandidateAsync(index, model)).ToArray();
        var probeResults = await Task.WhenAll(probeTasks);

        usable.AddRange(probeResults
            .Where(result => result.Probe.IsUsable)
            .OrderBy(result => result.Index)
            .Select(result => result.Model));

        var fatalErrors = probeResults
            .Select(result => result.Probe.FatalError)
            .Where(error => error is not null)
            .Cast<Exception>()
            .ToList();

        if (usable.Count > 0)
            return usable.ToArray();

        if (fatalErrors.Count > 0)
            throw new AggregateException("Unable to verify model availability for the configured provider.", fatalErrors);

        return [];

        async Task<(int Index, LLMModelDescriptor Model, ModelProbeResult Probe)> ProbeCandidateAsync(int index, LLMModelDescriptor model)
        {
            await gate.WaitAsync(ct);
            try
            {
                var probe = await ProbeAsync(
                    http,
                    chatCompletionsUrl,
                    normalizeModel is null ? model.Id : normalizeModel(model.Id),
                    bearerToken,
                    ct);
                return (index, model, probe);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static int DetermineMaxConcurrentProbes(string chatCompletionsUrl)
    {
        if (Uri.TryCreate(chatCompletionsUrl, UriKind.Absolute, out var uri)
            && uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return GithubModelsMaxConcurrentProbes;
        }

        return DefaultMaxConcurrentProbes;
    }

    private static async Task<ModelProbeResult> ProbeAsync(
        HttpClient http,
        string chatCompletionsUrl,
        string model,
        string? bearerToken,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var req = HttpRequestHelper.CreateJsonPost(chatCompletionsUrl, BuildProbePayload(model));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                HttpRequestHelper.SetBearerAuth(req, bearerToken);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (resp.IsSuccessStatusCode)
                return ModelProbeResult.Usable();

            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, timeout.Token);
            return ClassifyFailure(resp.StatusCode, body);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ModelProbeResult.Fatal(new TimeoutException($"Model availability probe timed out for '{model}'.", ex));
        }
        catch (HttpRequestException ex)
        {
            return ModelProbeResult.Fatal(ex);
        }
    }

    private static ModelProbeResult ClassifyFailure(HttpStatusCode statusCode, string body)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ModelProbeResult.Unavailable(),
            HttpStatusCode.Forbidden => ModelProbeResult.Unavailable(),
            HttpStatusCode.NotFound => ModelProbeResult.Unavailable(),
            HttpStatusCode.UnprocessableEntity => ModelProbeResult.Unavailable(),
            HttpStatusCode.Unauthorized => ModelProbeResult.Fatal(
                new HttpRequestException($"Model availability probe failed with 401 Unauthorized - {body}")),
            HttpStatusCode.TooManyRequests => ModelProbeResult.Fatal(
                new HttpRequestException($"Model availability probe was rate-limited: {body}")),
            _ when (int)statusCode >= 500 => ModelProbeResult.Fatal(
                new HttpRequestException($"Model availability probe failed: {(int)statusCode} - {body}")),
            _ => ModelProbeResult.Unavailable()
        };
    }

    private static byte[] BuildProbePayload(string model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("max_tokens", 1);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", "Reply with OK.");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static bool IsProbablyChatCapable(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var normalized = modelId.ToLowerInvariant();
        return !normalized.Contains("embedding", StringComparison.Ordinal)
               && !normalized.Contains("whisper", StringComparison.Ordinal)
               && !normalized.Contains("tts", StringComparison.Ordinal)
               && !normalized.Contains("transcribe", StringComparison.Ordinal)
               && !normalized.Contains("moderation", StringComparison.Ordinal)
               && !normalized.Contains("dall-e", StringComparison.Ordinal)
               && !normalized.Contains("image", StringComparison.Ordinal)
               && !normalized.Contains("vision-preview", StringComparison.Ordinal)
               && !normalized.Contains("omni-moderation", StringComparison.Ordinal);
    }

    private readonly record struct ModelProbeResult(bool IsUsable, Exception? FatalError)
    {
        public static ModelProbeResult Usable() => new(true, null);
        public static ModelProbeResult Unavailable() => new(false, null);
        public static ModelProbeResult Fatal(Exception error) => new(false, error);
    }
}



