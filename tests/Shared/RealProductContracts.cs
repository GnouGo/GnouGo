using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Browser.Mcp;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace GnOuGo.Planning.Examples;

/// <summary>Producer registrations, without starting a browser or invoking a tool.</summary>
public static class RealProductContracts
{
    public const string Prompt = "Input: a product to search, e.g. ‘chaussure geox homme 45’. Go to Amazon, search for it, list products, visit each product page, extract name/description/price, and save the results as an XLSX file on disk.";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonObject Capture(DocumentPolicy policy)
    {
        var result = new JsonObject();
        Capture<BrowserTools>("browser", "GnOuGo.Browser.Mcp.BrowserMcpJson");
        Capture<DocumentTools>("document", "GnOuGo.Document.Mcp.DocumentMcpJson");
        return result;
        void Capture<T>(string source, string serializerType) where T : class
        {
            // Use the exact source-generated serializer configured by each producer.
            var serializer = (JsonSerializerOptions)typeof(T).Assembly.GetType(serializerType, throwOnError: true)!
                .GetProperty("SerializerOptions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var services = new ServiceCollection(); services.AddLogging();
            services.AddMcpServer().WithTools<T>(serializer);
            using var provider = services.BuildServiceProvider();
            var tools = provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new McpToolInfo
            {
                Name = t.Name,
                Description = typeof(T) == typeof(DocumentTools) && t.Name == "document_write" ? policy.BuildDocumentWriteToolDescription() : t.Description,
                InputSchema = JsonNode.Parse(t.InputSchema.GetRawText()),
                OutputSchema = t.OutputSchema is { } output ? JsonNode.Parse(output.GetRawText()) : null,
                Meta = t.Meta?.DeepClone()
            }).ToArray();
            result[source] = JsonSerializer.SerializeToNode(tools, Json);
        }
    }

    public sealed class Factory(JsonObject snapshot) : IMcpClientFactory
    {
        public int InvocationAttempts { get; private set; }
        public List<string> DiscoveryReads { get; } = [];
        public Func<string, string, JsonNode?, CancellationToken, Task<McpCallResult>>? Handler { get; set; }
        public IReadOnlyList<McpServerMetadata> ServerMetadata => snapshot.Select(p => new McpServerMetadata { Name = p.Key, Description = p.Key }).ToArray();
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshot[serverName] is null) throw new ArgumentException("Unknown source.");
            return Task.FromResult<IMcpSession>(new Session(this, serverName));
        }
        private sealed class Session(Factory owner, string name) : IMcpSession
        {
            public string ServerName => name;
            public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); owner.DiscoveryReads.Add(name);
                return Task.FromResult<IReadOnlyList<McpToolInfo>>(JsonSerializer.Deserialize<McpToolInfo[]>(snapshotNode(), Json)!);
            }
            private JsonNode snapshotNode() => owner.Snapshot[name]!;
            public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
            public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
            public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
            {
                owner.InvocationAttempts++;
                return owner.Handler?.Invoke(name, toolName, arguments, ct) ?? throw new InvalidOperationException("Planning-only diagnosis forbids all tool invocations.");
            }
            public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct) => throw new InvalidOperationException("No prompt invocation is allowed.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private JsonObject Snapshot => snapshot;
    }
}
