using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.Browser.Mcp;

internal static class BrowserMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, BrowserMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(BrowserContentResult))]
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
