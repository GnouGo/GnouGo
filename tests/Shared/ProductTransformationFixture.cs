using System.Text.Json.Nodes;
using System.Xml.Linq;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Planning.Examples;

/// <summary>Sanitized observations and an independent workbook oracle. No network or model service.</summary>
public sealed class ProductTransformationFixture(string variant = "nominal") : ILLMClient
{
    public const string SearchUrl = "https://www.amazon.example/search?q=lamp";
    public const string OutputPath = "workflows/product-search/products.xlsx";
    public List<string> Effects { get; } = [];
    public List<LLMRequest> Calls { get; } = [];
    public string? WrittenContent { get; private set; }
    public bool InvalidResponse { get; set; }
    public bool FabricatedResponse { get; set; }
    public CancellationTokenSource? CancelDuringExtraction { get; set; }
    public Func<string, string, JsonObject>? Write { get; set; }
    public string BrowserSource { get; init; } = "browser";
    public string DocumentSource { get; init; } = "document";
    public string ReadMethod { get; init; } = "read_page";
    public string WriteMethod { get; init; } = "write_document";
    public string CloseMethod { get; init; } = "close_browser";
    public bool ReverseTools { get; init; }

    public string[][] ExpectedRows => variant switch
    {
        "empty" => [["Name", "Description", "Price"]],
        "changed" => [["Name", "Description", "Price"], ["Changed lamp", "New description", "9,90 €"], ["Second", "Updated", "12.00"]],
        "missing" => [["Name", "Description", "Price"], ["Only a name", "", ""]],
        _ => [["Name", "Description", "Price"], ["Lampe été, \"A\"", "Bright and small {{values}}", "19,99 €"], ["東京 lamp", "Line one Line two", "25.00"]]
    };
    public string SearchHtml => variant switch
    {
        "empty" => "<html><main /></html>",
        "missing" => "<html><main><a href='https://www.amazon.example/p/missing'>Only a name</a></main></html>",
        _ => "<html><main><a href='https://www.amazon.example/p/one'>First</a><a href='https://www.amazon.example/p/two'>Second</a></main></html>"
    };
    private string Page(string url) => url.EndsWith("/missing", StringComparison.Ordinal)
        ? "<html><h1>Only a name</h1></html>"
        : url.EndsWith("/one", StringComparison.Ordinal)
            ? variant == "changed" ? "<html><h1>Changed lamp</h1><p>New description</p><price>9,90 €</price></html>"
                : "<html><h1>Lampe été, &quot;A&quot;</h1><p>Bright\tand small {{values}}</p><price>19,99 €</price></html>"
            : variant == "changed" ? "<html><h1>Second</h1><p>Updated</p><price>12.00</price></html>"
                : "<html><h1>東京 lamp</h1><p>Line one\nLine two</p><price>25.00</price></html>";

