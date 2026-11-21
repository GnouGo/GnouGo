using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>One managed SDK task and its calling workflow share this short-lived channel. An action is delivered at most once.</summary>
internal sealed class CopilotTaskToolExchange : IDisposable
{
    private sealed record ActionRequest(string Id, string Tool, JsonObject Input, CancellationToken Caller, TaskCompletionSource<JsonObject> Receipt);
    private readonly Channel<ActionRequest> _requests = Channel.CreateBounded<ActionRequest>(1);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private ActionRequest? _claimed;
    private readonly List<AgentTaskEvidence> _evidence = [];
    private bool _uncertain;
    internal bool Uncertain { get { lock (_gate) return _uncertain || _claimed is not null; } }
    internal List<AgentTaskEvidence> Evidence { get { lock (_gate) return [.. _evidence]; } }
    internal bool Recovered { get; set; }
    internal JsonObject Descriptors { get; }

    internal static JsonObject ParseManifest(AgentTaskContext context, string? toolsJson)
    {
        var manifest = toolsJson is null ? new JsonObject() : JsonNode.Parse(toolsJson)!.AsObject();
        var selected = context.Task.Capabilities.Where(id => id.StartsWith("mcp_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!selected.SetEquals(manifest.Select(p => p.Key)) || selected.Count != 0 && context.Task.Budget.MaxActions is null ||
            manifest.Any(p => p.Value is not JsonObject descriptor || p.Key != AgentTaskContracts.McpToolId(descriptor) ||
                descriptor["input_schema"] is not JsonObject schema || PlanningContractValidation.ValidateSchema(schema).Count != 0))
            throw new InvalidDataException("The selected MCP contracts differ from the approved bounded task.");
        return manifest;
    }

    internal CopilotTaskToolExchange(JsonObject descriptors) => Descriptors = descriptors;

    internal List<CopilotHostTool> BuildTools() => Descriptors.Select(entry =>
    {
        var contract = entry.Value!.AsObject();
        using var schema = JsonDocument.Parse(contract["input_schema"]!.ToJsonString());
        return new CopilotHostTool(entry.Key, contract["server"] + "/" + contract["method"] + ". " + contract["description"],
            schema.RootElement.Clone(), (input, ct) => InvokeAsync(entry.Key, input, ct));
    }).ToList();

    private async Task<JsonElement> InvokeAsync(string tool, JsonElement input, CancellationToken ct)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _serial.WaitAsync(token.Token);
        try
        {
            var args = JsonNode.Parse(input.GetRawText())!.AsObject();
            if (PlanningContractValidation.ValidateInstance(args, Descriptors[tool]!["input_schema"]!).Count != 0)
                throw new InvalidDataException("The SDK proposed arguments outside the configured tool schema.");
            var action = new ActionRequest(Guid.NewGuid().ToString("N"), tool, args, token.Token, new(TaskCreationOptions.RunContinuationsAsynchronously));
            await _requests.Writer.WriteAsync(action, token.Token);
            var receipt = await action.Receipt.Task.WaitAsync(token.Token);
            if (receipt["error"]!.GetValue<bool>()) throw new InvalidOperationException("The MCP tool returned an error or an invalid result.");
            using var value = JsonDocument.Parse(receipt["result"]?.ToJsonString() ?? "null");
            return value.RootElement.Clone();
        }
        finally { _serial.Release(); }
    }

    internal async Task<JsonObject> ExchangeAsync(JsonObject? receipt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (receipt is not null)
            {
                if (_claimed is not { } action || receipt["id"]?.ToString() != action.Id || receipt["tool"]?.ToString() != action.Tool ||
                    receipt["error"] is not JsonValue error || !error.TryGetValue<bool>(out var failed) || !receipt.ContainsKey("result"))
                    throw new InvalidDataException("The host receipt does not match the outstanding MCP action.");
                if (!failed)
                    _evidence.Add(new(action.Id, "mcp.result", action.Tool, new JsonObject { ["request"] = action.Input, ["result"] = receipt["result"]?.DeepClone() }));
                _claimed = null;
                action.Receipt.SetResult(receipt);
            }
            else if (_claimed is not null)
                throw new InvalidOperationException("The issued action requires its original receipt.");
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        try
        {
            while (true)
            {
                var action = await _requests.Reader.ReadAsync(wait.Token);
                lock (_gate)
                {
                    if (_closed.IsCancellationRequested) return new() { ["state"] = "closed" };
                    if (_uncertain || _claimed is not null)
                        throw new InvalidOperationException("The MCP action scope is unresolved.");
                    if (action.Caller.IsCancellationRequested) continue;
                    _claimed = action;
                    return new() { ["state"] = "action", ["id"] = action.Id, ["tool"] = action.Tool, ["request"] = action.Input.DeepClone() };
                }
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { return new() { ["state"] = "closed" }; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _uncertain |= _claimed is not null;
            _closed.Cancel();
        }
    }
}
