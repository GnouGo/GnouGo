using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;

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
    public string Fingerprint { get; set; } = "";
    public List<LLMHttpAttempt> Attempts { get; set; } = [];
    public double DelayMilliseconds { get; set; }
}

public sealed class LLMHttpAttempt
{
    public string Id { get; set; } = "";
    public int? Status { get; set; }
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public string? RetryAfter { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
}

/// <summary>Opt-in for one side-effect-free, synchronous generation HTTP operation.
/// The journal must reserve conservative usage before accepting a new attempt.
/// No context means POSTs are not retried. GETs need no usage reservation.
/// A policy override permits only confirmed transient responses, never uncertain replay.</summary>
public sealed class LLMHttpRetryContext
{
    private static readonly AsyncLocal<LLMHttpRetryContext?> Ambient = new();
    internal static LLMHttpRetryContext? Current => Ambient.Value;
    internal string RequestId { get; }
    internal ILLMHttpRetryJournal Journal { get; }
    internal LLMProviderRetryPolicyOptions? PolicyOverride { get; }
    private ExceptionDispatchInfo? _journalFailure;

    public LLMHttpRetryContext(string requestId, ILLMHttpRetryJournal journal, LLMProviderRetryPolicyOptions? policyOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(journal);
        if (policyOverride is not null && (policyOverride.MaxAttempts is < 1 or > 3 || policyOverride.MaxUncertainRetries != 0 ||
            policyOverride.AttemptTimeoutMilliseconds <= 0 || policyOverride.BaseDelayMilliseconds <= 0 ||
            policyOverride.MaxDelayMilliseconds < policyOverride.BaseDelayMilliseconds || policyOverride.MaxTotalDelayMilliseconds < 0))
            throw new ArgumentException("A confirmed-response policy requires at most three attempts and no uncertain retries.", nameof(policyOverride));
        RequestId = requestId;
        Journal = journal;
        PolicyOverride = policyOverride?.Clone();
    }
    internal async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
    {
        try { return await Journal.LoadAsync(ct).ConfigureAwait(false); }
        catch (Exception error) { _journalFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }
    internal async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        try { await Journal.SaveAsync(state, ct).ConfigureAwait(false); }
        catch (Exception error) { _journalFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }
    // A storage/admission failure belongs to the host, not to provider retry classification.
    internal void RethrowJournalFailure() => Interlocked.Exchange(ref _journalFailure, null)?.Throw();
    public IDisposable Activate()
    {
        if (Ambient.Value is not null) throw new InvalidOperationException("Nested HTTP retry contexts are not allowed.");
        Ambient.Value = this;
        return new Scope();
    }
    private sealed class Scope : IDisposable { public void Dispose() => Ambient.Value = null; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LLMProviderRetryPolicyOptions))]
[JsonSerializable(typeof(LLMHttpRetryState))]
public partial class LLMHttpRetryJsonContext : JsonSerializerContext;
