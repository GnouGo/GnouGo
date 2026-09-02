using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// A positive or zero decimal amount paired with an ISO-style currency code.
/// </summary>
public sealed record MonetaryAmount(decimal Amount, string Currency);

/// <summary>
/// One model-usage estimate in the currency of its pricing metadata.
/// </summary>
public sealed record ModelUsageCostEstimate(decimal Amount, string Currency);

/// <summary>
/// A conversion quote where one source-currency unit equals <see cref="Rate"/>
/// target-currency units.
/// </summary>
public sealed record CurrencyExchangeQuote(
    string SourceCurrency,
    string TargetCurrency,
    decimal Rate,
    DateTimeOffset AsOfUtc,
    string Source);

/// <summary>
/// Provider-neutral limits for a bounded collection of LLM calls.
/// </summary>
public sealed record LLMUsageBudgetLimits
{
    public int? MaxCalls { get; init; }
    public long? MaxTotalTokens { get; init; }
    public TimeSpan? MaxElapsed { get; init; }
    public MonetaryAmount? MaxEstimatedCost { get; init; }
    public decimal? MaxEstimatedCostUsd { get; init; }

    internal MonetaryAmount? ResolveMaxEstimatedCost()
        => MaxEstimatedCost ?? (MaxEstimatedCostUsd is { } legacy ? new MonetaryAmount(legacy, "USD") : null);

    public void Validate()
    {
        if (MaxCalls is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCalls), "The LLM call budget must be positive.");
        if (MaxTotalTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalTokens), "The LLM token budget must be positive.");
        if (MaxElapsed is { } elapsed && elapsed <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxElapsed), "The LLM elapsed-time budget must be positive.");
        if (MaxEstimatedCost is not null && MaxEstimatedCostUsd is not null)
            throw new ArgumentException("The currency-aware and legacy USD estimated-cost limits cannot both be set.", nameof(LLMUsageBudgetLimits));
        if (MaxEstimatedCost is { Amount: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(MaxEstimatedCost), "The LLM estimated-cost budget must be positive.");
        if (MaxEstimatedCost is { } monetaryLimit)
            _ = NormalizeCurrency(monetaryLimit.Currency, nameof(MaxEstimatedCost));
        if (MaxEstimatedCostUsd is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEstimatedCostUsd), "The LLM estimated-cost budget must be positive.");
        if (MaxCalls is null && MaxTotalTokens is null && MaxElapsed is null
            && MaxEstimatedCost is null && MaxEstimatedCostUsd is null)
            throw new ArgumentException("At least one LLM usage budget limit is required.", nameof(LLMUsageBudgetLimits));
    }

    internal static string NormalizeCurrency(string? currency, string parameterName)
    {
        var value = currency ?? string.Empty;
        if (value.Length != 3 || value.Any(static character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency codes must contain exactly three uppercase ASCII letters.", parameterName);
        return value;
    }
}

/// <summary>
/// Redacted cumulative state for an LLM usage budget.
/// </summary>
public sealed record LLMUsageBudgetSnapshot
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public string EstimatedCostCurrency { get; init; } = "USD";
    public IReadOnlyList<CurrencyExchangeQuote> ExchangeRates { get; init; } = Array.Empty<CurrencyExchangeQuote>();
    public decimal EstimatedCostUsd { get; init; }
}

/// <summary>
/// Optional durable sink used by hosts that need a budget to span process boundaries.
/// Implementations must persist only the redacted snapshot.
/// </summary>
public interface ILLMUsageBudgetSink
{
    ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct);
}

/// <summary>
/// Factory used by hosts to attach an execution-level budget to a workflow engine.
/// </summary>
public interface ILLMUsageBudgetScopeFactory
{
    LLMUsageBudgetScope? CreateScope();
}

