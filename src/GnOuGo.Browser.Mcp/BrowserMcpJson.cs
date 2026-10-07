using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Browser.Mcp;

internal static class BrowserMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    // Publish the same authoritative record contract at both consumer-visible
    // paths. The SDK otherwise emits a collection $ref with an empty items sibling;
    // a selected subtree must remain usable without its former document root.
    public static JsonElement ContentSchema(JsonElement generated)
    {
        var schema = JsonNode.Parse(generated.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        var records = properties["observation"]!["properties"]!["records"]!;
        records["items"]!["properties"]!["actions"]!["items"] = new JsonObject
            { ["type"] = "string", ["enum"] = new JsonArray("activate", "follow", "fill", "select", "press") };
        properties["observationSnapshot"]!["properties"]!["pages"]!["items"]!["properties"]!["records"] =
            properties["observation"]!["properties"]!["records"]!.DeepClone();
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, BrowserMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(BrowserContentResult))]
[JsonSerializable(typeof(BrowserObservationCapture))]
[JsonSerializable(typeof(BrowserActionResult))]
[JsonSerializable(typeof(BrowserKeyActionResult))]
[JsonSerializable(typeof(BrowserSelectResult))]
[JsonSerializable(typeof(BrowserWaitResult))]
[JsonSerializable(typeof(BrowserScreenshotResult))]
[JsonSerializable(typeof(BrowserCloseResult))]
[JsonSerializable(typeof(BrowserToolErrorResult))]
[JsonSerializable(typeof(BrowserToolCorrelation))]
[JsonSerializable(typeof(BrowserServerSettings))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BrowserMcpJsonContext : JsonSerializerContext;
