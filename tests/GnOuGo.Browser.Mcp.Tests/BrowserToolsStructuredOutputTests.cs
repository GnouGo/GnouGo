using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserToolsStructuredOutputTests
{
    [Theory]
    [InlineData("complete", 200)]
    [InlineData("http_failure", 404)]
    [InlineData("incomplete", null)]
    [InlineData("wrong_final", null)]
    [InlineData("no_hops", null)]
    public void PublishedNavigationReceiptExposesItsOwnTerminalHttpResponse(string variant, int? status)
    {
        const string source = "https://example.invalid/source", final = "https://example.invalid/item";
        var terminal = new BrowserNavigationHop(final, "GET", variant == "http_failure" ? 404 : 200, null);
        var evidence = new BrowserNavigationEvidence(source, variant == "wrong_final" ? source : final,
            variant == "no_hops" ? [] : [new(source, "GET", 302, final), terminal], variant != "incomplete")
        {
            DocumentUrl = final + "?view=history", SupersededResponses = [new(final, "GET", 503, null)]
        };
        var wire = JsonSerializer.SerializeToNode(evidence, BrowserMcpJsonContext.Default.BrowserNavigationEvidence)!;
        var properties = PublishedContentProperties()["navigationEvidence"]!["properties"]!;
        var receiptSchema = properties["terminalResponse"];
        Assert.NotNull(receiptSchema);
        Assert.Equal("integer", (string?)receiptSchema["properties"]?["statusCode"]?["type"]);
        Assert.Equal("string", (string?)receiptSchema["properties"]?["method"]?["type"]);
        if (status.HasValue)
        {
            Assert.True(JsonNode.DeepEquals(wire["hops"]![1], wire["terminalResponse"]));
            Assert.Equal(status.Value, (int?)wire["terminalResponse"]!["statusCode"]);
        }
        else Assert.Null(wire["terminalResponse"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("matching")]
    public void PublishedAncestorContractAcceptsTheActualNullableDictionaryValue(string variant)
    {
        var record = new BrowserObservationRecord("link", "a", "main a", "main", "Observed", "https://example.invalid/item", null)
        {
            AncestorAttributes = new Dictionary<string, BrowserObservationAncestorAttribute?>
            {
                ["data-code"] = variant == "missing" ? null : new(variant == "empty" ? "" : "observed", "div", null, 0)
            }
        };
        var wire = JsonSerializer.SerializeToNode(record, BrowserMcpJsonContext.Default.BrowserObservationRecord)!;
        Assert.True(wire["ancestorAttributes"]!.AsObject().ContainsKey("data-code"));
        if (variant == "missing") Assert.Null(wire["ancestorAttributes"]!["data-code"]);
        else Assert.Equal(variant == "empty" ? "" : "observed", wire["ancestorAttributes"]!["data-code"]!["value"]!.ToString());
        foreach (var schema in PublishedRecordSchemas())
        {
            var contextSchema = schema["properties"]!["ancestorAttributes"]!["additionalProperties"]!;
            var types = contextSchema["type"] is JsonArray list ? list.Select(item => item!.ToString()).ToArray() : [contextSchema["type"]!.ToString()];
            Assert.Contains(wire["ancestorAttributes"]!["data-code"] is null ? "null" : "object", types);
            Assert.Equal(new[] { "value", "tag", "role", "index" }, contextSchema["required"]!.AsArray().Select(item => item!.ToString()));
            Assert.Equal("string", contextSchema["properties"]!["value"]!["type"]!.ToString());
            Assert.Equal("string", contextSchema["properties"]!["tag"]!["type"]!.ToString());
            Assert.Equal("integer", contextSchema["properties"]!["index"]!["type"]!.ToString());
        }
    }

    [Fact]
    public void PublishedObservationBoundsDistinguishPagesFromRecordsPerPage()
    {
        var properties = PublishedContentProperties();
        var snapshotPages = properties["observationSnapshot"]!["properties"]!["pages"]!;
        Assert.Equal(100, (int?)snapshotPages["maxItems"]);
        Assert.Equal(100, (int?)properties["observationManifest"]!["properties"]!["pages"]!["maxItems"]);
        Assert.Equal(200, (int?)properties["observation"]!["properties"]!["records"]!["maxItems"]);
        Assert.Equal(200, (int?)snapshotPages["items"]!["properties"]!["records"]!["maxItems"]);
    }

    private static JsonNode[] PublishedRecordSchemas()
    {
        var properties = PublishedContentProperties();
        return [properties["observation"]!["properties"]!["records"]!["items"]!,
            properties["observationSnapshot"]!["properties"]!["pages"]!["items"]!["properties"]!["records"]!["items"]!];
    }

    private static JsonNode PublishedContentProperties()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<BrowserServerSettings>>(Options.Create(new BrowserServerSettings()));
        services.AddSingleton<PlaywrightBrowserHost>();
        services.AddTransient<BrowserTools>();
        services.AddMcpServer().WithTools<BrowserTools>(BrowserMcpJson.SerializerOptions);
        using var provider = services.BuildServiceProvider();
        var descriptor = provider.GetServices<McpServerTool>().Single(tool => tool.ProtocolTool.Name == "browser_get_content");
        var schema = JsonNode.Parse(BrowserMcpJson.ContentSchema(descriptor.ProtocolTool.OutputSchema!.Value).GetRawText())!;
        return schema["properties"]!;
    }

    [Fact]
    public void AllBrowserMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(BrowserTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute != null)
            .ToArray();

        Assert.NotEmpty(toolMethods);

        foreach (var item in toolMethods)
        {
            Assert.True(item.Attribute!.UseStructuredContent, item.Method.Name);
            Assert.NotNull(item.Attribute.OutputSchemaType);
            Assert.NotEqual(typeof(object), item.Method.ReturnType);
            Assert.Equal(UnwrapToolReturnType(item.Method.ReturnType), item.Attribute.OutputSchemaType);
        }
    }

    [Fact]
    public void McpToolRegistration_CreatesToolDescriptorsWithOutputSchemas()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IOptions<BrowserServerSettings>>(Options.Create(new BrowserServerSettings()));
        services.AddSingleton<PlaywrightBrowserHost>();
        services.AddTransient<BrowserTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Browser.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<BrowserTools>(BrowserMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
        var contentTool = tools.Single(t => t.ProtocolTool.Name == "browser_get_content");
        var published = BrowserMcpJson.ContentSchema(contentTool.ProtocolTool.OutputSchema!.Value);
        var properties = published.GetProperty("properties");
        var acquisition = properties.GetProperty("acquisition").GetProperty("properties");
        Assert.True(acquisition.TryGetProperty("navigation", out _));
        Assert.True(acquisition.TryGetProperty("lastResponse", out _));
        Assert.True(acquisition.TryGetProperty("recoveries", out var recoveries));
        Assert.Equal("array", recoveries.GetProperty("type").EnumerateArray().First().GetString());
        var records = properties.GetProperty("observationSnapshot").GetProperty("properties").GetProperty("pages")
            .GetProperty("items").GetProperty("properties").GetProperty("records");
        Assert.Equal("array", records.GetProperty("type").GetString());
        Assert.Equal(properties.GetProperty("observation").GetProperty("properties").GetProperty("records").GetRawText(), records.GetRawText());
        Assert.Equal("string", records.GetProperty("items").GetProperty("properties").GetProperty("text").GetProperty("type").GetString());
        var record = records.GetProperty("items").GetProperty("properties");
        Assert.True(record.TryGetProperty("reference", out _));
        Assert.Contains(record.GetProperty("actions").GetProperty("items").GetProperty("enum").EnumerateArray(), e => e.GetString() == "activate");
        foreach (var name in new[] { "browser_click", "browser_fill", "browser_press", "browser_select" })
        {
            var input = tools.Single(t => t.ProtocolTool.Name == name).ProtocolTool.InputSchema;
            Assert.True(input.GetProperty("properties").TryGetProperty("reference", out _));
            if (input.TryGetProperty("required", out var required))
                Assert.DoesNotContain(required.EnumerateArray(), field => field.GetString() == "selector");
        }
        var url = tools.Single(t => t.ProtocolTool.Name == "browser_get_content").ProtocolTool.InputSchema.GetProperty("properties").GetProperty("url");
        Assert.True(url.TryGetProperty("pattern", out var pattern), "The producer must publish its absolute HTTP URL requirement.");
        Assert.Matches(pattern.GetString()!, "https://example.invalid/items/7");
        Assert.DoesNotMatch(pattern.GetString()!, "https%3A%2F%2Fexample.invalid%2Fitems%2F7");
    }

    [Fact]
    public void BrowserToolFailure_ReturnsStructuredFailure()
    {
        var result = BrowserToolFailure.Action("click", "#missing", new InvalidOperationException("selector missing"));

        Assert.False(result.Success);
        Assert.False(result.Ok);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
        Assert.Contains("selector missing", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#missing", result.Selector);
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;
}
