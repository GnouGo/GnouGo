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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string Run, string Invocation, Guid Channel), CopilotTaskToolExchange> Exchanges = new();

    private const string Metadata = "{\"management\":{\"visibility\":\"management_only\"}}";
    [McpServerTool(Name = "copilot_task_contract", UseStructuredContent = true), Description("Exact schema-9 bounded task contract for the Flow.Copilot adapter.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public Task<JsonObject> ContractAsync(CancellationToken cancellationToken, bool hostToolsOnly = false) => tasks.ContractAsync(cancellationToken, hostToolsOnly);

    [McpServerTool(Name = "copilot_task_validate", UseStructuredContent = true), Description("Validate an approved task scope without dispatching adaptive work.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> ValidateAsync(string contextJson, CancellationToken cancellationToken, string? toolsJson = null)
    {
        var context = Parse(contextJson);
        var manifest = CopilotTaskToolExchange.ParseManifest(context, toolsJson);
        return new() { ["errors"] = new JsonArray((await tasks.ValidateAsync(context, cancellationToken, manifest)).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };
    }

    [McpServerTool(Name = "copilot_task_run", UseStructuredContent = true), Description("Execute one approved bounded task using its durable tenant/run/invocation identity. Existing identities are never redispatched.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> RunAsync(RequestContext<CallToolRequestParams> request, string contextJson, CancellationToken cancellationToken, string? toolsJson = null, string? channel = null)
    {
        using var scope = human.Push(request.Server, cancellationToken);
        var context = Parse(contextJson);
        var manifest = CopilotTaskToolExchange.ParseManifest(context, toolsJson);
        if (manifest.Count == 0 && channel is null) return Receipt(await tasks.RunAsync(context, cancellationToken));
        if (trace.Current?.RunId != context.RunId || trace.Current.StepId != context.InvocationId)
            throw new McpException("The requested task channel differs from the transport invocation.");
        var key = ChannelKey(context.TenantId, context.RunId, context.InvocationId, channel);
        using var exchange = new CopilotTaskToolExchange(manifest);
        if (!Exchanges.TryAdd(key, exchange)) throw new McpException("This task channel is already owned.");
        try
        {
            var receipt = Receipt(await tasks.RunAsync(context, cancellationToken, exchange));
            receipt["recovered"] = exchange.Recovered;
            return receipt;
        }
        finally { Exchanges.TryRemove(key, out _); }
    }

    private static (string, string, string, Guid) ChannelKey(string tenant, string run, string invocation, string? channel)
    {
        if (!Guid.TryParseExact(channel, "N", out var id)) throw new McpException("An invocation channel is required.");
        return (tenant, run, invocation, id);
    }

    [McpServerTool(Name = "copilot_task_exchange", UseStructuredContent = true), Description("Deliver the original host tool receipt and wait for the managed session's next authorized MCP action.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> ExchangeAsync(string runId, string invocationId, string channel, CancellationToken cancellationToken, string? receiptJson = null)
    {
        var owner = trace.Current;
        if (owner?.TenantId is not { Length: > 0 } tenant || owner.RunId != runId || owner.StepId != invocationId)
            throw new McpException("The task channel does not match the transport invocation.");
        if (!Exchanges.TryGetValue(ChannelKey(tenant, runId, invocationId, channel), out var exchange))
        {
            if (receiptJson is not null) throw new McpException("The original task channel is unavailable.");
            await Task.Delay(50, cancellationToken);
            return new() { ["state"] = "waiting" };
        }
        return await exchange.ExchangeAsync(receiptJson is null ? null : JsonNode.Parse(receiptJson)!.AsObject(), cancellationToken);
    }

    [McpServerTool(Name = "copilot_task_inspect", UseStructuredContent = true), Description("Read the encrypted receipt for an interrupted task without restoring a Copilot session or repeating its effects.")]
    [McpMeta("gnougo", JsonValue = Metadata)]
    public async Task<JsonObject> InspectAsync(string contextJson, CancellationToken cancellationToken)
        => Receipt(await tasks.InspectAsync(Parse(contextJson), cancellationToken));

    // A successful receipt lookup can describe a failed task. Keep task status out
    // of the transport error envelope so recovery can consume that terminal result.
    private static JsonObject Receipt(AgentTaskResult result) => new()
    { ["schemaVersion"] = 9, ["result"] = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult) };

    private AgentTaskContext Parse(string json)
    {
        var context = JsonSerializer.Deserialize(json, AgentTaskJsonContext.Default.AgentTaskContext) ?? throw new McpException("A task context is required.");
        if (string.IsNullOrWhiteSpace(context.TenantId) || string.IsNullOrWhiteSpace(context.RunId) || string.IsNullOrWhiteSpace(context.InvocationId) ||
            (string.IsNullOrWhiteSpace(trace.Current?.TenantId) || trace.Current.TenantId != context.TenantId))
            throw new McpException("The task identity does not match the transport tenant.");
        return context;
    }
}
