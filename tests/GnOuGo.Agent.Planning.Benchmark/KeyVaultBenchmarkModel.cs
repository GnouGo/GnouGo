using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

/// <summary>One configured live model and one authorized spending ledger shared by planning and explicit live execution hosts.</summary>
internal sealed class KeyVaultBenchmarkModel : ILLMClient, ILLMCapabilityResolver, IPlanningConstructionAdmission, IDisposable
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
    private readonly IExchangeRateProvider _rates;
    private readonly IModelUsageCostEstimator _estimator;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _planningDeadlines = new(StringComparer.Ordinal);
    private bool _advisoryTokens;
    internal void CaptureTokenPolicy(PlanningRequest request) => _advisoryTokens = PlanningTokenThresholds.Advisory(request);
    private static readonly TimeSpan MaxQuoteAge = new EcbExchangeRateProviderOptions().MaxQuoteAge;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private static readonly Lazy<TiktokenTokenizer> ExecutionTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    internal KeyVaultBenchmarkModel(BenchmarkCampaign campaign, LLMOptions options,
        ILLMClient? client = null, IExchangeRateProvider? rates = null,
        IModelUsageCostEstimator? estimator = null, TimeProvider? time = null, HttpClient? http = null)
    {
        _campaign = campaign; _options = options;
        _http = http ?? LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, TimeSpan.FromMinutes(10));
        _client = client ?? new RoutingLLMClientAdapter(new RoutingLLMClient(options, RoutingLLMClient.CreateDefaultProviders(_http)));
        _time = time ?? TimeProvider.System;
        _rates = rates ?? new EcbExchangeRateProvider(_http, timeProvider: _time); _estimator = estimator ?? new ModelMetadataUsageCostEstimator(options);
    }
    internal string Model => _options.DefaultModel;
    internal string Provider => _options.DefaultProvider;
    internal IReadOnlyDictionary<string, McpServerOptions> McpServers => _options.McpServers;
    internal string ConfigurationFingerprint => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(_options) +
        JsonSerializer.Serialize(new RoutingLLMClient(_options, []).ResolveDeclaredMetadata(Provider, Model)));
    internal JsonObject MetadataSources { get; private init; } = new();
    public async Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling,
        IReadOnlyList<int> outputTokenCeilings, CancellationToken ct)
    {
        if (inputTokenCeiling <= 0 || outputTokenCeilings.Count < 1 || outputTokenCeilings.Any(v => v <= 0))
            throw new ArgumentException("Construction admission requires bounded navigation, draft and review ceilings.");
        await _dispatchGate.WaitAsync(ct);
        try
        {
            // Mirror the physical journal's conservative input bound. No request,
            // charge, reservation or receipt is created by this read-only preflight.
            decimal headroom = 0;
            JsonObject? quote = null;
            foreach (var output in outputTokenCeilings)
            {
                var exposure = _advisoryTokens
                    ? await InputTokenAllowanceAsync(Provider, Model, output, ct)
                        ?? throw BenchmarkCampaign.AdmissionFailure("declared_review_exposure")
                    : checked(3L * inputTokenCeiling);
                var price = AdmissionPrice(exposure, output);
                quote ??= await FreshQuoteAsync(price.Currency, ct, persist: false);
                if (quote["source_currency"]?.ToString() != price.Currency)
                    throw BenchmarkCampaign.AdmissionFailure("review_price_currency_changed");
                headroom = checked(headroom + ConvertCost(price, quote, beforeDispatch: true));
            }
            var accounting = await BenchmarkHttpJournal.AccountingAsync(_campaign, ct: ct);
            var ceiling = await _campaign.CostCeilingAsync(ct);
            if (accounting["cost_upper_bound_eur"]!.GetValue<decimal>() + headroom > ceiling)
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded,
                    "The existing campaign allowance cannot cover the draft and mandatory construction reviews.",
                    details: new JsonObject { ["dispatch_status"] = "not_started", ["classification"] = "construction_review_allowance",
                        ["limit_kind"] = "estimated_cost", ["currency"] = "EUR", ["limit"] = ceiling });
        }
        finally { _dispatchGate.Release(); }
    }
    internal async Task CapturePlanningPolicyAsync(PlanningRequest request, CancellationToken ct, double activeMilliseconds = 0)
    {
        CaptureTokenPolicy(request);
        var value = new JsonObject { ["max_physical_attempts"] = PlanningTransportPolicies.Read(request) is null ? request.MaxModelCalls :
                Math.Min(request.MaxModelCalls, PlanningBudgetOptions.Parse(request.Options)?.MaxCalls ?? request.MaxModelCalls),
            ["max_input_tokens"] = request.Generation.MaxInputTokensPerRequest, ["max_output_tokens"] = request.Generation.MaxOutputTokens,
            ["max_corrections"] = request.MaxReplanAttempts, ["construction_preset"] = request.Options["construction_preset"]?.DeepClone(),
            ["llm_budget"] = request.Options["llm_budget"]?.DeepClone() };
        value["planning_format"] = 11;
        value["construction_review_headroom"] = 2;
        value["construction_review_output_tokens"] = PlanningConstructionPhasePolicies.Resolve(request, "construction_local_judge").OutputTokens;
        value["construction_limits"] = request.Options["construction_limits"]?.DeepClone();
        foreach (var key in new[] { PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", PlanningTokenThresholds.OptionName, PlanningTokenThresholds.OptimizationOptionName })
            if (request.Options[key] is { } captured) value[key] = captured.DeepClone();
        if (PlanningTransportPolicies.Read(request) is not null)
        {
            if (!double.IsFinite(activeMilliseconds) || activeMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(activeMilliseconds));
            // An elapsed deadline still permits settlement of an already received response.
            // The journal rejects every new physical attempt against this unchanged bound.
            var remaining = PlanningBenchmarkMeasurements.PlanningElapsedLimit(request) - TimeSpan.FromMilliseconds(activeMilliseconds);
            var deadline = _time.GetUtcNow() + remaining;
            if (!_planningDeadlines.TryGetValue(request.SessionId, out var prior) || deadline < prior)
                _planningDeadlines[request.SessionId] = deadline;
        }
        var saved = await _campaign.LoadAsync("planning-evaluation-session-policy", request.SessionId, ct);
        if (saved is not null)
        {
            if (!JsonNode.DeepEquals(saved, value)) throw new PlanningConflictException("The captured session transport policy cannot change during recovery.");
            return;
        }
        if ((await _campaign.Records.ListAsync("planning-evaluation-requests", "benchmark", BenchmarkCampaign.Author, ct))
            .Any(r => r.Key.StartsWith(_campaign.Id + ":" + request.SessionId + ":", StringComparison.Ordinal)))
            throw new PlanningConflictException("An issued run without its active-format policy cannot acquire new authority.");
        await _campaign.SaveAsync("planning-evaluation-session-policy", request.SessionId, value, ct);
    }
    internal async Task<JsonObject> ReadinessAsync(CancellationToken ct)
    {
        var metadata = new RoutingLLMClient(_options, []).ResolveDeclaredMetadata(Provider, Model);
        var planning = await InputTokenAllowanceAsync(Provider, Model, 32768, ct);
        var mapping = await InputTokenAllowanceAsync(Provider, Model, 8192, ct);
        JsonObject accounting;
        try
        {
            var price = AdmissionPrice(96000, 32768);
            var quote = await FreshQuoteAsync(price.Currency, ct, persist: false);
            accounting = new() { ["ready"] = true, ["quote"] = quote.DeepClone(),
                ["reservation_estimate_eur"] = ConvertCost(price, quote, beforeDispatch: true) };
        }
        catch (WorkflowRuntimeException ex) when (ex.Code == ErrorCodes.LlmBudgetUnverifiable)
        { accounting = new() { ["ready"] = false, ["error"] = ex.Code, ["details"] = ex.Details?.DeepClone() }; }
        var limitsReady = planning is > 0 && mapping is > 0;
        return new JsonObject { ["provider"] = Provider, ["model"] = Model, ["configuration_hash"] = ConfigurationFingerprint,
            ["metadata_sources"] = MetadataSources.DeepClone(), ["declared_metadata_hash"] = metadata is null ? null : PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(metadata)),
            ["supports_reasoning_effort"] = metadata?.Capabilities.SupportsReasoningEffort,
            ["supported_reasoning_efforts"] = metadata?.Capabilities.SupportedReasoningEfforts is { } efforts
                ? new JsonArray(efforts.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) : null,
            ["max_input_tokens"] = metadata?.MaxInputTokens, ["context_window_tokens"] = metadata?.ContextWindowTokens,
            ["max_output_tokens"] = metadata?.MaxOutputTokens, ["planning_input_allowance"] = planning is null ? null : Math.Min(96000, planning.Value),
            ["mapping_input_allowance"] = mapping is null ? null : Math.Min(96000, mapping.Value), ["model_calls"] = 0,
            ["accounting"] = accounting, ["ready"] = limitsReady && accounting["ready"]!.GetValue<bool>(),
            ["limitation"] = !limitsReady ? "Exact deployment limits (MaxInputTokens, ContextWindowTokens and MaxOutputTokens) are required; no nearby-model or campaign-limit fallback is permitted."
                : accounting["ready"]!.GetValue<bool>() ? null : "Verified pricing and a fresh currency quote are required before external work." };
    }

    internal static void RequireReady(JsonObject readiness)
    {
        if (readiness["ready"]?.GetValue<bool>() == true) return;
        throw BenchmarkCampaign.AdmissionFailure(readiness["accounting"]?["details"]?["reason"]?.ToString() ?? "deployment_allowance_unavailable");
    }

    private ModelUsageCostEstimate AdmissionPrice(long input, long output)
    {
        var price = _estimator.EstimateCostWithCurrency(Model, input, output, Provider);
        if (price is null || price.Amount < 0 || string.IsNullOrEmpty(price.Currency) || price.Currency.Length != 3 || price.Currency.Any(c => c is < 'A' or > 'Z'))
            throw BenchmarkCampaign.AdmissionFailure("model_price_unavailable");
        return price;
    }

    private bool ValidQuote(JsonObject quote, string currency, bool fresh)
    {
        try
        {
            var date = quote["as_of_utc"]!.GetValue<DateTimeOffset>();
            return quote["source_currency"]?.ToString() == currency && quote["target_currency"]?.ToString() == "EUR"
                && quote["rate"]!.GetValue<decimal>() > 0 && !string.IsNullOrWhiteSpace(quote["source"]?.ToString())
                && date != default && date <= _time.GetUtcNow() && (!fresh || _time.GetUtcNow() - date <= MaxQuoteAge);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException) { return false; }
    }

    private async Task<JsonObject> FreshQuoteAsync(string currency, CancellationToken ct, bool persist = true)
    {
        const string collection = "planning-evaluation-exchange-rates";
        var key = currency + "-EUR";
        var cached = await _campaign.LoadAsync(collection, key, ct);
        if (cached is not null && ValidQuote(cached, currency, fresh: true)) return cached;
        CurrencyExchangeQuote? quote;
        try { quote = await _rates.GetQuoteAsync(currency, "EUR", ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { throw BenchmarkCampaign.AdmissionFailure("currency_quote_unavailable"); }
        if (quote is null) throw BenchmarkCampaign.AdmissionFailure("currency_quote_unavailable");
        var result = new JsonObject { ["source_currency"] = quote.SourceCurrency, ["target_currency"] = quote.TargetCurrency,
            ["rate"] = quote.Rate, ["as_of_utc"] = quote.AsOfUtc, ["source"] = quote.Source };
        if (!ValidQuote(result, currency, fresh: true)) throw BenchmarkCampaign.AdmissionFailure("currency_quote_invalid_or_stale");
        if (persist) await _campaign.SaveAsync(collection, key, result, ct);
        return result;
    }

    private static decimal ConvertCost(ModelUsageCostEstimate price, JsonObject quote, bool beforeDispatch)
    {
        try { return checked(price.Amount * quote["rate"]!.GetValue<decimal>()); }
        catch (OverflowException) when (beforeDispatch) { throw BenchmarkCampaign.AdmissionFailure("currency_conversion_overflow"); }
    }

    private async Task<BenchmarkHttpJournal> PrepareAdmissionAsync(string requestId, long input, long output, int? sessionLimit, CancellationToken ct,
        int reviewCount = 0, long reviewInput = 0, int reviewOutput = 0, JsonObject? effectiveTransportPolicy = null)
    {
        var existing = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        if (existing is not null)
        {
            if (existing["effective_transport_policy"] is { } retainedPolicy &&
                !JsonNode.DeepEquals(retainedPolicy, effectiveTransportPolicy))
                throw new InvalidOperationException("The captured effective request transport policy changed.");
            // Never reconstruct an historical admission from today's quote or relax unknown completion.
            if (existing["exchange_quote"] is not JsonObject saved ||
                !ValidQuote(saved, saved["source_currency"]?.ToString() ?? "", fresh: false))
                throw new InvalidOperationException("The issued request has no verified exchange quote; retain it for explicit recovery.");
            return new(_campaign, requestId, existing["input_ceiling"]!.GetValue<long>(), existing["output_ceiling"]!.GetValue<long>(),
                existing["cost_ceiling_eur"]!.GetValue<decimal>(), sessionLimit,
                existing["construction_review_cost_headroom_eur"]?.GetValue<decimal>() ?? 0,
                existing["construction_review_attempt_headroom"]?.GetValue<int>() ?? 0,
                existing["effective_transport_policy"]?.AsObject(), _time);
        }
        var price = AdmissionPrice(input, output);
        var quote = await FreshQuoteAsync(price.Currency, ct);
        var reviewHeadroom = 0m;
        if (reviewCount > 0)
        {
            var reviewPrice = AdmissionPrice(reviewInput, reviewOutput);
            if (reviewPrice.Currency != price.Currency) throw BenchmarkCampaign.AdmissionFailure("review_price_currency_changed");
            reviewHeadroom = checked(reviewCount * ConvertCost(reviewPrice, quote, true));
        }
        var journal = new BenchmarkHttpJournal(_campaign, requestId, input, output, ConvertCost(price, quote, true), sessionLimit, reviewHeadroom, reviewCount, effectiveTransportPolicy, _time);
        await journal.PrepareAsync(ct, quote);
        return journal;
    }

    private async Task<decimal> CompletedCostAsync(string requestId, long input, long output)
    {
        var record = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, requestId, CancellationToken.None);
        var quote = record?["exchange_quote"] as JsonObject;
        var price = _estimator.EstimateCostWithCurrency(Model, input, output, Provider);
        if (price is null || price.Amount < 0 || quote is null || !ValidQuote(quote, price.Currency, fresh: false))
            throw new InvalidOperationException("The completed request cannot be settled using its admitted pricing currency and quote.");
        return ConvertCost(price, quote, false);
    }
    internal static void ApplyUserMetadata(LLMOptions options, IReadOnlyDictionary<string, LLMModelMetadata>? overrides, string provider, string? model)
    {
        if (!string.Equals(options.DefaultProvider, provider, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(options.DefaultModel) || model is not null && options.DefaultModel != model)
            throw new InvalidOperationException("The configured provider/model does not match the evaluation pin.");
        // Same precedence as Server startup. Persisted user defaults cannot switch a pinned campaign.
        var runtime = new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance);
        foreach (var item in overrides ?? new Dictionary<string, LLMModelMetadata>()) runtime.UpsertModelOverride(item.Key, item.Value);
        options.ModelOverrides = runtime.Current.ModelOverrides;
    }
    internal static async Task<KeyVaultBenchmarkModel> CreateAsync(string providerName, string? expectedModel, BenchmarkCampaign campaign, string root, CancellationToken ct, bool pinConfiguration = true)
    {
        var services = new ServiceCollection(); services.AddLogging();
        var vault = WorkspaceDatabasePath(root, null, KeyVaultDatabasePathResolver.DefaultRelativePath);
        services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite("Data Source=" + vault)); services.AddScoped<KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        // Bundled definitions are host defaults; KeyVault applies the same current
        // per-server overrides as Agent.Server. No credentials are printed/exported.
        var hostDefaults = Path.Combine("src", "GnOuGo.Agent.Server", "appsettings.json");
        var host = File.Exists(hostDefaults) ? JsonNode.Parse(await File.ReadAllTextAsync(hostDefaults, ct)) : null;
        var baseline = host?["LLM"]?.Deserialize<LLMOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        baseline.DefaultProvider = providerName; baseline.DefaultModel = expectedModel ?? "";
        var bundled = host?["BundledMcp"]?.Deserialize<BundledMcpSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var config = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultRuntimeConfigStore>.Instance,
            Options.Create(bundled), Options.Create(new KeyVaultSettings { DatabasePath = vault }));
        var options = await config.BuildEffectiveOptionsAsync(baseline, ct);
        var database = WorkspaceDatabasePath(root, host?["Agent"]?["DatabasePath"]?.GetValue<string>(), AgentMcpHostingExtensions.DefaultDatabasePath);
        IReadOnlyDictionary<string, LLMModelMetadata>? userOverrides = null;
        if (File.Exists(database))
        {
            var userServices = new ServiceCollection(); userServices.AddLogging(); userServices.AddAgentMcpPersistence(database);
            await using var userProvider = userServices.BuildServiceProvider();
            await using var userScope = userProvider.CreateAsyncScope();
            userOverrides = (await userScope.ServiceProvider.GetRequiredService<IUserConfigRepository>().GetAsync(ct: ct)).ModelOverrides;
        }
        ApplyUserMetadata(options, userOverrides, providerName, expectedModel);
        var sources = new JsonObject { ["host_metadata_files"] = options.ModelMetadataFiles.Count,
            ["host_overrides_hash"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(baseline.ModelOverrides)),
            ["persisted_user_config_available"] = File.Exists(database), ["persisted_override_count"] = userOverrides?.Count ?? 0,
            ["persisted_overrides_hash"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(userOverrides)) };
        var settings = options.ResolveProvider(options.DefaultProvider) ?? throw new InvalidOperationException("No configured provider.");
        if (!pinConfiguration) return new(campaign, options) { MetadataSources = sources };
        // Preserve the initial configuration; explicit later budget authorizations have their own durable history.
        await campaign.PinAsync(new() { ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["endpoint"] = settings.Url,
            ["request_policy"] = JsonSerializer.SerializeToNode(settings.RequestPolicy), ["reasoning"] = "medium", ["max_input_tokens"] = 96_000,
            ["max_output_tokens"] = 32_768, ["max_calls"] = 8, ["max_repairs"] = 2, ["cost_ceiling_eur"] = 50 }, ct);
        var retryConfiguration = JsonSerializer.SerializeToNode(settings.RetryPolicy)!.AsObject();
        var pinnedRetry = await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        if (pinnedRetry is not null && !JsonNode.DeepEquals(pinnedRetry, retryConfiguration))
            throw new InvalidOperationException("The campaign HTTP retry policy changed.");
        if (pinnedRetry is null) await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", retryConfiguration, ct);
        return new(campaign, options) { MetadataSources = sources };
    }
    internal static string WorkspaceDatabasePath(string root, string? configuredPath, string defaultPath)
        => Path.GetFullPath(Path.Combine(Path.GetFullPath(root), string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath));
    internal static IKeyVaultRecordStore WorkspaceRecords(string root)
        => KeyVaultRecordStoreFactory.CreateWorkspaceStore(WorkspaceDatabasePath(root, null, KeyVaultDatabasePathResolver.DefaultRelativePath), root);
    internal static LLMRequest CreateDispatchRequest(LLMRequest request, string provider, string model, bool execution = false, bool advisoryTokens = false)
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
        if (execution)
        {
            if (dispatched.MaxTokens is <= 0 || dispatched.MaxTokens > 32768 && !advisoryTokens)
                throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded,
                    "Execution request exceeds the campaign output allowance.", details: new JsonObject
                    {
                        ["classification"] = "request_output_allowance", ["dispatch_status"] = "not_started",
                        ["limit_kind"] = "output_tokens", ["limit"] = 32768, ["current"] = dispatched.MaxTokens,
                        ["measurement"] = "declared_output_tokens"
                    });
            if (advisoryTokens && dispatched.MaxTokens is int outputAllowance && outputAllowance > 32768)
                PlanningTokenThresholds.Warn("execution", "declared_output_tokens", outputAllowance, 32768);
            var estimate = ExecutionInputEstimate(JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest));
            if (estimate > 96000 && advisoryTokens) PlanningTokenThresholds.Warn("execution", "estimated_input_tokens", estimate, 96000);
            if (estimate > 96000 && !advisoryTokens)
                throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded,
                    "Execution request exceeds the campaign input allowance.", details: new JsonObject
                    {
                        ["classification"] = "request_input_allowance", ["dispatch_status"] = "not_started",
                        ["limit_kind"] = "input_tokens", ["limit"] = 96000, ["current"] = estimate,
                        ["measurement"] = "conservative_token_estimate"
                    });
        }
        return dispatched;
    }
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => CallBoundedAsync(request, false, ct);
    internal async Task RecoverSavedResponseAsync(LLMRequest request, CancellationToken ct)
    {
        var policy = await _campaign.LoadAsync("planning-evaluation-session-policy", request.ClientRequestId!.Split(':')[0], ct);
        if (policy?[PlanningTransportPolicies.OptionName] is null ||
            await _campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, ct) is not null) return;
        var record = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, ct);
        var last = record?["transport"]?["Attempts"]?.AsArray().LastOrDefault();
        if (last?["Status"]?.GetValue<int>() is >= 200 and < 300 && last["Body"] is not null && last["Failure"] is null)
            await CallAsync(request, ct);
    }
    internal Task<LLMResponse> CallExecutionAsync(LLMRequest request, CancellationToken ct) => CallBoundedAsync(request, true, ct);
    private async Task<LLMResponse> CallBoundedAsync(LLMRequest request, bool execution, CancellationToken ct)
    {
        await _dispatchGate.WaitAsync(ct);
        try { return await DispatchAsync(request, ct, execution); }
        finally { _dispatchGate.Release(); }
    }

    private LLMProviderRetryPolicyOptions RuntimeRetryPolicy(JsonObject? captured = null)
    {
        var policy = captured?["effective_retry_policy"]?.Deserialize(LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions)
            ?? _options.ResolveProvider(Provider)!.RetryPolicy.Clone();
        policy.MaxAttempts = 1; policy.MaxUncertainRetries = 0;
        return policy;
    }

    private JsonObject RuntimeTransportPolicy(string mode, LLMRequest request) => new()
    {
        ["version"] = 2, ["mode"] = mode, ["disable_transport_retries"] = true,
        ["request_hash"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)),
        ["allow_http_recovery"] = true,
        ["effective_retry_policy"] = JsonSerializer.SerializeToNode(RuntimeRetryPolicy(), LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions)
    };

    // A saved logical request is not permission to repeat an unresolved physical attempt.
    // Check identity before the campaign's historical receipt fast path; never rewrite its policy.
    private async Task<JsonObject?> RuntimeJournalAsync(LLMRequest request, CancellationToken ct, string? sdkWireFingerprint = null)
    {
        var id = request.ClientRequestId ?? throw new InvalidOperationException("Runtime inference requires an exact request identity.");
        var original = await _campaign.LoadAsync("planning-evaluation-requests", id, ct);
        var receipt = await _campaign.LoadAsync("planning-evaluation-receipts", id, ct);
        if (original is not null && !JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)) ||
            receipt is not null && original is null)
            throw new InvalidOperationException("The original runtime request does not match its retained identity.");
        var journal = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, ct);
        if (journal?["effective_transport_policy"] is JsonObject policy &&
            journal["effective_transport_policy_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(policy.ToJsonString()))
            throw new InvalidOperationException("The retained runtime transport policy hash changed.");
        var captured = journal?["effective_transport_policy"] as JsonObject;
        if (BenchmarkHttpJournal.IsRuntimeSingleAttempt(captured))
        {
            if (captured!["request_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) ||
                captured["mode"]?.ToString() != (sdkWireFingerprint is null ? "runtime_single_attempt" : "sdk_proxy_single_attempt") ||
                sdkWireFingerprint is not null && captured["wire_fingerprint"]?.ToString() != sdkWireFingerprint)
                throw new InvalidOperationException("The captured runtime admission does not match this exact request and upstream.");
        }
        else if (captured?["mode"]?.ToString() == "sdk_proxy_single_attempt" ||
            sdkWireFingerprint is not null && (journal is not null || original is not null || receipt is not null))
            throw new InvalidOperationException("Historical SDK evidence does not establish the exact upstream; automatic recovery is forbidden.");
        if (receipt is not null) return journal;
        if (journal is null)
        {
            if (original is not null) throw new InvalidOperationException("The reserved runtime request has no recoverable HTTP journal.");
            return null;
        }
        var attempts = journal["transport"]?["Attempts"] as JsonArray
            ?? throw new InvalidOperationException("The runtime HTTP journal is incomplete.");
        if (attempts.Count == 0 && BenchmarkHttpJournal.IsRuntimeSingleAttempt(journal["effective_transport_policy"] as JsonObject)) return journal;
        if (attempts.LastOrDefault() is JsonObject last && last["Status"]?.GetValue<int>() is >= 200 and < 300 &&
            last["Body"] is not null && last["Failure"] is null && original is not null) return journal;
        throw new InvalidOperationException("Runtime inference has no exact completed response; another dispatch is forbidden.");
    }

    private async Task<LLMResponse> DispatchAsync(LLMRequest request, CancellationToken ct, bool execution = false)
    {
        var runtimeJournal = execution ? await RuntimeJournalAsync(request, ct) : null;
        var policy = execution ? null : await _campaign.LoadAsync("planning-evaluation-session-policy", request.ClientRequestId!.Split(':')[0], ct);
        var incremental = policy?["construction_review_headroom"]?.GetValue<int>() > 0;
        var planning = policy is null ? null : PlanningFromPolicy(policy);
        var advisoryTokens = execution ? _advisoryTokens : planning is not null && PlanningTokenThresholds.Advisory(planning);
        var capturedTransport = planning is null ? null : PlanningTransportPolicies.Read(planning);
        if (capturedTransport is not null) PlanningTransportPolicies.ValidateIssued(request, planning!);
        if (capturedTransport is not null && await _campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId!, ct) is { } reserved &&
            !JsonNode.DeepEquals(reserved, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)))
            throw new InvalidOperationException("The reserved request changed.");
        var dispatched = CreateDispatchRequest(request, Provider, Model, execution, advisoryTokens);
        if (execution && (runtimeJournal is null || BenchmarkHttpJournal.IsRuntimeSingleAttempt(runtimeJournal["effective_transport_policy"] as JsonObject)))
            dispatched.DisableTransportRetries = true;
        if (incremental && capturedTransport is null) dispatched.DisableTransportRetries = true;
        else if (capturedTransport is not null) dispatched.DisableTransportRetries = request.DisableTransportRetries;
        var retryPolicy = capturedTransport is null ? _options.ResolveProvider(Provider)!.RetryPolicy.Clone() : new LLMProviderRetryPolicyOptions
        {
            MaxAttempts = capturedTransport.MaxAttempts, MaxUncertainRetries = capturedTransport.MaxUncertainRetries,
            AttemptTimeoutMilliseconds = capturedTransport.AttemptTimeoutMilliseconds,
            BaseDelayMilliseconds = capturedTransport.BaseDelayMilliseconds, MaxDelayMilliseconds = capturedTransport.MaxDelayMilliseconds,
            MaxTotalDelayMilliseconds = capturedTransport.MaxTotalDelayMilliseconds
        };
        if (dispatched.DisableTransportRetries) retryPolicy.MaxAttempts = 1;
        var reviewCount = capturedTransport is not null ? request.RequiredReviewCalls!.Value :
            request.StructuredOutputSchema?["properties"]?["findings"] is null ? policy?["construction_review_headroom"]?.GetValue<int>() ?? 0 : 0;
        var reviewInput = planning is null ? 0 : 3L * PlanningConstructionInputPolicy.EffectiveLimit(planning, "construction_local_judge");
        var reviewOutput = policy?["construction_review_output_tokens"]?.GetValue<int>() ?? 0;
        if (advisoryTokens && reviewCount > 0)
            reviewInput = await InputTokenAllowanceAsync(Provider, Model, reviewOutput, ct)
                ?? throw BenchmarkCampaign.AdmissionFailure("declared_review_exposure");
        JsonObject? effectiveTransportPolicy = new JsonObject
        {
            ["version"] = capturedTransport is null ? 1 : 2, ["disable_transport_retries"] = dispatched.DisableTransportRetries,
            ["allow_http_recovery"] = capturedTransport is not null || !incremental,
            ["effective_retry_policy"] = JsonSerializer.SerializeToNode(retryPolicy, LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions)
        };
        if (execution)
            effectiveTransportPolicy = runtimeJournal is null ? RuntimeTransportPolicy("runtime_single_attempt", request) :
                runtimeJournal["effective_transport_policy"]?.DeepClone().AsObject();
        if (capturedTransport is not null)
        {
            if (planning!.Options[PlanningTokenThresholds.OptionName] is { } tokenMode)
                effectiveTransportPolicy![PlanningTokenThresholds.OptionName] = tokenMode.DeepClone();
            if (PlanningTokenThresholds.OptimizationAdvisory(planning!))
                effectiveTransportPolicy![PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
            effectiveTransportPolicy!["planning_transport_policy"] = policy![PlanningTransportPolicies.OptionName]!.DeepClone();
            effectiveTransportPolicy["max_total_tokens"] = advisoryTokens
                ? PlanningBudgetOptions.Parse(planning!.Options)?.MaxTotalTokens ?? 15_000_000L
                : Math.Min(15_000_000L, PlanningBudgetOptions.Parse(planning!.Options)?.MaxTotalTokens ?? 15_000_000L);
            effectiveTransportPolicy["max_elapsed_ms"] = (long)PlanningBenchmarkMeasurements.PlanningElapsedLimit(planning).TotalMilliseconds;
            effectiveTransportPolicy["review_token_headroom"] = checked(reviewCount * (reviewInput + reviewOutput));
            var retainedTransport = (await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, ct))?["effective_transport_policy"];
            if (retainedTransport?["deadline_utc"] is { } retainedDeadline)
                effectiveTransportPolicy["deadline_utc"] = retainedDeadline.DeepClone();
            else if (_planningDeadlines.TryGetValue(request.ClientRequestId!.Split(':')[0], out var deadline))
                effectiveTransportPolicy["deadline_utc"] = deadline.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            else throw new InvalidOperationException("Capture the remaining planning duration before dispatch.");
        }
        BenchmarkHttpJournal? journal = null;
        return await _campaign.CallAsync(request, async token =>
        {
            var json = JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest);
            var phaseInputLimit = PlanningInputLimit(policy, request);
            RequirePlanningPhasePolicy(policy, request);
            var retained = capturedTransport is null ? null : await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, token);
            var last = retained?["transport"]?["Attempts"]?.AsArray().LastOrDefault();
            if (capturedTransport is not null && !(last?["Status"]?.GetValue<int>() is >= 200 and < 300 &&
                last["Body"] is not null && last["Failure"] is null))
                await ModelRequestAdmission.ValidateAsync(request, this, phaseInputLimit, "planning", token, advisoryTokens);
            var input = Math.Max(phaseInputLimit, Encoding.UTF8.GetByteCount(json));
            var output = dispatched.MaxTokens ?? 32_768;
            // Match the retained planning estimator, including the issued envelope rather than only its prompt.
            var estimatedPlanningInput = ((long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 2) / 3 + 256;
            if (policy is not null && (output > policy["max_output_tokens"]!.GetValue<int>() ||
                estimatedPlanningInput > phaseInputLimit && !advisoryTokens))
                throw BenchmarkCampaign.AdmissionFailure("captured_session_request_allowance");
            journal = await PrepareAdmissionAsync(request.ClientRequestId!, input, output,
                execution ? null : policy?["max_physical_attempts"]?.GetValue<int>() ?? 8, token, reviewCount,
                reviewInput, reviewOutput, effectiveTransportPolicy);
        }, async token =>
        {
            using var context = new LLMHttpRetryContext(request.ClientRequestId!, journal!, execution ? RuntimeRetryPolicy(effectiveTransportPolicy) : capturedTransport is null ? null : retryPolicy).Activate();
            var response = await _client.CallAsync(dispatched, token);
            long ReadTokens(params string[] keys)
            {
                foreach (var key in keys)
                    if (long.TryParse(response.Usage?[key]?.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0) return value;
                throw new InvalidOperationException("Provider usage is missing; the reservation remains conservative.");
            }
            var input = ReadTokens("input_tokens", "prompt_tokens"); var output = ReadTokens("output_tokens", "completion_tokens");
            var measuredUsage = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output,
                ["total_tokens"] = checked(input + output), ["benchmark_cost_eur"] = await CompletedCostAsync(request.ClientRequestId!, input, output) };
            var reasoning = response.Usage?["reasoning_tokens"] ?? response.Usage?["output_tokens_details"]?["reasoning_tokens"] ??
                response.Usage?["completion_tokens_details"]?["reasoning_tokens"];
            if (reasoning is not null) measuredUsage["reasoning_tokens"] = reasoning.DeepClone();
            response.Usage = await journal!.CompleteAsync(measuredUsage, CancellationToken.None);
            return response;
        }, ct, allowHttpRecovery: capturedTransport is not null || !incremental);
    }

    internal static int PlanningInputLimit(JsonObject? policy, LLMRequest request)
    {
        if (policy is null) return 96_000;
        return PlanningConstructionInputPolicy.EffectiveLimit(PlanningFromPolicy(policy), request);
    }

    private static PlanningRequest PlanningFromPolicy(JsonObject policy)
    {
        if (policy["planning_format"]?.GetValue<int>() != 11) throw new PlanningConflictException("The captured policy belongs to a retired planning format.");
        var request = new PlanningRequest()
    {
        MaxModelCalls = policy["max_physical_attempts"]?.GetValue<int>() ?? 64,
        Generation = new()
        {
            MaxInputTokensPerRequest = policy["max_input_tokens"]!.GetValue<int>(),
            MaxOutputTokens = policy["max_output_tokens"]?.GetValue<int>() ?? 32_768
        },
        Options = new() { ["construction_limits"] = policy["construction_limits"]?.DeepClone(),
            ["llm_budget"] = policy["llm_budget"]?.DeepClone() }
        };
        foreach (var key in new[] { PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", PlanningTokenThresholds.OptionName, PlanningTokenThresholds.OptimizationOptionName })
            if (policy[key] is { } captured) request.Options[key] = captured.DeepClone();
        return request;
    }

    internal static void RequirePlanningPhasePolicy(JsonObject? policy, LLMRequest request)
    {
        if (policy is null) return;
        var planning = PlanningFromPolicy(policy);
        var phase = PlanningConstructionPhasePolicies.Resolve(planning, request.StructuredOutputSchema?["title"]?.GetValue<string>() ?? "");
        if (request.MaxTokens is null or <= 0 || request.MaxTokens > phase.OutputTokens || request.Reasoning != phase.Reasoning)
            throw BenchmarkCampaign.AdmissionFailure("captured_session_phase_policy");
    }

    internal async Task<JsonObject?> PartialUsageAsync(string id, CancellationToken ct)
    {
        var record = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, ct);
        if (record?["usage"] is JsonObject known) return BenchmarkHttpJournal.UsageWithReservations(record, known);
        if (record is null && (await _campaign.LoadAsync("planning-evaluation-failures", id, ct))?["dispatch_status"]?.ToString() == "not_started")
            return BenchmarkHttpJournal.UndispatchedUsage();
        if (record?["transport"]?["Attempts"] is not JsonArray attempts) return null;
        if (attempts.Count == 0) return BenchmarkHttpJournal.UndispatchedUsage();
        var pending = attempts.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), hasVerifiedUsage: false, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(record)));
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
            if (ExecutionInputEstimate(body) > 96000)
            {
                if (!_advisoryTokens) throw new InvalidOperationException("Execution request exceeds the campaign input allowance.");
                PlanningTokenThresholds.Warn("sdk_proxy", "estimated_input_tokens", ExecutionInputEstimate(body), 96000);
            }
            var id = run + "-copilot:" + PlanningGraphCompiler.Fingerprint(sdkId);
            var request = new LLMRequest { ClientRequestId = id, Provider = Provider, Model = Model, Prompt = body, MaxTokens = output };
            BenchmarkHttpJournal? journal = null;
            var wireFingerprint = PlanningGraphCompiler.Fingerprint("POST\n" + upstream.AbsoluteUri + "\n" + body);
            var retained = await RuntimeJournalAsync(request, ct, wireFingerprint);
            var transportPolicy = retained is null ? RuntimeTransportPolicy("sdk_proxy_single_attempt", request) :
                retained["effective_transport_policy"]?.DeepClone().AsObject();
            if (retained is null)
            {
                transportPolicy!["timeout_ms"] = (long)_http.Timeout.TotalMilliseconds;
                transportPolicy["wire_fingerprint"] = wireFingerprint;
            }
            var response = await _campaign.CallAsync(request, async token => journal = await PrepareAdmissionAsync(id, input, output.Value, null, token,
                effectiveTransportPolicy: transportPolicy), async token =>
            {
                var state = await journal!.LoadAsync(token) ?? new();
                if (state.Attempts.Count == 0)
                {
                    state.Fingerprint = wireFingerprint; state.Attempts.Add(new() { Id = id });
                    await journal.SaveAsync(state, token);
                    using var message = new HttpRequestMessage(HttpMethod.Post, upstream) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    foreach (var header in headers)
                        if (!header.Key.StartsWith("X-GnOuGo-Inference-", StringComparison.OrdinalIgnoreCase) && header.Key is not ("Host" or "Content-Length" or "Content-Type"))
                            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    using var result = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                    var receivedText = await result.Content.ReadAsStringAsync(token);
                    state.Attempts[0].Status = (int)result.StatusCode; state.Attempts[0].Body = receivedText;
                    state.Attempts[0].ContentType = result.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await journal.SaveAsync(state, CancellationToken.None);
                }
                if (state.Fingerprint != wireFingerprint || state.Attempts.Count != 1 ||
                    state.Attempts[0].Status is null || state.Attempts[0].Body is null || state.Attempts[0].Failure is not null)
                    throw new InvalidOperationException("No exact completed SDK inference response is available.");
                var completed = state.Attempts[0];
                var text = completed.Body!; var contentType = completed.ContentType ?? "application/json";
                JsonNode? usage = null;
                if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && l != "data: [DONE]"))
                    { var item = JsonNode.Parse(line[6..]); usage = item?["usage"] ?? item?["response"]?["usage"] ?? usage; }
                }
                else usage = JsonNode.Parse(text)?["usage"];
                JsonObject? measured = null;
                if (completed.Status is >= 200 and < 300 && (usage?["input_tokens"] ?? usage?["prompt_tokens"])?.GetValue<long>() is { } usedInput &&
                    (usage?["output_tokens"] ?? usage?["completion_tokens"])?.GetValue<long>() is { } usedOutput)
                {
                    measured = await journal.CompleteAsync(new() { ["input_tokens"] = usedInput, ["output_tokens"] = usedOutput,
                        ["benchmark_cost_eur"] = await CompletedCostAsync(id, usedInput, usedOutput) }, CancellationToken.None);
                }
                return new LLMResponse { Text = text, Json = new JsonObject { ["status"] = completed.Status, ["content_type"] = contentType }, Usage = measured };
            }, ct, allowHttpRecovery: true);
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
        var reason = (failure as WorkflowRuntimeException)?.Details?["reason"]?.ToString() ?? (failure.Message switch
        {
            "Execution request exceeds the campaign input allowance." => "input_allowance",
            "The campaign or session cannot cover another HTTP attempt." => "spending_or_attempt_allowance",
            "No currency quote." => "currency_quote_unavailable",
            _ => "admission_or_transport_failure"
        });
        return _campaign.SaveAsync("planning-evaluation-execution-admissions", run + ":" + PlanningGraphCompiler.Fingerprint(body + reason),
            new() { ["reason"] = reason, ["exception_type"] = failure.GetType().Name,
                ["json_bytes"] = Encoding.UTF8.GetByteCount(body), ["body"] = body }, CancellationToken.None);
    }
    public void Dispose() { _http.Dispose(); _dispatchGate.Dispose(); }
}
