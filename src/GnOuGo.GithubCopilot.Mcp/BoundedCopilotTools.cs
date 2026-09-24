using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

[McpServerToolType]
internal sealed class BoundedCopilotTools(BoundedCopilotTasks tasks, McpCopilotHumanInputProvider human,
    CodeMcpTraceContextAccessor trace)
{
    private const string Metadata = "{\"management\":{\"visibility\":\"management_only\"}}";
    [McpServerTool(Name = "copilot_task_contract", UseStructuredContent = true), Description("Exact schema-9 bounded task contract for the Flow.Copilot adapter.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public JsonObject Contract() => tasks.Contract();

    [McpServerTool(Name = "copilot_task_validate", UseStructuredContent = true), Description("Validate an approved task scope without dispatching adaptive work.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public JsonObject Validate(string contextJson)
        => new() { ["errors"] = new JsonArray(tasks.Validate(Parse(contextJson)).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };

    [McpServerTool(Name = "copilot_task_run", UseStructuredContent = true), Description("Execute one approved bounded task using its durable tenant/run/invocation identity. Existing identities are never redispatched.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> RunAsync(RequestContext<CallToolRequestParams> request, string contextJson, CancellationToken cancellationToken)
    {
        using var scope = human.Push(request.Server, cancellationToken);
        return JsonSerializer.SerializeToNode(await tasks.RunAsync(Parse(contextJson), cancellationToken), AgentTaskJsonContext.Default.AgentTaskResult)!.AsObject();
    }

    [McpServerTool(Name = "copilot_task_inspect", UseStructuredContent = true), Description("Read the encrypted receipt for an interrupted task without restoring a Copilot session or repeating its effects.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> InspectAsync(string contextJson, CancellationToken cancellationToken)
        => JsonSerializer.SerializeToNode(await tasks.InspectAsync(Parse(contextJson), cancellationToken), AgentTaskJsonContext.Default.AgentTaskResult)!.AsObject();

    private AgentTaskContext Parse(string json)
    {
        var context = JsonSerializer.Deserialize(json, AgentTaskJsonContext.Default.AgentTaskContext) ?? throw new McpException("A task context is required.");
        if (string.IsNullOrWhiteSpace(context.TenantId) || string.IsNullOrWhiteSpace(context.RunId) || string.IsNullOrWhiteSpace(context.InvocationId) ||
            trace.Current?.TenantId is { Length: > 0 } tenant && tenant != context.TenantId)
            throw new McpException("The task identity does not match the transport tenant.");
        return context;
    }
}
