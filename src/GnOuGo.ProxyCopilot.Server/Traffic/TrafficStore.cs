using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Traffic;

public sealed record TrafficSummary(string Id, string TenantId, string Provider, string Model, string Protocol,
    DateTimeOffset StartedAt, string Status, int? StatusCode, double DurationMs, double? FirstTokenMs,
    JsonObject? Usage, string? Error, bool Truncated);
public sealed record CapturedBody(string Text, bool Truncated);
public sealed record TrafficDetail(TrafficSummary Summary, Dictionary<string, CapturedBody> Bodies);
public sealed record TrafficSnapshot(long Version, TrafficSummary[] Calls);

public interface ITrafficStore
{
    string Start(ModelRoute route, byte[] request);
    void AddCredential(string id, string credential);
    void Append(string id, string body, ReadOnlySpan<byte> bytes);
    void Progress(string id, JsonObject chunk);
    void Complete(string id, string status, int statusCode, string? error = null);
    TrafficSnapshot Snapshot();
    TrafficDetail? Detail(string id);
    void Clear();
    TrafficSubscription Subscribe();
}

public sealed class TrafficSubscription(ChannelReader<long> reader, Action dispose) : IDisposable
{
    public ChannelReader<long> Reader { get; } = reader;
    public void Dispose() => dispose();
}

public sealed class TrafficStore(ProxyOptions options, CredentialRedactor redactor) : ITrafficStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<Channel<long>> _subscribers = [];
    private long _version;
    private long _bytes;

    public string Start(ModelRoute route, byte[] request)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _entries.Add(id, new Entry(id, options.TenantId, route));
            _order.AddLast(id);
            while (_entries.Count > options.Capture.MaxCalls) EvictOldest();
            Changed();
        }
        Append(id, "clientRequest", request);
        return id;
    }

    public void AddCredential(string id, string credential)
    {
        lock (_gate)
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Credentials.Add(credential);
                _bytes += Encoding.UTF8.GetByteCount(credential);
                entry.Bytes += Encoding.UTF8.GetByteCount(credential);
                EnforceBudget();
            }
    }

    public void Append(string id, string body, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            if (!entry.Bodies.TryGetValue(body, out var captured)) entry.Bodies.Add(body, captured = new Body());
            var count = Math.Min(bytes.Length, options.Capture.MaxBodyBytes - (int)captured.Data.Length);
            if (count > 0)
            {
                var previousCapacity = captured.Data.Capacity;
                captured.Data.Write(bytes[..count]);
                var allocated = captured.Data.Capacity - previousCapacity;
                _bytes += allocated; entry.Bytes += allocated;
            }
            captured.Truncated |= count != bytes.Length;
            EnforceBudget();
            Changed();
        }
    }

    public void Progress(string id, JsonObject chunk)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            if (chunk["usage"] is JsonObject usage) entry.Usage = Protocols.ChatContract.Usage(
                Protocols.ChatContract.Count(usage["prompt_tokens"]), Protocols.ChatContract.Count(usage["completion_tokens"]));
            if (entry.FirstTokenMs is null && chunk["choices"] is JsonArray choices && choices.Any(c =>
                c?["delta"]?["tool_calls"] is JsonArray { Count: > 0 }
                || c?["delta"]?["content"] is JsonValue text && text.TryGetValue<string>(out var value) && value.Length > 0
                || c?["message"] is JsonObject)) entry.FirstTokenMs = entry.Elapsed;
            Changed();
        }
    }

    public void Complete(string id, string status, int statusCode, string? error = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            entry.Status = status; entry.StatusCode = statusCode; entry.Duration = entry.Elapsed;
            entry.Error = error; Changed();
        }
    }

    public TrafficSnapshot Snapshot()
    {
        lock (_gate) return new(_version, _order.Reverse().Select(id => Summary(_entries[id])).ToArray());
    }

    public TrafficDetail? Detail(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return null;
            return new(Summary(entry), entry.Bodies.ToDictionary(b => b.Key, b => new CapturedBody(
                redactor.Redact(Encoding.UTF8.GetString(b.Value.Data.GetBuffer(), 0, (int)b.Value.Data.Length), entry.Credentials), b.Value.Truncated)));
        }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _order.Clear(); _bytes = 0; Changed(); }
    }

    public TrafficSubscription Subscribe()
    {
        lock (_gate)
        {
            if (_subscribers.Count >= 16) throw new ProxyException(429, "too_many_viewers", "Too many live traffic viewers.");
            var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            _subscribers.Add(channel); channel.Writer.TryWrite(_version);
            return new(channel.Reader, () => { lock (_gate) { _subscribers.Remove(channel); channel.Writer.TryComplete(); } });
        }
    }

    private TrafficSummary Summary(Entry entry) => new(entry.Id, entry.TenantId, entry.Route.Provider, entry.Route.Id, entry.Route.Type,
        entry.StartedAt, entry.Status, entry.StatusCode, entry.Duration ?? entry.Elapsed, entry.FirstTokenMs,
        entry.Usage?.DeepClone().AsObject(), entry.Error is null ? null : redactor.Redact(entry.Error, entry.Credentials), entry.Bodies.Values.Any(b => b.Truncated));
    private void Changed() { _version++; foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(_version); }
    private void EnforceBudget() { while (_bytes > options.Capture.MaxTotalBytes && _order.Count > 0) EvictOldest(); }
    private void EvictOldest()
    {
        var id = _order.First!.Value;
        _bytes -= _entries[id].Bytes;
        _entries.Remove(id); _order.RemoveFirst();
    }

    private sealed class Entry(string id, string tenantId, ModelRoute route)
    {
        public string Id { get; } = id;
        public string TenantId { get; } = tenantId;
        public ModelRoute Route { get; } = route;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        public double Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        public string Status { get; set; } = "running";
        public int? StatusCode { get; set; }
        public double? Duration { get; set; }
        public double? FirstTokenMs { get; set; }
        public JsonObject? Usage { get; set; }
        public string? Error { get; set; }
        public long Bytes { get; set; }
        public List<string> Credentials { get; } = [];
        public Dictionary<string, Body> Bodies { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Body
    {
        public MemoryStream Data { get; } = new();
        public bool Truncated { get; set; }
    }
}
