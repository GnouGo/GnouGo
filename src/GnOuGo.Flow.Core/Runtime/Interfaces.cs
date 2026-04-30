using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Main workflow runtime interface.
/// </summary>
public interface IWorkflowRuntime
{
    Task<RunResult> ExecuteAsync(CompiledWorkflow workflow, JsonNode? inputs, CancellationToken ct);
}

/// <summary>
/// Interface for LLM client.
/// </summary>
public interface ILLMClient
{
    Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct);
}

/// <summary>
/// LLM request.
/// </summary>
public sealed class LLMRequest
{
    public string? Provider { get; set; }
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public double? Temperature { get; set; }
    public JsonNode? StructuredOutputSchema { get; set; }
    public bool? StructuredOutputStrict { get; set; }

    /// <summary>
    /// Optional thinking / reasoning effort level for models that support it
    /// (OpenAI o-series / gpt-5, Anthropic claude-sonnet via Copilot, Ollama "think").
    /// Accepted values: "minimal" | "low" | "medium" | "high" | "max" | "auto" | null.
    /// "auto" or null means "let the provider decide" (no field emitted).
    /// "max" is mapped to the highest provider-supported level (e.g. "high" for OpenAI).
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Optional list of tool definitions to make available to the LLM.
    /// Each tool is a JsonObject with at least { name, description, inputSchema }.
    /// Populated automatically by mcp.call when the LLM needs to select and invoke tools.
    /// </summary>
    public List<LLMTool>? Tools { get; set; }
}

/// <summary>
/// Runtime defaults applied to LLM-capable steps when a workflow omits
/// <c>provider</c> and/or <c>model</c> in step input.
/// </summary>
public sealed class LlmRuntimeDefaults
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// A tool definition that can be provided to an LLM for function calling.
/// </summary>
public sealed class LLMTool
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? InputSchema { get; set; }
}

/// <summary>
/// LLM response.
/// </summary>
public sealed class LLMResponse
{
    public string Text { get; set; } = "";
    public JsonNode? Json { get; set; }
    public JsonNode? Usage { get; set; }
    public JsonNode? Raw { get; set; }

    /// <summary>
    /// If the LLM requested a tool call, this contains the tool call details.
    /// </summary>
    public List<LLMToolCall>? ToolCalls { get; set; }
}

/// <summary>
/// Represents a tool call requested by the LLM.
/// </summary>
public sealed class LLMToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonNode? Arguments { get; set; }
}

/// <summary>
/// Interface for fetching remote workflows.
/// </summary>
public interface IWorkflowFetcher
{
    Task<string> FetchAsync(string url, string? integrity, CancellationToken ct);
}

/// <summary>
/// Interface for template engine abstraction.
/// </summary>
public interface ITemplateEngine
{
    Task<TemplateResult> RenderAsync(string template, JsonNode? data, bool strict, string mode, CancellationToken ct);
}

/// <summary>
/// Template rendering result.
/// </summary>
public sealed class TemplateResult
{
    public string? Text { get; set; }
    public JsonNode? Json { get; set; }
    public JsonNode? Meta { get; set; }
}

/// <summary>
/// Fetch policy for remote workflows.
/// </summary>
public sealed class FetchPolicy
{
    public List<string> AllowedHostnames { get; set; } = new();
    public bool RequireHttps { get; set; } = true;
    public int MaxSizeBytes { get; set; } = 1_048_576; // 1MB
    public int TimeoutMs { get; set; } = 30_000;
    public int MaxRedirects { get; set; } = 5;
    public bool RequireIntegrity { get; set; } = false;
}


// ─── MCP (Model Context Protocol) ────────────────────────────────────

