using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Planning.Benchmark;

// A benchmark transport boundary. No external business operations can execute.
internal sealed class FrozenCatalog(JsonArray discovery, Func<string, string, JsonNode?, McpCallResult>? execute = null) : IMcpClientFactory
{
    public IReadOnlyList<McpServerMetadata> ServerMetadata => discovery.Select(server => new McpServerMetadata
    {
        Name = server!["name"]!.ToString(), Description = server["description"]?.ToString()
    }).ToArray();
    public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(new Session(serverName,
        discovery.OfType<JsonObject>().SingleOrDefault(s => s["name"]!.ToString() == serverName), execute));

    private sealed class Session(string name, JsonObject? server, Func<string, string, JsonNode?, McpCallResult>? execute) : IMcpSession
    {
        public string ServerName => name;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>(
            server?["tools"] is JsonArray tools ? tools.Select(t => JsonSerializer.Deserialize(t!, BenchmarkJson.Default.McpToolInfo)!).ToArray() : []);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>(
            server?["prompts"] is JsonArray prompts ? prompts.Select(p => JsonSerializer.Deserialize(p!, BenchmarkJson.Default.McpPromptInfo)!).ToArray() : []);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct) => throw new InvalidOperationException("A frozen prompt response is required.");
        public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
        {
            if (name == "GnOuGo.Agent.Mcp" && toolName == "agent_get_by_name")
                return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" } });
            if (execute is not null) return Task.FromResult(execute(name, toolName, arguments));
            throw new InvalidOperationException("No frozen execution response was configured for this invocation.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpToolInfo))]
[JsonSerializable(typeof(McpPromptInfo))]
internal partial class BenchmarkJson : JsonSerializerContext;