    public InMemoryMcpClientFactory Factory(bool distractors = false)
    {
        var factory = new InMemoryMcpClientFactory();
        var browser = new MockMcpServerConfig { Description = "Browse web pages and read their HTML; close the browser during cleanup." };
        var document = new MockMcpServerConfig { Description = "Write documents, including XLSX from tabular text, inside allowed workspace paths." };
        Add(browser, ReadMethod, "Read the requested web page as HTML. Returns raw content; extraction requires a typed transformation.", "read",
            ObjectSchema(("url", "string")), ObjectSchema(("content", "string")), args =>
            {
                var url = args!["url"]!.ToString(); Effects.Add("read:" + url);
                return new() { ["content"] = url == SearchUrl ? SearchHtml : Page(url) };
            });
        Add(browser, CloseMethod, "Close the browser even after failure or cancellation.", "lifecycle", ObjectSchema(), ObjectSchema(), _ => { Effects.Add("close"); return new(); });
        Add(document, WriteMethod, "Write a document to the allowed workspace. Creates missing parent directories. XLSX accepts tab-separated rows with a header. Normalize embedded tabs and line breaks in cells before writing.", "write",
            ObjectSchema(("path", "string"), ("content", "string")), ObjectSchema(("path", "string")), args =>
            {
                var path = args!["path"]!.ToString(); var content = args["content"]!.ToString();
                Effects.Add("write:" + path); WrittenContent = content;
                return Write?.Invoke(path, content) ?? new() { ["path"] = path };
            });
        // Match the retained source sizes: nine Browser operations and four Document
        // operations, including ordinary irrelevant operations. Their metadata is kept.
        for (var i = 0; i < 7; i++) Add(browser, "other_browser_" + i, "Inspect unrelated browser configuration " + i, "read", ObjectSchema(), ObjectSchema(), _ => throw new InvalidOperationException("Irrelevant operation"));
        for (var i = 0; i < 3; i++) Add(document, "other_document_" + i, "Read document configuration " + i, "read", ObjectSchema(), ObjectSchema(), _ => throw new InvalidOperationException("Irrelevant operation"));
        if (ReverseTools) { browser.Tools.Reverse(); document.Tools.Reverse(); }
        factory.RegisterServer(BrowserSource, browser); factory.RegisterServer(DocumentSource, document);
        if (distractors) for (var i = 0; i < 10; i++) factory.RegisterServer("unrelated_" + i, new() { Description = "Unrelated inventory statistics " + i });
        return factory;
    }
    private static JsonObject ObjectSchema(params (string Name, string Type)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, new JsonObject { ["type"] = f.Type }))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
    private static void Add(MockMcpServerConfig server, string name, string description, string effect, JsonObject input, JsonObject output, Func<JsonNode?, JsonObject> action)
    {
        server.Tools.Add(new() { Name = name, Description = description, EffectKind = effect, InputSchema = input, OutputSchema = output });
        server.ToolHandlers[name] = args => new() { Content = action(args) };
    }
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Calls.Add(request);
        if (InvalidResponse) return Task.FromResult(new LLMResponse { Json = new JsonObject { ["urls"] = "not an array" } });
        var marker = request.Prompt.LastIndexOf("Business data (JSON):\n", StringComparison.Ordinal);
        if (marker < 0) marker = request.Prompt.LastIndexOf("Business data: ", StringComparison.Ordinal);
        var start = request.Prompt.IndexOf('{', marker);
        var data = JsonNode.Parse(request.Prompt[start..])!;
        JsonObject output;
        if (request.StructuredOutputSchema?["properties"]?["urls"] is not null)
        {
            var html = XDocument.Parse(data["html"]!.ToString());
            output = new() { ["urls"] = new JsonArray(html.Descendants("a").Select(a => (JsonNode?)JsonValue.Create(a.Attribute("href")!.Value)).ToArray()) };
        }
        else if (request.StructuredOutputSchema?["properties"]?["name"] is not null)
        {
            CancelDuringExtraction?.Cancel(); ct.ThrowIfCancellationRequested();
            var html = XDocument.Parse(data["html"]!.ToString());
            output = new() { ["name"] = html.Descendants("h1").Single().Value, ["description"] = html.Descendants("p").SingleOrDefault()?.Value, ["price"] = html.Descendants("price").SingleOrDefault()?.Value };
        }
        else
        {
            var rows = new List<string> { "Name\tDescription\tPrice" };
            foreach (var record in data["records"]!.AsArray())
                rows.Add(string.Join('\t', new[] { "name", "description", "price" }.Select(field => string.Join(' ', (record![field]?.ToString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))));
            output = new() { ["content"] = string.Join('\n', rows) };
        }
        if (FabricatedResponse && output.ContainsKey("content")) output["content"] = "Name\tDescription\tPrice\nFabricated\tNo observation\t0";
        return Task.FromResult(new LLMResponse { Json = output });
    }
    public bool VerifyText() => WrittenContent is { } text && text.Split('\n').Select(row => row.Split('\t')).SequenceEqual(ExpectedRows, RowComparer.Instance);
    private sealed class RowComparer : IEqualityComparer<string[]>
    {
        internal static readonly RowComparer Instance = new();
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] obj) => obj.Length;
    }
}
