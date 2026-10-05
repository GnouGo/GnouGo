using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

/// <summary>One configured live model and one authorized spending ledger shared by planning and explicit live execution hosts.</summary>
internal sealed class KeyVaultBenchmarkModel : ILLMClient, ILLMCapabilityResolver, IDisposable
{
    public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct)
        => ((ILLMCapabilityResolver)_client).InputTokenAllowanceAsync(Provider, Model, outputTokens, ct);
    public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
        => ((ILLMCapabilityResolver)_client).SupportsStructuredOutputAsync(Provider, Model, ct);
    public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
        => ((ILLMCapabilityResolver)_client).SupportedReasoningLevelsAsync(Provider, Model, ct);
    private readonly BenchmarkCampaign _campaign;
    private readonly LLMOptions _options;
    private readonly HttpClient _http;
    private readonly ILLMClient _client;
    private readonly EcbExchangeRateProvider _rates;
    private readonly ModelMetadataUsageCostEstimator _estimator;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private static readonly Lazy<TiktokenTokenizer> ExecutionTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    private KeyVaultBenchmarkModel(BenchmarkCampaign campaign, LLMOptions options)
    {
        _campaign = campaign; _options = options;
        _http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, TimeSpan.FromMinutes(10));
        _client = new RoutingLLMClientAdapter(new RoutingLLMClient(options, RoutingLLMClient.CreateDefaultProviders(_http)));
        _rates = new(_http); _estimator = new(options);
    }
    internal string Model => _options.DefaultModel;
    internal string Provider => _options.DefaultProvider;
    internal IReadOnlyDictionary<string, McpServerOptions> McpServers => _options.McpServers;
    internal string ConfigurationFingerprint => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(_options));
    internal static async Task<KeyVaultBenchmarkModel> CreateAsync(string providerName, string? expectedModel, BenchmarkCampaign campaign, string root, CancellationToken ct)
    {
        var services = new ServiceCollection(); services.AddLogging();
        var vault = KeyVaultDatabasePathResolver.Resolve(null, root);
        services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite("Data Source=" + vault)); services.AddScoped<KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        var baseline = new LLMOptions { DefaultProvider = providerName, DefaultModel = expectedModel ?? "" };
        // Bundled definitions are host defaults; KeyVault applies the same current
        // per-server overrides as Agent.Server. No credentials are printed/exported.
        var hostDefaults = Path.Combine("src", "GnOuGo.Agent.Server", "appsettings.json");
        var host = File.Exists(hostDefaults) ? JsonNode.Parse(await File.ReadAllTextAsync(hostDefaults, ct)) : null;
        if (host?["LLM"]?["McpServers"] is { } servers)
            baseline.McpServers = JsonSerializer.Deserialize<Dictionary<string, McpServerOptions>>(servers, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var bundled = host?["BundledMcp"]?.Deserialize<BundledMcpSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var config = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultRuntimeConfigStore>.Instance,
            Options.Create(bundled), Options.Create(new KeyVaultSettings { DatabasePath = vault }));
        var options = await config.BuildEffectiveOptionsAsync(baseline, ct);
        if (string.IsNullOrWhiteSpace(options.DefaultModel) || expectedModel is not null && options.DefaultModel != expectedModel)
            throw new InvalidOperationException("The configured model does not match the evaluation model.");
        var settings = options.ResolveProvider(options.DefaultProvider) ?? throw new InvalidOperationException("No configured provider.");
        // Preserve the initial configuration; explicit later budget authorizations have their own durable history.
        await campaign.PinAsync(new() { ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["endpoint"] = settings.Url,
            ["request_policy"] = JsonSerializer.SerializeToNode(settings.RequestPolicy), ["reasoning"] = "medium", ["max_input_tokens"] = 96_000,
            ["max_output_tokens"] = 32_768, ["max_calls"] = 8, ["max_repairs"] = 2, ["cost_ceiling_eur"] = 50 }, ct);
        var retryConfiguration = JsonSerializer.SerializeToNode(settings.RetryPolicy)!.AsObject();
        var pinnedRetry = await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        if (pinnedRetry is not null && !JsonNode.DeepEquals(pinnedRetry, retryConfiguration))
            throw new InvalidOperationException("The campaign HTTP retry policy changed.");
        if (pinnedRetry is null) await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", retryConfiguration, ct);
        return new(campaign, options);
    }
    internal static LLMRequest CreateDispatchRequest(LLMRequest request, string provider, string model, bool execution = false)
    {
        if (request.Tools is { Count: > 0 })
            throw new InvalidOperationException("Benchmark recovery permits only side-effect-free generation without tools.");
        var dispatched = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = provider; dispatched.Model = model;
        // Planning uses the pinned transport policy; runtime bindings may impose
        // a stricter invocation allowance that the harness must preserve.
        dispatched.DisableTransportRetries = execution && request.DisableTransportRetries;
        // The planner may prefer background generation. This adapter owns synchronous HTTP
        // recovery; change only its dispatch copy, never the durable planner reservation.
        dispatched.UseBackgroundMode = false;
        if (execution && (dispatched.MaxTokens is <= 0 or > 32768 ||
            ExecutionInputEstimate(JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest)) > 96000))
            throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded,
                "Execution request exceeds the campaign input or output allowance.",
                details: new JsonObject { ["dispatch_status"] = "not_started" });
        return dispatched;
    }
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => CallBoundedAsync(request, false, ct);
    internal Task<LLMResponse> CallExecutionAsync(LLMRequest request, CancellationToken ct) => CallBoundedAsync(request, true, ct);
    private async Task<LLMResponse> CallBoundedAsync(LLMRequest request, bool execution, CancellationToken ct)
    {
        await _dispatchGate.WaitAsync(ct);
        try { return await DispatchAsync(request, singleAttempt: false, ct, execution); }
        finally { _dispatchGate.Release(); }
    }

    internal Task<LLMResponse> DiagnosticAsync(LLMRequest request, CancellationToken ct)
        => DispatchAsync(request, singleAttempt: true, ct);

    private async Task<LLMResponse> DispatchAsync(LLMRequest request, bool singleAttempt, CancellationToken ct, bool execution = false)
    {
        var dispatched = CreateDispatchRequest(request, Provider, Model, execution);
        if (singleAttempt) dispatched.DisableTransportRetries = true;
        BenchmarkHttpJournal? journal = null;
        return await _campaign.CallAsync(request, async token =>
        {
            var json = JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest);
            var input = Math.Max(96_000, Encoding.UTF8.GetByteCount(json));
            var output = dispatched.MaxTokens ?? 32_768;
            var ceiling = _estimator.EstimateCostWithCurrency(Model, input, output, Provider)
                ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(ceiling.Currency, "EUR", token) ?? throw new InvalidOperationException("No currency quote.");
            journal = new(_campaign, request.ClientRequestId!, input, output, ceiling.Amount * quote.Rate, sessionAttemptLimit: execution ? null : 8);
            await journal.PrepareAsync(token);
        }, async token =>
        {
            using var context = new LLMHttpRetryContext(request.ClientRequestId!, journal!).Activate();
            var response = await _client.CallAsync(dispatched, token);
            long ReadTokens(params string[] keys)
            {
                foreach (var key in keys)
                    if (long.TryParse(response.Usage?[key]?.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0) return value;
                throw new InvalidOperationException("Provider usage is missing; the reservation remains conservative.");
            }
            var input = ReadTokens("input_tokens", "prompt_tokens"); var output = ReadTokens("output_tokens", "completion_tokens");
            var estimate = _estimator.EstimateCostWithCurrency(Model, input, output, Provider) ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(estimate.Currency, "EUR", CancellationToken.None) ?? throw new InvalidOperationException("No currency quote.");
            response.Usage = await journal!.CompleteAsync(new() { ["input_tokens"] = input, ["output_tokens"] = output,
                ["total_tokens"] = checked(input + output), ["benchmark_cost_eur"] = estimate.Amount * quote.Rate }, CancellationToken.None);
            return response;
        }, ct, allowHttpRecovery: true);
    }

    internal async Task<JsonObject?> PartialUsageAsync(string id, CancellationToken ct)
    {
        var record = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, ct);
        if (record?["usage"] is JsonObject known) return known.DeepClone().AsObject();
        if (record?["transport"]?["Attempts"] is not JsonArray attempts) return null;
        if (attempts.Count == 0) return BenchmarkHttpJournal.UndispatchedUsage();
        var pending = attempts.Count(a => a!["Status"] is null || a["Status"]!.GetValue<int>() is >= 200 and < 300);
        return new() { ["transport_attempts"] = attempts.Count, ["uncertain_attempts"] = pending,
            ["reserved_input_tokens"] = pending * record["input_ceiling"]!.GetValue<long>(),
            ["reserved_output_tokens"] = pending * record["output_ceiling"]!.GetValue<long>(),
            ["reserved_cost_eur"] = pending * record["cost_ceiling_eur"]!.GetValue<decimal>() };
    }
    internal async Task<(int Status, string ContentType, string Body)> ProxyInferenceAsync(string run, Dictionary<string, string> headers, string body, CancellationToken ct)
    {
        await _dispatchGate.WaitAsync(ct);
        try
        {
            var endpoint = new Uri(_options.ResolveProvider(Provider)!.Url);
            if (!headers.TryGetValue("X-GnOuGo-Inference-Upstream", out var upstreamText) || !Uri.TryCreate(upstreamText, UriKind.Absolute, out var upstream) ||
                upstream.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority) ||
                !upstream.AbsolutePath.StartsWith(endpoint.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Inference upstream is outside the pinned provider.");
            if (!headers.TryGetValue("X-GnOuGo-Inference-Request", out var sdkId) || string.IsNullOrWhiteSpace(sdkId)) throw new InvalidOperationException("Missing SDK request identity.");
            var payload = PrepareProxyPayload(JsonNode.Parse(body)!.AsObject(), Model, upstream.AbsolutePath);
            body = payload.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var output = (payload["max_completion_tokens"] ?? payload["max_tokens"] ?? payload["max_output_tokens"])?.GetValue<int>();
            if (output is null || output <= 0 || output > 32768) throw new InvalidOperationException("Execution inference lacks a bounded output allowance.");
            var input = Math.Max(96000, checked(Encoding.UTF8.GetByteCount(body) + 4096));
            if (ExecutionInputEstimate(body) > 96000) throw new InvalidOperationException("Execution request exceeds the campaign input allowance.");
            var id = run + "-copilot:" + PlanningGraphCompiler.Fingerprint(sdkId);
            var request = new LLMRequest { ClientRequestId = id, Provider = Provider, Model = Model, Prompt = body, MaxTokens = output };
            var price = _estimator.EstimateCostWithCurrency(Model, input, output.Value, Provider) ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(price.Currency, "EUR", ct) ?? throw new InvalidOperationException("No currency quote.");
            var journal = new BenchmarkHttpJournal(_campaign, id, input, output.Value, price.Amount * quote.Rate, sessionAttemptLimit: null);
            var response = await _campaign.CallAsync(request, token => journal.PrepareAsync(token), async token =>
            {
                var state = await journal.LoadAsync(token) ?? new();
                if (state.Attempts.Count != 0) throw new InvalidOperationException("Unknown SDK inference cannot be dispatched again.");
                state.Fingerprint = PlanningGraphCompiler.Fingerprint(body); state.Attempts.Add(new() { Id = id });
                await journal.SaveAsync(state, token);
                using var message = new HttpRequestMessage(HttpMethod.Post, upstream) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                foreach (var header in headers)
                    if (!header.Key.StartsWith("X-GnOuGo-Inference-", StringComparison.OrdinalIgnoreCase) && header.Key is not ("Host" or "Content-Length" or "Content-Type"))
                        message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var result = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                var text = await result.Content.ReadAsStringAsync(token); var contentType = result.Content.Headers.ContentType?.ToString() ?? "application/json";
                state.Attempts[0].Status = (int)result.StatusCode; state.Attempts[0].Body = text; state.Attempts[0].ContentType = contentType;
                await journal.SaveAsync(state, CancellationToken.None);
                JsonNode? usage = null;
                if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && l != "data: [DONE]"))
                    { var item = JsonNode.Parse(line[6..]); usage = item?["usage"] ?? item?["response"]?["usage"] ?? usage; }
                }
                else usage = JsonNode.Parse(text)?["usage"];
                JsonObject? measured = null;
                if (result.IsSuccessStatusCode && (usage?["input_tokens"] ?? usage?["prompt_tokens"])?.GetValue<long>() is { } usedInput &&
                    (usage?["output_tokens"] ?? usage?["completion_tokens"])?.GetValue<long>() is { } usedOutput)
                {
                    var actual = _estimator.EstimateCostWithCurrency(Model, usedInput, usedOutput, Provider) ?? throw new InvalidOperationException("No model price metadata.");
                    measured = await journal.CompleteAsync(new() { ["input_tokens"] = usedInput, ["output_tokens"] = usedOutput, ["benchmark_cost_eur"] = actual.Amount * quote.Rate }, CancellationToken.None);
                }
                return new LLMResponse { Text = text, Json = new JsonObject { ["status"] = (int)result.StatusCode, ["content_type"] = contentType }, Usage = measured };
            }, ct);
            return (response.Json!["status"]!.GetValue<int>(), response.Json["content_type"]!.ToString(), response.Text);
        }
        finally { _dispatchGate.Release(); }
    }
    internal static JsonObject PrepareProxyPayload(JsonObject original, string model, string path)
    {
        if (original["model"]?.ToString() != model || original["n"] is { } n && n.GetValue<int>() != 1 ||
            original["previous_response_id"] is not null || original["conversation"] is not null || !TextOnly(original))
            throw new InvalidOperationException("Execution inference requires the pinned model, one completion and fully visible text input.");
        var responses = path.EndsWith("/responses", StringComparison.Ordinal);
        if (!responses && !path.EndsWith("/chat/completions", StringComparison.Ordinal)) throw new InvalidOperationException("Unsupported inference protocol.");
        var result = original.DeepClone().AsObject();
        var field = responses ? "max_output_tokens" : "max_completion_tokens";
        var requested = (result[field] ?? result["max_tokens"])?.GetValue<int>() ?? 32768;
        if (requested <= 0) throw new InvalidOperationException("Invalid inference output ceiling.");
        result.Remove("max_tokens"); result[field] = Math.Min(requested, 32768);
        if (responses) { result["reasoning"] ??= new JsonObject(); result["reasoning"]!["effort"] = "medium"; }
        else
        {
            result["reasoning_effort"] = "medium";
            if (result["stream"]?.GetValue<bool>() == true)
            { result["stream_options"] ??= new JsonObject(); result["stream_options"]!["include_usage"] = true; }
        }
        return result;

        static bool TextOnly(JsonNode? node) => node switch
        {
            JsonObject obj => !obj.Any(p => p.Key is "image_url" or "input_audio" or "audio" or "file_data" or "file_id" ||
                p.Key == "type" && p.Value?.ToString() is "input_image" or "input_audio" or "input_file" or "image" or "audio") && obj.All(p => TextOnly(p.Value)),
            JsonArray items => items.All(TextOnly), _ => true
        };
    }
    // GPT-5 uses o200k_base. Count the full visible JSON envelope with headroom
    // for chat framing; retain the larger byte-based reservation for EUR safety.
    internal static int ExecutionInputEstimate(string body) => checked((int)Math.Ceiling(ExecutionTokenizer.Value.CountTokens(body) * 1.25) + 4096);
    internal Task RetainProxyFailureAsync(string run, string body, Exception failure)
    {
        // This record is encrypted like the campaign journal. Public reports use
        // only its type, reason and counts, never the retained request contents.
        var reason = failure.Message switch
        {
            "Execution request exceeds the campaign input allowance." => "input_allowance",
            "The campaign or session cannot cover another HTTP attempt." => "spending_or_attempt_allowance",
            "No currency quote." => "currency_quote_unavailable",
            _ => "admission_or_transport_failure"
        };
        return _campaign.SaveAsync("planning-evaluation-execution-admissions", run + ":" + PlanningGraphCompiler.Fingerprint(body + reason),
            new() { ["reason"] = reason, ["exception_type"] = failure.GetType().Name,
                ["json_bytes"] = Encoding.UTF8.GetByteCount(body), ["body"] = body }, CancellationToken.None);
    }
    public void Dispose() { _http.Dispose(); _dispatchGate.Dispose(); }
}