/// <summary>
/// Static metadata for a configured MCP server.
/// Used by planning/prompt-building code to describe available servers
/// without opening a live connection.
/// </summary>
public sealed class McpServerMetadata
{
    /// <summary>Logical server name (matches configuration).</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-friendly description of what the server is for.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional per-server timeout, in seconds, used by workflow planning discovery.
    /// Slow stdio servers can require more time during cold start.
    /// </summary>
    public int? DiscoveryTimeoutSeconds { get; set; }
}

/// <summary>
/// Factory for obtaining MCP clients by server name.
/// Implementations manage connection lifecycle (stdio, SSE, etc.).
/// </summary>
public interface IMcpClientFactory
{
    /// <summary>
    /// Get or create an MCP client connected to the named server.
    /// </summary>
    /// <param name="serverName">Logical server name (matches configuration).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected MCP client.</returns>
    Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// Returns static metadata for configured servers.
    /// Used by workflow planners to choose the right server name.
    /// </summary>
    IReadOnlyList<McpServerMetadata> ServerMetadata { get; }
}

/// <summary>
/// Correlation metadata propagated from workflow MCP steps to MCP transports.
/// HTTP transports send it as headers; stdio transports expose it as environment
/// variables when the MCP server process is started.
/// </summary>
public sealed record McpCorrelationContext
{
    public string? CorrelationId { get; init; }
    public string? RunId { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? TraceParent { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public string? ServerName { get; init; }
    public string? MethodName { get; init; }
    public string? Kind { get; init; }
}

/// <summary>
/// Abstraction over an MCP session (client connected to a server).
/// Wraps the ModelContextProtocol.Client.IMcpClient for testability.
/// </summary>
public interface IMcpSession : IAsyncDisposable
{
    /// <summary>
    /// List tools exposed by this MCP server.
    /// </summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct);

    /// <summary>
    /// List resources exposed by this MCP server.
    /// </summary>
    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct);

    /// <summary>
    /// List prompts exposed by this MCP server.
    /// </summary>
    Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct);

    /// <summary>
    /// Call a tool on this MCP server.
    /// </summary>
    Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct);

    /// <summary>
    /// Get a prompt from this MCP server (resolve prompt template with arguments).
    /// </summary>
    Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct);

    /// <summary>
    /// Server name.
    /// </summary>
    string ServerName { get; }
}

/// <summary>
/// Describes an MCP tool.
/// </summary>
public sealed class McpToolInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? InputSchema { get; set; }
}

/// <summary>
/// Describes an MCP resource.
/// </summary>
public sealed class McpResourceInfo
{
    public string Uri { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>
/// Describes an MCP prompt.
/// </summary>
public sealed class McpPromptInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<McpPromptArgument>? Arguments { get; set; }
}

/// <summary>
/// Describes an argument for an MCP prompt.
/// </summary>
public sealed class McpPromptArgument
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// Result of calling an MCP tool.
/// </summary>
public sealed class McpCallResult
{
    public bool IsError { get; set; }
    public JsonNode? Content { get; set; }

    /// <summary>
    /// Optional LLM usage metadata (e.g., prompt_tokens, completion_tokens).
    /// Returned by MCP servers that proxy LLM calls.
    /// </summary>
    public JsonObject? Usage { get; set; }

    /// <summary>
    /// Optional model name used by the MCP server for this call.
    /// </summary>
    public string? Model { get; set; }
}

/// <summary>
/// Result of getting an MCP prompt (resolved messages).
/// </summary>
public sealed class McpGetPromptResult
{
    public string? Description { get; set; }
    public List<McpPromptMessage> Messages { get; set; } = new();

    /// <summary>
    /// Optional LLM usage metadata (e.g., prompt_tokens, completion_tokens).
    /// Returned by MCP servers that proxy LLM calls.
    /// </summary>
    public JsonObject? Usage { get; set; }

    /// <summary>
    /// Optional model name used by the MCP server for this call.
    /// </summary>
    public string? Model { get; set; }
}

/// <summary>
/// A message returned by an MCP prompt.
/// </summary>
public sealed class McpPromptMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}
