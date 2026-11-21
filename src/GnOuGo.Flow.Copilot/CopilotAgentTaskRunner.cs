using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Copilot;

/// <summary>Transport adapter only. The MCP host owns the existing managed Copilot session implementation.</summary>
public sealed partial class CopilotAgentTaskRunner(IMcpClientFactory transport, string serverName, IReadOnlyList<CopilotToolBinding>? allowedTools = null) : IAgentTaskRunner
{
    private sealed record ValidatedScope(string Definition, List<CopilotToolContract> Tools);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentTaskContext, ValidatedScope> _validated = new();

    public bool SupportsActionLimit => true;
    public string Description => "Execute bounded adaptive tasks with managed Copilot, approved tools and independently verifiable observations.";

    public async Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct)
    {
        var arguments = new JsonObject();
        if (allowedTools is { Count: > 0 }) arguments["hostToolsOnly"] = true;
        var response = await CallAsync("copilot_task_contract", arguments, null, ct);
        if (response?["schemaVersion"]?.GetValue<int>() != 9)
            throw new InvalidOperationException("The Copilot server does not implement the schema-9 bounded task protocol.");
        var contract = JsonSerializer.Deserialize(response["contract"], AgentTaskJsonContext.Default.AgentTaskRunnerContract)
            ?? throw new InvalidOperationException("The Copilot server did not declare its bounded task contract.");
        if (allowedTools is not { Count: > 0 }) return contract;
        if (response["hostToolProtocol"]?.GetValue<int>() != 1)
            throw new InvalidOperationException("The configured Copilot MCP cannot dispatch host-scoped tools.");
        var schema = contract.InputSchema;
        var options = schema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray();
        options.Clear();
        var descriptors = new JsonObject();
        foreach (var tool in await DiscoverHostToolsAsync(ct))
        {
            options.Add((JsonNode?)JsonValue.Create(tool.Id));
            descriptors[tool.Id] = tool.Descriptor;
        }
        schema["properties"]!["verification"]!["items"]!["properties"]!["kind"]!["enum"] = new JsonArray("mcp.result");
        return new(contract.Description + " Selected MCP capabilities are named by their pinned live contracts. " +
            "Use mcp.result with the exact capability as subject and facts {request, result}. A finite max_actions is required. " +
            descriptors.ToJsonString(), schema);
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct)
    {
        AgentTaskContracts.Parse(JsonSerializer.SerializeToNode(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition));
        var arguments = Arguments(context);
        var selected = await ResolveHostToolsAsync(context.Task, ct);
        if (selected.Count > 0) arguments["toolsJson"] = Manifest(selected).ToJsonString();
        var response = await CallAsync("copilot_task_validate", arguments, context, ct);
        if (response?["errors"] is not JsonArray errors) throw new InvalidOperationException("Invalid Copilot scope validation response.");
        if (errors.Count == 0 && selected.Count != 0)
            _validated.AddOrUpdate(context, new(JsonSerializer.Serialize(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition), selected));
        return errors.Select(e => e?.GetValue<string>() ?? "Invalid scope finding.").ToArray();
    }

    public async Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct)
    {
        List<CopilotToolContract> selected;
        if (_validated.TryGetValue(context, out var approved) && _validated.Remove(context) &&
            approved.Definition == JsonSerializer.Serialize(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition))
            selected = approved.Tools;
        else selected = await ResolveHostToolsAsync(context.Task, ct);
        return selected.Count == 0 ? await ResultAsync("copilot_task_run", context, ct)
            : await RunWithHostToolsAsync(context, selected, ct);
    }
    public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => ResultAsync("copilot_task_inspect", context, ct);

    private async Task<AgentTaskResult> ResultAsync(string method, AgentTaskContext context, CancellationToken ct)
    {
        var response = await CallAsync(method, Arguments(context), context, ct);
        return ParseResult(response);
    }

    private static AgentTaskResult ParseResult(JsonNode? response)
    {
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
