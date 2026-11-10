using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>A host persists attempts and admits their possible usage before each send.
/// Calls sharing a journal must be serialized by the host. Save must be atomic.</summary>
public interface ILLMHttpRetryJournal
{
    Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct);
    Task SaveAsync(LLMHttpRetryState state, CancellationToken ct);
}

/// <summary>Encrypted by durable hosts. A missing response is unknown usage, never zero.</summary>
public sealed class LLMHttpRetryState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LLMTransportSelection? Transport { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestMethod { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestBody { get; set; }
    public string Fingerprint { get; set; } = "";
    public List<LLMHttpAttempt> Attempts { get; set; } = [];
    public double DelayMilliseconds { get; set; }
}

public sealed class LLMHttpReceiptRetrieval
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int? Status { get; set; }
    public string? Body { get; set; }
    public string? Failure { get; set; }
}

public sealed class LLMHttpAttempt
{
    private LLMHttpReadTermination? _readTermination;
    // Absent on historical attempts. Observation only: never used for admission or retry decisions.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LLMHttpReadTermination? ReadTermination { get => _readTermination; set => _readTermination = value; }
    internal void ObserveReadTermination(LLMHttpReadTermination value)
        => Interlocked.CompareExchange(ref _readTermination, value, null);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancellationSource { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseHttpVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndependentGenerationDurationExhausted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderErrorCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamFailure { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LLMHttpReceiptRetrieval>? ReceiptRetrievals { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProtocolComplete { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssembledBody { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialBodyBase64 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderResponseId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StreamCursor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TerminationStatus { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UsageKnown { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FirstByteAtUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastByteAtUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryDecision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAtUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedAtUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ObservedStatus { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialBody { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResponseBodyComplete { get; set; }
    public string Id { get; set; } = "";
    public int? Status { get; set; }
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public string? RetryAfter { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
}

/// <summary>What the response stream reader observed, not which remote hop caused it.
/// No exception messages, headers, URLs or stacks are retained.</summary>
public sealed class LLMHttpReadTermination
{
    public string Observer { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HttpError { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SocketError { get; set; }

    internal static LLMHttpReadTermination FromException(Exception error, DateTimeOffset now, string observer = "response_stream")
    {
        var result = new LLMHttpReadTermination { AtUtc = now, Observer = observer,
            Outcome = error is OperationCanceledException ? "cancelled" : "exception",
            ErrorType = error switch { OperationCanceledException => "operation_cancelled",
                HttpIOException => "http_io", HttpRequestException => "http_request",
                System.Net.Sockets.SocketException => "socket", IOException => "io", _ => "other" } };
        // Bound traversal and retain only framework enum codes, never arbitrary exception content.
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            result.HttpError ??= current switch { HttpIOException http => http.HttpRequestError.ToString(),
                HttpRequestException http => http.HttpRequestError.ToString(), _ => null };
            if (current is System.Net.Sockets.SocketException socket) result.SocketError ??= socket.SocketErrorCode.ToString();
        }
        return result;
    }
}

/// <summary>Opt-in for one side-effect-free, synchronous generation HTTP operation.
/// The journal must reserve conservative usage before accepting a new attempt.
/// No context means POSTs are not retried. GETs need no usage reservation.
/// A policy override permits confirmed transient responses. An explicit independent-generation
/// duration additionally permits bounded fresh attempts after observed transport loss. Its
/// host must establish tool-free generation and retain every uncertain reservation.</summary>
public sealed class LLMHttpRetryContext
{
    private static readonly AsyncLocal<LLMHttpRetryContext?> Ambient = new();
    internal static LLMHttpRetryContext? Current => Ambient.Value;
    internal string RequestId { get; }
    internal ILLMHttpRetryJournal Journal { get; }
    internal LLMProviderRetryPolicyOptions? PolicyOverride { get; }
    internal int? IndependentGenerationMaxElapsedMilliseconds { get; }
    private ExceptionDispatchInfo? _journalFailure;

    public LLMHttpRetryContext(string requestId, ILLMHttpRetryJournal journal, LLMProviderRetryPolicyOptions? policyOverride = null,
        int? independentGenerationMaxElapsedMilliseconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(journal);
        if (independentGenerationMaxElapsedMilliseconds is not null &&
            (independentGenerationMaxElapsedMilliseconds is not (> 0 and <= 3_660_000) || policyOverride is null ||
             policyOverride.MaxUncertainRetries is < 1 or > 2 || policyOverride.MaxUncertainRetries >= policyOverride.MaxAttempts))
            throw new ArgumentException("Independent tool-free generation requires an explicit bounded policy.", nameof(independentGenerationMaxElapsedMilliseconds));
        if (policyOverride is not null && (policyOverride.MaxAttempts is < 1 or > 3 ||
            independentGenerationMaxElapsedMilliseconds is null && policyOverride.MaxUncertainRetries != 0 ||
            policyOverride.AttemptTimeoutMilliseconds <= 0 || policyOverride.BaseDelayMilliseconds <= 0 ||
            policyOverride.MaxDelayMilliseconds < policyOverride.BaseDelayMilliseconds || policyOverride.MaxTotalDelayMilliseconds < 0))
            throw new ArgumentException("A confirmed-response policy requires at most three attempts and no uncertain retries.", nameof(policyOverride));
        RequestId = requestId;
        Journal = journal;
        PolicyOverride = policyOverride?.Clone();
        IndependentGenerationMaxElapsedMilliseconds = independentGenerationMaxElapsedMilliseconds;
    }
    internal async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
    {
        try { return await Journal.LoadAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { _journalFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }
    internal async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        try { await Journal.SaveAsync(state, ct).ConfigureAwait(false); }
        // An attempt deadline may interrupt a progress write. The transport
        // owner must durably save its final outcome with a noncancelled token
        // before recovery or another attempt. Do not retain this cancellation
        // as a later storage failure after that final save has succeeded.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { _journalFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }
    // A storage/admission failure belongs to the host, not to provider retry classification.
    internal void ThrowIfJournalFailed() => Volatile.Read(ref _journalFailure)?.Throw();
    internal void RethrowJournalFailure() => Interlocked.Exchange(ref _journalFailure, null)?.Throw();
    public IDisposable Activate()
    {
        if (Ambient.Value is not null) throw new InvalidOperationException("Nested HTTP retry contexts are not allowed.");
        Ambient.Value = this;
        return new Scope();
    }

    /// <summary>
    /// Adds a mandatory host admission journal to the active request without changing
    /// its retry policy or recovery owner. Both reservations precede every physical send.
    /// A failed/mismatched journal forbids dispatch; it never authorizes replay.
    /// </summary>
    public static IDisposable RequireAdditionalJournal(ILLMHttpRetryJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var owner = Ambient.Value ?? throw new InvalidOperationException("An active host HTTP journal is required.");
        Ambient.Value = new(owner.RequestId, new RequiredJournal(owner.Journal, journal), owner.PolicyOverride,
            owner.IndependentGenerationMaxElapsedMilliseconds);
        return new Scope(owner);
    }

    private sealed class RequiredJournal(ILLMHttpRetryJournal owner, ILLMHttpRetryJournal required) : ILLMHttpRetryJournal
    {
        public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
        {
            var state = await owner.LoadAsync(ct).ConfigureAwait(false);
            var other = await required.LoadAsync(ct).ConfigureAwait(false);
            if (other is { Attempts.Count: > 0 } && (state is null ||
                JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) !=
                JsonSerializer.Serialize(other, LLMHttpRetryJsonContext.Default.LLMHttpRetryState)))
                throw new InvalidOperationException("Required HTTP journals disagree; independent receipt recovery is required.");
            return state;
        }
        public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            await required.SaveAsync(state, ct).ConfigureAwait(false);
            await owner.SaveAsync(state, ct).ConfigureAwait(false);
        }
    }
    private sealed class Scope(LLMHttpRetryContext? previous = null) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Ambient.Value = previous; }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LLMProviderRetryPolicyOptions))]
[JsonSerializable(typeof(LLMHttpRetryState))]
public partial class LLMHttpRetryJsonContext : JsonSerializerContext;
