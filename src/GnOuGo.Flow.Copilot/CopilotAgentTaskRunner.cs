using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Copilot;

/// <summary>Transport adapter only. The MCP host owns the existing managed Copilot session implementation.</summary>
public sealed class CopilotAgentTaskRunner(IMcpClientFactory transport, string serverName) : IAgentTaskRunner
{
    public string Description => "Adaptive project editing and testing through managed Copilot with bounded permissions and verified command/file evidence.";

    public async Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct)
    {
        var response = await CallAsync("copilot_task_contract", new JsonObject(), null, ct);
        if (response?["schemaVersion"]?.GetValue<int>() != 9)
            throw new InvalidOperationException("The Copilot server does not implement the schema-9 bounded task protocol.");
        return JsonSerializer.Deserialize(response["contract"], AgentTaskJsonContext.Default.AgentTaskRunnerContract)
            ?? throw new InvalidOperationException("The Copilot server did not declare its bounded task contract.");
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct)
    {
        AgentTaskContracts.Parse(JsonSerializer.SerializeToNode(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition));
        var response = await CallAsync("copilot_task_validate", Arguments(context), context, ct);
        if (response?["errors"] is not JsonArray errors) throw new InvalidOperationException("Invalid Copilot scope validation response.");
        return errors.Select(e => e?.GetValue<string>() ?? "Invalid scope finding.").ToArray();
    }

    public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) => ResultAsync("copilot_task_run", context, ct);
    public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => ResultAsync("copilot_task_inspect", context, ct);

    private async Task<AgentTaskResult> ResultAsync(string method, AgentTaskContext context, CancellationToken ct)
    {
        var response = await CallAsync(method, Arguments(context), context, ct);
        if (response?["schemaVersion"]?.GetValue<int>() != 9 || response["result"] is null)
            throw new InvalidOperationException("Invalid Copilot receipt protocol. Regenerate and approve the task.");
        return JsonSerializer.Deserialize(response["result"], AgentTaskJsonContext.Default.AgentTaskResult)
            ?? throw new InvalidOperationException("Invalid Copilot task receipt.");
    }

    private static JsonObject Arguments(AgentTaskContext context) => new()
    { ["contextJson"] = JsonSerializer.Serialize(context, AgentTaskJsonContext.Default.AgentTaskContext) };

    private async Task<JsonNode?> CallAsync(string method, JsonObject arguments, AgentTaskContext? context, CancellationToken ct)
    {
        using var call = (transport as IMcpExecutionHooks)?.BeginCall(new(
            new() { TenantId = context?.TenantId, RunId = context?.RunId, ExecutionId = context?.ExecutionId ?? context?.RunId, AgentId = context?.AgentId, AgentName = context?.AgentName,
                StepId = context?.InvocationId, StepType = "agent.run", ServerName = serverName, MethodName = method, Kind = "tool" },
            progress => context?.Progress?.Invoke(new(progress.EventKind ?? "progress", progress.Message)),
            signal => context?.HumanInput?.Invoke(signal.Request, signal.Phase.ToString())));
        await using var session = await transport.GetClientAsync(serverName, ct);
        if (session is ILiveMcpToolDiscoverySession discovery) await discovery.EnsureToolsDiscoveredAsync(ct);
        var result = await session.CallToolAsync(method, arguments, ct);
        if (result.IsError) throw new InvalidOperationException("The Copilot task operation failed. Inspect its durable receipt before retrying execution.");
        return result.Content;
    }
}
