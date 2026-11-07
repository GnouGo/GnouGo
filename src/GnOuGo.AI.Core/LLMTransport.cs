using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.AI.Core;

[JsonConverter(typeof(JsonStringEnumConverter<LLMStreamingMode>))]
public enum LLMStreamingMode { Auto, Streaming, NonStreaming }

public sealed class LLMModelTransportOptions
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LLMStreamingMode? StreamingMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? StreamingSupport { get; set; }
}

/// <summary>Credential-free, immutable resolution for one native generation protocol.</summary>
public sealed record LLMTransportSelection(LLMStreamingMode RequestedMode, LLMStreamingMode EffectiveMode, string Protocol, string CapabilitySource)
{
    public bool IsStreaming => EffectiveMode == LLMStreamingMode.Streaming;
}

public static class LLMTransportResolver
{
    public const string ChatCompletions = "ChatCompletions";
    public const string Responses = "Responses";
    public const string Messages = "Messages";
    public const string MessageBatches = "MessageBatches";
    public const string OllamaChat = "OllamaChat";
    public const string Local = "Local";
    internal static readonly HttpRequestOptionsKey<LLMTransportSelection> SelectionKey = new("gnougo.llm.transport");

    public static string Protocol(ModelProviderOptions provider, bool background) => provider.ResolvedType switch
    {
        "openai" => background && provider.RequestPolicy.BackgroundProtocol != LLMBackgroundProtocolMode.ChatCompletions ? Responses : ChatCompletions,
        "copilot" => ChatCompletions,
        "anthropic" => background ? MessageBatches : Messages,
        "ollama" => OllamaChat,
        "local" => Local,
        _ => "Unsupported"
    };

    public static LLMTransportSelection Resolve(string model, ModelProviderOptions provider, bool background, ModelCapabilityMetadata? capabilities = null)
    {
        var protocol = Protocol(provider, background);
        LLMModelTransportOptions? modelPolicy = null;
        provider.ModelTransportOverrides?.TryGetValue(model, out modelPolicy);
        var requested = modelPolicy?.StreamingMode ?? provider.RequestPolicy.StreamingMode ?? LLMStreamingMode.Auto;
        bool? declared = null;
        var source = "unknown";
        if (modelPolicy?.StreamingSupport?.TryGetValue(protocol, out var exact) == true) { declared = exact; source = "exact deployment declaration"; }
        else if (provider.RequestPolicy.StreamingSupport?.TryGetValue(protocol, out var route) == true) { declared = route; source = "configured route declaration"; }
        else if (IsOfficialRoute(provider) && capabilities?.SupportsStreaming is { } supported) { declared = supported; source = "declared model capability on native route"; }
        // A declaration cannot grant a protocol the adapter cannot implement.
        if (protocol is MessageBatches or "Unsupported") { declared = false; source = "native protocol does not support generation streaming"; }
        if (capabilities?.SupportsStreaming == false) { declared = false; source = "model explicitly excludes streaming"; }
        if (requested == LLMStreamingMode.Streaming && declared != true)
            throw new InvalidOperationException($"Streaming is not confirmed for the configured model/deployment and {protocol} route ({source}). Declare support or select Auto/NonStreaming before inference.");
        var effective = requested == LLMStreamingMode.NonStreaming || declared != true ? LLMStreamingMode.NonStreaming : LLMStreamingMode.Streaming;
        return new(requested, effective, protocol, source);
    }

    public static LLMTransportSelection ForRequest(string model, ModelProviderOptions provider, LLMClientRequest request)
        => request.ResolvedTransport ?? Resolve(model, provider, request.UseBackgroundMode,
            new LLMModelMetadataResolver(new LLMOptions()).ResolveDeclaredMetadata(provider.ResolvedType, model)?.Capabilities);

    private static bool IsOfficialRoute(ModelProviderOptions provider)
    {
        if (provider.ResolvedType == "local") return true;
        if (!Uri.TryCreate(provider.Url, UriKind.Absolute, out var uri)) return false;
        return provider.ResolvedType switch
        {
            "openai" => uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase),
            "anthropic" => uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase),
            // The former GitHub Models service is retired. Custom Copilot routes require declarations.
            "ollama" => uri.IsLoopback,
            _ => false
        };
    }

    public static Dictionary<string, LLMModelTransportOptions>? CloneOverrides(Dictionary<string, LLMModelTransportOptions>? source)
        => source?.ToDictionary(p => p.Key, p => new LLMModelTransportOptions
        {
            StreamingMode = p.Value.StreamingMode,
            StreamingSupport = p.Value.StreamingSupport is null ? null : new(p.Value.StreamingSupport, StringComparer.Ordinal)
        }, StringComparer.Ordinal);

    internal static void ValidateSupport(Dictionary<string, bool>? support)
    {
        if (support is null) return;
        foreach (var key in support.Keys)
            if (key is not (ChatCompletions or Responses or Messages or MessageBatches or OllamaChat or Local))
                throw new InvalidOperationException("Unknown native protocol in streaming support declaration.");
    }

    /// <summary>Applies provider-native wire flags before hashing/admission. Never changes the route.</summary>
    public static void Apply(HttpRequestMessage message, LLMTransportSelection selection, ModelCapabilityMetadata? capabilities = null)
    {
        message.Options.Set(SelectionKey, selection);
        if (!selection.IsStreaming) return; // Preserve historical non-streaming wire bytes exactly.
        if (message.Content is not ByteArrayContent content) throw new InvalidOperationException("Generation requires a captured JSON body.");
        // ByteArrayContent is already materialized; no network or arbitrary asynchronous content is read here.
        var body = JsonNode.Parse(content.ReadAsByteArrayAsync().GetAwaiter().GetResult())!.AsObject();
        if (selection.IsStreaming)
        {
            body["stream"] = true;
            if (selection.Protocol == ChatCompletions && capabilities?.UnsupportedRequestParameters?.Contains("stream_options", StringComparer.Ordinal) != true)
                body["stream_options"] = new JsonObject { ["include_usage"] = true };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(selection.Protocol == OllamaChat ? "application/x-ndjson" : "text/event-stream"));
        }
        else if (selection.Protocol == OllamaChat) body["stream"] = false;
        else { body.Remove("stream"); body.Remove("stream_options"); }
        var replacement = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, StreamingJsonContext.Default.JsonObject));
        replacement.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Content = replacement;
        content.Dispose();
        Activity.Current?.SetTag("gnougo.llm.transport.requested", selection.RequestedMode.ToString());
        Activity.Current?.SetTag("gnougo.llm.transport.effective", selection.EffectiveMode.ToString());
        Activity.Current?.SetTag("gnougo.llm.transport.protocol", selection.Protocol);
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(LLMTransportSelection))]
[JsonSerializable(typeof(Dictionary<string, LLMModelTransportOptions>))]
public partial class StreamingJsonContext : JsonSerializerContext;