/// <summary>
/// Thread-safe, provider-neutral LLM usage budget. Child scopes enforce their own limits
/// while atomically contributing the same call to their parent scope.
/// </summary>
public sealed class LLMUsageBudgetScope
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly SemaphoreSlim? _unaccountedCostGate;
    private readonly LLMUsageBudgetScope? _parent;
    private readonly ILLMUsageBudgetSink? _sink;
    private readonly IExchangeRateProvider? _exchangeRateProvider;
    private readonly TimeProvider _timeProvider;
    private readonly MonetaryAmount? _costLimit;
    private LLMUsageBudgetSnapshot _snapshot;

    public LLMUsageBudgetScope(
        LLMUsageBudgetLimits limits,
        LLMUsageBudgetSnapshot? initialSnapshot = null,
        LLMUsageBudgetScope? parent = null,
        ILLMUsageBudgetSink? sink = null,
        TimeProvider? timeProvider = null,
        IExchangeRateProvider? exchangeRateProvider = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        Limits = limits;
        _parent = parent;
        _sink = sink;
        _exchangeRateProvider = exchangeRateProvider ?? parent?._exchangeRateProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _costLimit = limits.ResolveMaxEstimatedCost() is { } configuredLimit
            ? configuredLimit with
            {
                Currency = LLMUsageBudgetLimits.NormalizeCurrency(
                    configuredLimit.Currency,
                    nameof(LLMUsageBudgetLimits.MaxEstimatedCost))
            }
            : null;
        _unaccountedCostGate = _costLimit is not null ? new SemaphoreSlim(1, 1) : null;

        var now = _timeProvider.GetUtcNow();
        _snapshot = initialSnapshot is null
            ? new LLMUsageBudgetSnapshot
            {
                StartedAtUtc = now,
                EstimatedCostCurrency = _costLimit?.Currency ?? "USD"
            }
            : ValidateInitialSnapshot(initialSnapshot, now, _costLimit?.Currency);
    }

    public LLMUsageBudgetLimits Limits { get; }

    public LLMUsageBudgetSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot with { };
        }
    }

    public LLMUsageBudgetScope CreateChild(
        LLMUsageBudgetLimits limits,
        ILLMUsageBudgetSink? sink = null)
        => new(
            limits,
            parent: this,
            sink: sink,
            exchangeRateProvider: _exchangeRateProvider,
            timeProvider: _timeProvider);

    /// <summary>
    /// Executes one LLM request under this scope and every parent scope.
    /// </summary>
    public async Task<LLMResponse> CallAsync(
        ILLMClient client,
        IModelUsageCostEstimator? costEstimator,
        LLMRequest request,
        string stage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var safeStage = SanitizeStage(stage);
        var reservations = new List<LocalReservation>();
        try
        {
            await ReserveChainAsync(request, costEstimator, safeStage, reservations, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackReservationsAsync(reservations, safeStage, CancellationToken.None).ConfigureAwait(false);
            ReleaseReservations(reservations);
            throw;
        }

        LLMResponse response;
        try
        {
            response = await client.CallAsync(request, ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReservations(reservations);
            throw;
        }

        Exception? completionFailure = null;
        try
        {
            foreach (var reservation in reservations)
            {
                try
                {
                    await reservation.Scope.CompleteLocalAsync(
                        reservation,
                        response.Usage,
                        costEstimator,
                        request,
                        safeStage,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    completionFailure ??= ex;
                }
            }
        }
        finally
        {
            ReleaseReservations(reservations);
        }

        if (completionFailure is not null)
            throw completionFailure;

        return response;
    }

    private async ValueTask ReserveChainAsync(
        LLMRequest request,
        IModelUsageCostEstimator? costEstimator,
        string stage,
        List<LocalReservation> reservations,
        CancellationToken ct)
    {
        if (_parent is not null)
            await _parent.ReserveChainAsync(request, costEstimator, stage, reservations, ct).ConfigureAwait(false);

        if (_unaccountedCostGate is not null)
            await _unaccountedCostGate.WaitAsync(ct).ConfigureAwait(false);

        var ownsCostGate = _unaccountedCostGate is not null;
        var incremented = false;
        try
        {
            if (_costLimit is not null)
            {
                ModelUsageCostEstimate? priceProbe;
                try
                {
                    priceProbe = costEstimator?.EstimateCostWithCurrency(
                        request.Model,
                        1,
                        1,
                        request.Provider);
                }
                catch
                {
                    throw CreateUnverifiable(stage, "pricing", Snapshot);
                }
                if (priceProbe is null)
                    throw CreateUnverifiable(stage, "pricing", Snapshot);
                _ = await ConvertToBudgetCurrencyAsync(priceProbe, stage, ct).ConfigureAwait(false);
            }

            LLMUsageBudgetSnapshot snapshot;
            lock (_gate)
            {
                snapshot = _snapshot;
                EnsurePreCallLimits(snapshot, stage);
                _snapshot = snapshot with { Calls = checked(snapshot.Calls + 1) };
                snapshot = _snapshot;
                incremented = true;
            }

            await PersistAsync(snapshot, stage, ct).ConfigureAwait(false);
            reservations.Add(new LocalReservation(this, ownsCostGate));
        }
        catch
        {
            if (incremented)
            {
                lock (_gate)
                    _snapshot = _snapshot with { Calls = Math.Max(0, _snapshot.Calls - 1) };
            }
            if (ownsCostGate)
                _unaccountedCostGate!.Release();
            throw;
        }
    }

    private static async ValueTask RollbackReservationsAsync(
        List<LocalReservation> reservations,
        string stage,
        CancellationToken ct)
    {
        for (var index = reservations.Count - 1; index >= 0; index--)
        {
            var scope = reservations[index].Scope;
            LLMUsageBudgetSnapshot snapshot;
            lock (scope._gate)
            {
                scope._snapshot = scope._snapshot with { Calls = Math.Max(0, scope._snapshot.Calls - 1) };
                snapshot = scope._snapshot;
            }

            try
            {
                await scope.PersistAsync(snapshot, stage, ct).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original pre-dispatch failure. The scope still fails closed.
            }
        }
    }

    private void EnsurePreCallLimits(
        LLMUsageBudgetSnapshot snapshot,
        string stage)
    {
        if (Limits.MaxElapsed is { } maxElapsed)
        {
            var elapsed = _timeProvider.GetUtcNow() - snapshot.StartedAtUtc;
            if (elapsed >= maxElapsed)
                throw CreateExceeded(stage, "elapsed_ms", maxElapsed.TotalMilliseconds, elapsed.TotalMilliseconds, snapshot);
        }

        if (Limits.MaxCalls is { } maxCalls && snapshot.Calls >= maxCalls)
            throw CreateExceeded(stage, "calls", maxCalls, snapshot.Calls, snapshot);

        if (Limits.MaxTotalTokens is { } maxTokens && snapshot.TotalTokens >= maxTokens)
            throw CreateExceeded(stage, "total_tokens", maxTokens, snapshot.TotalTokens, snapshot);

        if (_costLimit is { } maxCost)
        {
            if (snapshot.EstimatedCost >= maxCost.Amount)
                throw CreateExceeded(
                    stage,
                    ResolveEstimatedCostLimitKind(),
                    maxCost.Amount,
                    snapshot.EstimatedCost,
                    snapshot);
        }
    }

    private async ValueTask CompleteLocalAsync(
        LocalReservation reservation,
        JsonNode? usage,
        IModelUsageCostEstimator? costEstimator,
        LLMRequest request,
        string stage,
        CancellationToken ct)
    {
        _ = reservation;
        var requiresTokens = Limits.MaxTotalTokens.HasValue || _costLimit is not null;
        ParsedUsage? parsedUsage;
        try
        {
            parsedUsage = ParseUsage(usage);
        }
        catch (OverflowException)
        {
            throw CreateUnverifiable(stage, "usage", Snapshot);
        }
        if (requiresTokens && parsedUsage is null)
            throw CreateUnverifiable(stage, "usage", Snapshot);

        ModelUsageCostEstimate? sourceCost = null;
        MonetaryAmount? estimatedCost = null;
        if (_costLimit is not null)
        {
            if (parsedUsage?.InputTokens is null || parsedUsage.OutputTokens is null)
                throw CreateUnverifiable(stage, "usage", Snapshot);

            try
            {
                sourceCost = costEstimator?.EstimateCostWithCurrency(
                    request.Model,
                    parsedUsage.InputTokens.Value,
                    parsedUsage.OutputTokens.Value,
                    request.Provider);
            }
            catch
            {
                throw CreateUnverifiable(stage, "pricing", Snapshot);
            }
            if (sourceCost is null)
                throw CreateUnverifiable(stage, "pricing", Snapshot);
            estimatedCost = await ConvertToBudgetCurrencyAsync(sourceCost, stage, ct).ConfigureAwait(false);
        }

        LLMUsageBudgetSnapshot snapshot;
        try
        {
            lock (_gate)
            {
                var current = _snapshot;
                snapshot = current with
                {
                    InputTokens = checked(current.InputTokens + (parsedUsage?.InputTokens ?? 0)),
                    OutputTokens = checked(current.OutputTokens + (parsedUsage?.OutputTokens ?? 0)),
                    TotalTokens = checked(current.TotalTokens + (parsedUsage?.TotalTokens ?? 0)),
                    EstimatedCost = checked(current.EstimatedCost + (estimatedCost?.Amount ?? 0m)),
                    EstimatedCostUsd = checked(current.EstimatedCostUsd + ResolveLegacyUsdIncrement(sourceCost, estimatedCost))
                };
                _snapshot = snapshot;
            }
        }
        catch (OverflowException)
        {
            throw CreateUnverifiable(stage, "usage", Snapshot);
        }

        await PersistAsync(snapshot, stage, ct).ConfigureAwait(false);

        if (Limits.MaxTotalTokens is { } maxTokens && snapshot.TotalTokens > maxTokens)
            throw CreateExceeded(stage, "total_tokens", maxTokens, snapshot.TotalTokens, snapshot);
        if (_costLimit is { } maxCost && snapshot.EstimatedCost > maxCost.Amount)
        {
            throw CreateExceeded(
                stage,
                ResolveEstimatedCostLimitKind(),
                maxCost.Amount,
                snapshot.EstimatedCost,
                snapshot);
        }
    }

    private decimal ResolveLegacyUsdIncrement(
        ModelUsageCostEstimate? sourceCost,
        MonetaryAmount? normalizedCost)
    {
        if (sourceCost is not null
            && string.Equals(
                NormalizeCostCurrency(sourceCost.Currency),
                "USD",
                StringComparison.Ordinal))
        {
            return sourceCost.Amount;
        }

        return normalizedCost is not null
               && string.Equals(normalizedCost.Currency, "USD", StringComparison.Ordinal)
            ? normalizedCost.Amount
            : 0m;
    }

    private async ValueTask<MonetaryAmount> ConvertToBudgetCurrencyAsync(
        ModelUsageCostEstimate estimate,
        string stage,
        CancellationToken ct)
    {
        if (_costLimit is null)
            throw new InvalidOperationException("A cost estimate was requested without a monetary budget.");

        string sourceCurrency;
        try
        {
            if (estimate.Amount < 0)
                throw new ArgumentOutOfRangeException(nameof(estimate));
            sourceCurrency = NormalizeCostCurrency(estimate.Currency);
        }
        catch (ArgumentException)
        {
            throw CreateUnverifiable(stage, "currency", Snapshot);
        }

        var targetCurrency = _costLimit.Currency;
        if (string.Equals(sourceCurrency, targetCurrency, StringComparison.Ordinal))
            return new MonetaryAmount(estimate.Amount, targetCurrency);

        CurrencyExchangeQuote? quote;
        lock (_gate)
        {
            quote = _snapshot.ExchangeRates.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceCurrency, sourceCurrency, StringComparison.Ordinal)
                && string.Equals(candidate.TargetCurrency, targetCurrency, StringComparison.Ordinal));
        }

        if (quote is null)
        {
            if (_exchangeRateProvider is null)
                throw CreateUnverifiable(stage, "exchange_rate", Snapshot);
            try
            {
                quote = await _exchangeRateProvider.GetQuoteAsync(sourceCurrency, targetCurrency, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw CreateUnverifiable(stage, "exchange_rate", Snapshot);
            }

            if (!IsValidExchangeQuote(quote, sourceCurrency, targetCurrency))
                throw CreateUnverifiable(stage, "exchange_rate", Snapshot);
            var validatedQuote = quote!;

            lock (_gate)
            {
                var existing = _snapshot.ExchangeRates.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceCurrency, sourceCurrency, StringComparison.Ordinal)
                    && string.Equals(candidate.TargetCurrency, targetCurrency, StringComparison.Ordinal));
                if (existing is not null)
                {
                    quote = existing;
                }
                else
                {
                    quote = validatedQuote;
                    _snapshot = _snapshot with
                    {
                        ExchangeRates = _snapshot.ExchangeRates.Append(validatedQuote).ToArray()
                    };
                }
            }
        }

        var applicableQuote = quote
            ?? throw CreateUnverifiable(stage, "exchange_rate", Snapshot);
        try
        {
            return new MonetaryAmount(checked(estimate.Amount * applicableQuote.Rate), targetCurrency);
        }
        catch (OverflowException)
        {
            throw CreateUnverifiable(stage, "exchange_rate", Snapshot);
        }
    }

    private bool IsValidExchangeQuote(
        CurrencyExchangeQuote? quote,
        string sourceCurrency,
        string targetCurrency)
    {
        if (quote is null || quote.Rate <= 0 || quote.AsOfUtc == default
            || quote.AsOfUtc > _timeProvider.GetUtcNow()
            || string.IsNullOrWhiteSpace(quote.Source) || quote.Source.Length > 160)
        {
            return false;
        }

        try
        {
            return string.Equals(
                       NormalizeCostCurrency(quote.SourceCurrency),
                       sourceCurrency,
                       StringComparison.Ordinal)
                   && string.Equals(
                       NormalizeCostCurrency(quote.TargetCurrency),
                       targetCurrency,
                       StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeCostCurrency(string? currency)
        => LLMUsageBudgetLimits.NormalizeCurrency(currency, "currency");

    private string ResolveEstimatedCostLimitKind()
        => Limits.MaxEstimatedCostUsd is not null ? "estimated_cost_usd" : "estimated_cost";

    private async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, string stage, CancellationToken ct)
    {
        if (_sink is null)
            return;

        await _persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        var persistedSnapshot = snapshot;
        try
        {
            persistedSnapshot = Snapshot;
            await _sink.PersistAsync(persistedSnapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw CreateUnverifiable(stage, "ledger", persistedSnapshot);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static void ReleaseReservations(List<LocalReservation> reservations)
    {
        for (var index = reservations.Count - 1; index >= 0; index--)
        {
            var reservation = reservations[index];
            if (reservation.OwnsCostGate)
                reservation.Scope._unaccountedCostGate!.Release();
        }
        reservations.Clear();
    }

    private static ParsedUsage? ParseUsage(JsonNode? usage)
    {
        if (usage is not JsonObject obj)
            return null;

        var input = ReadLong(obj, "prompt_tokens") ?? ReadLong(obj, "input_tokens");
        var output = ReadLong(obj, "completion_tokens") ?? ReadLong(obj, "output_tokens");
        var total = ReadLong(obj, "total_tokens");
        if (input.HasValue || output.HasValue)
        {
            var calculatedTotal = checked((input ?? 0) + (output ?? 0));
            total = Math.Max(total ?? 0, calculatedTotal);
        }

        if (input is < 0 || output is < 0 || total is < 0 || total is null)
            return null;

        return new ParsedUsage(input, output, total.Value);
    }

    private static long? ReadLong(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var longValue))
            return longValue;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<string>(out var text)
            && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private WorkflowRuntimeException CreateExceeded(
        string stage,
        string limitKind,
        object limit,
        object current,
        LLMUsageBudgetSnapshot snapshot)
        => new(
            ErrorCodes.LlmBudgetExceeded,
            "The configured LLM usage budget was exceeded.",
            retryable: false,
            details: BuildDetails("budget_exceeded", stage, limitKind, limit, current, snapshot,
                "Reduce the request or increase the explicitly configured LLM usage budget."));

    private WorkflowRuntimeException CreateUnverifiable(
        string stage,
        string limitKind,
        LLMUsageBudgetSnapshot snapshot)
        => new(
            ErrorCodes.LlmBudgetUnverifiable,
            "The configured LLM usage budget could not be verified safely.",
            retryable: false,
            details: BuildDetails("budget_unverifiable", stage, limitKind, null, null, snapshot,
                "Configure provider-neutral usage and pricing metadata, or remove the unverifiable limit explicitly."));

    private JsonObject BuildDetails(
        string classification,
        string stage,
        string limitKind,
        object? limit,
        object? current,
        LLMUsageBudgetSnapshot snapshot,
        string recommendedAction)
    {
        var details = new JsonObject
        {
            ["stage"] = stage,
            ["classification"] = classification,
            ["limit_kind"] = limitKind,
            ["retryable"] = false,
            ["calls"] = snapshot.Calls,
            ["input_tokens"] = snapshot.InputTokens,
            ["output_tokens"] = snapshot.OutputTokens,
            ["total_tokens"] = snapshot.TotalTokens,
            ["estimated_cost"] = snapshot.EstimatedCost,
            ["estimated_cost_currency"] = snapshot.EstimatedCostCurrency,
            ["estimated_cost_usd"] = snapshot.EstimatedCostUsd,
            ["elapsed_ms"] = Math.Max(0, (_timeProvider.GetUtcNow() - snapshot.StartedAtUtc).TotalMilliseconds),
            ["recommended_action"] = recommendedAction
        };
        if (limit is not null)
            details["limit"] = CreateScalarNode(limit);
        if (current is not null)
            details["current"] = CreateScalarNode(current);
        return details;
    }

    private static JsonNode CreateScalarNode(object value) => value switch
    {
        int typed => JsonValue.Create(typed),
        long typed => JsonValue.Create(typed),
        double typed => JsonValue.Create(typed),
        decimal typed => JsonValue.Create(typed),
        _ => JsonValue.Create(value.ToString())!
    };

    private static LLMUsageBudgetSnapshot ValidateInitialSnapshot(
        LLMUsageBudgetSnapshot snapshot,
        DateTimeOffset now,
        string? expectedCostCurrency)
    {
        long componentTotal;
        try
        {
            componentTotal = checked(snapshot.InputTokens + snapshot.OutputTokens);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("The initial LLM usage budget snapshot is invalid.", nameof(snapshot), ex);
        }
        string snapshotCurrency;
        try
        {
            snapshotCurrency = NormalizeCostCurrency(snapshot.EstimatedCostCurrency);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The initial LLM usage budget snapshot is invalid.", nameof(snapshot), ex);
        }
        if (expectedCostCurrency is not null
            && !string.Equals(snapshotCurrency, expectedCostCurrency, StringComparison.Ordinal))
        {
            throw new ArgumentException("The initial LLM usage budget snapshot currency does not match the configured limit.", nameof(snapshot));
        }

        var normalizedEstimatedCost = snapshot.EstimatedCost;
        if (normalizedEstimatedCost == 0
            && snapshot.EstimatedCostUsd > 0
            && string.Equals(snapshotCurrency, "USD", StringComparison.Ordinal))
        {
            normalizedEstimatedCost = snapshot.EstimatedCostUsd;
        }
        var normalizedLegacyUsd = snapshot.EstimatedCostUsd;
        if (normalizedLegacyUsd == 0
            && normalizedEstimatedCost > 0
            && string.Equals(snapshotCurrency, "USD", StringComparison.Ordinal))
        {
            normalizedLegacyUsd = normalizedEstimatedCost;
        }

        if (snapshot.StartedAtUtc == default || snapshot.StartedAtUtc > now
            || snapshot.Calls < 0 || snapshot.InputTokens < 0 || snapshot.OutputTokens < 0
            || snapshot.TotalTokens < componentTotal || normalizedEstimatedCost < 0 || normalizedLegacyUsd < 0
            || snapshot.ExchangeRates is null
            || !InitialExchangeRatesAreValid(snapshot.ExchangeRates, now))
            throw new ArgumentException("The initial LLM usage budget snapshot is invalid.", nameof(snapshot));
        return snapshot with
        {
            EstimatedCost = normalizedEstimatedCost,
            EstimatedCostCurrency = snapshotCurrency,
            EstimatedCostUsd = normalizedLegacyUsd,
            ExchangeRates = snapshot.ExchangeRates.ToArray()
        };
    }

    private static bool InitialExchangeRatesAreValid(
        IReadOnlyList<CurrencyExchangeQuote> quotes,
        DateTimeOffset now)
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quote in quotes)
        {
            if (quote is null || quote.Rate <= 0 || quote.AsOfUtc == default || quote.AsOfUtc > now
                || string.IsNullOrWhiteSpace(quote.Source) || quote.Source.Length > 160)
            {
                return false;
            }

            try
            {
                var pair = NormalizeCostCurrency(quote.SourceCurrency)
                           + "/"
                           + NormalizeCostCurrency(quote.TargetCurrency);
                if (!pairs.Add(pair))
                    return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizeStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return "llm_call";

        var safe = new string(stage
            .Take(64)
            .Select(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "llm_call" : safe;
    }

    private sealed record LocalReservation(LLMUsageBudgetScope Scope, bool OwnsCostGate);
    private sealed record ParsedUsage(long? InputTokens, long? OutputTokens, long TotalTokens);
}
