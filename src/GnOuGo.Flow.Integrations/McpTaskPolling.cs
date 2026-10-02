using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace GnOuGo.Flow.Integrations;

/// <summary>Capability-driven MCP task transport; callers still receive one ordinary tool result.</summary>
internal static class McpTaskPolling
{
    internal static bool Supported(McpClient client) => client.ServerCapabilities.Extensions?.ContainsKey("io.modelcontextprotocol/tasks") == true &&
        string.CompareOrdinal(client.NegotiatedProtocolVersion, "2026-07-28") >= 0;

    internal static async Task<CallToolResult> CallAsync(McpClient client, string name, JsonNode? arguments, JsonObject? meta, CancellationToken ct)
    {
        var created = await client.CallToolAsTaskAsync(new()
        {
            Name = name, Meta = meta?.DeepClone().AsObject(),
            Arguments = (arguments as JsonObject)?.ToDictionary(p => p.Key, p => Element(p.Value), StringComparer.Ordinal)
        }, ct);
        if (!created.IsTask) return created.Result!;
        var task = created.TaskCreated!;
        var answered = new Dictionary<string, InputResponse>(StringComparer.Ordinal);
        var interval = task.PollIntervalMs ?? 1000;
        var stuck = 0;
        try
        {
            while (true)
            {
                var result = await client.GetTaskAsync(new GetTaskRequestParams { TaskId = task.TaskId, Meta = meta?.DeepClone().AsObject() }, ct);
                interval = Math.Clamp(result.PollIntervalMs ?? interval, 10, 30_000);
                switch (result)
                {
                    case CompletedTaskResult complete:
                        return complete.Result.Deserialize((JsonTypeInfo<CallToolResult>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))
                            ?? throw new McpException("The task completion receipt is invalid.");
                    case FailedTaskResult:
                        throw new McpException("The remote task failed. Inspect its retained evidence before retrying.");
                    case CancelledTaskResult:
                        throw new OperationCanceledException("The remote task was cancelled.", ct);
                    case InputRequiredTaskResult input:
                        var pending = input.InputRequests ?? new Dictionary<string, InputRequest>();
                        var fresh = pending.Where(p => !answered.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                        if (fresh.Count > 0)
                        {
                            var responses = await client.ResolveInputRequestsAsync(fresh, ct);
                            foreach (var response in responses) answered.Add(response.Key, response.Value);
                            stuck = 0;
                        }
                        else if (++stuck >= 60) throw new McpException("The task stopped making progress after its input requests were answered.");
                        // Re-send retained answers if an acknowledgement was lost. Never
                        // ask the user twice for the same task-local input identity.
                        var updates = answered.Where(p => pending.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                        if (updates.Count > 0)
                            await client.UpdateTaskAsync(new() { TaskId = task.TaskId, Meta = meta?.DeepClone().AsObject(), InputResponses = updates }, ct);
                        break;
                    case WorkingTaskResult: stuck = 0; break;
                    default: throw new McpException("The task returned an unsupported status.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(interval), ct);
            }
        }
        catch
        {
            // Tasks use tasks/cancel, not cancellation of the original tools/call.
            // Acknowledgement is not evidence that external execution completed.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await client.CancelTaskAsync(new CancelTaskRequestParams { TaskId = task.TaskId, Meta = meta?.DeepClone().AsObject() }, cancellation.Token); }
            catch (Exception ex) when (ex is McpException or IOException or OperationCanceledException) { }
            throw;
        }
    }

    private static JsonElement Element(JsonNode? value)
    {
        using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }
}
