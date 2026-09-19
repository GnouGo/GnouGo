using System.Text.Json.Serialization;

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
/// No context means uncertain POSTs are not retried. GETs need no usage reservation.</summary>
public sealed class LLMHttpRetryContext(string requestId, ILLMHttpRetryJournal journal)
{
    private static readonly AsyncLocal<LLMHttpRetryContext?> Ambient = new();
    internal static LLMHttpRetryContext? Current => Ambient.Value;
    internal string RequestId { get; } = requestId;
    internal ILLMHttpRetryJournal Journal { get; } = journal;
    internal LLMHttpRetryState? State { get; set; }
    public IDisposable Activate()
    {
        if (Ambient.Value is not null) throw new InvalidOperationException("Nested HTTP retry contexts are not allowed.");
        Ambient.Value = this;
        return new Scope();
    }
    private sealed class Scope : IDisposable { public void Dispose() => Ambient.Value = null; }
}

[JsonSerializable(typeof(LLMHttpRetryState))]
public partial class LLMHttpRetryJsonContext : JsonSerializerContext;
