using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Copilot;

public sealed partial class CopilotAgentTaskRunner
{
    private async Task<List<CopilotToolContract>> DiscoverHostToolsAsync(CancellationToken ct)
    {
        var contracts = new List<CopilotToolContract>();
        foreach (var group in (allowedTools ?? []).Distinct().GroupBy(tool => tool.Server, StringComparer.Ordinal))
        {
            await using var client = await transport.GetClientAsync(group.Key, ct);
            var catalog = await client.ListToolsAsync(ct);
            foreach (var binding in group)
            {
                var tool = catalog.SingleOrDefault(tool => tool.Name == binding.Method)
                    ?? throw new InvalidOperationException("The host's configured MCP tool is unavailable.");
                contracts.Add(CopilotToolContract.From(binding, tool));
            }
        }
        return contracts;
    }

    private async Task<List<CopilotToolContract>> ResolveHostToolsAsync(AgentTaskDefinition task, CancellationToken ct)
    {
        var ids = task.Capabilities.Where(c => c.StartsWith("mcp_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return [];
        var tools = (await DiscoverHostToolsAsync(ct)).Where(tool => ids.Contains(tool.Id)).ToList();
        if (tools.Count != ids.Count || task.Budget.MaxActions is null)
            throw new InvalidOperationException("A selected MCP contract changed, is unavailable or lacks an action bound.");
        return tools;
    }

    private static JsonObject Manifest(IEnumerable<CopilotToolContract> tools) =>
        new(tools.Select(tool => new KeyValuePair<string, JsonNode?>(tool.Id, tool.Descriptor.DeepClone())));

    private async Task<AgentTaskResult> RunWithHostToolsAsync(AgentTaskContext context, List<CopilotToolContract> tools, CancellationToken ct)
    {
        var selected = tools.ToDictionary(tool => tool.Id, StringComparer.Ordinal);
        var scope = Guid.NewGuid().ToString("N");
        var arguments = Arguments(context);
        arguments["channel"] = scope;
        arguments["toolsJson"] = Manifest(tools).ToJsonString();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromMilliseconds(context.Task.Budget.MaxElapsedMilliseconds));
        var run = CallAsync("copilot_task_run", arguments, context, lifetime.Token);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? receipt = null;
        var evidence = new List<AgentTaskEvidence>();
        try
        {
            while (!run.IsCompleted)
            {
                var exchange = new JsonObject { ["runId"] = context.RunId, ["invocationId"] = context.InvocationId, ["channel"] = scope };
                if (receipt is not null) exchange["receiptJson"] = receipt.ToJsonString();
                var pending = CallAsync("copilot_task_exchange", exchange, context, lifetime.Token);
                if (await Task.WhenAny(run, pending) == run)
                {
                    await lifetime.CancelAsync();
                    try { await pending; } catch (OperationCanceledException) { }
                    break;
                }
                var action = await pending;
                receipt = null;
                if (action?["state"]?.ToString() == "waiting") continue;
                if (action?["state"]?.ToString() == "closed") break;
                if (action?["id"]?.GetValue<string>() is not { } id || !seen.Add(id) ||
                    seen.Count > context.Task.Budget.MaxActions || action["tool"]?.GetValue<string>() is not { } toolId ||
                    !selected.TryGetValue(toolId, out var tool) || action["request"] is not JsonObject input ||
                    PlanningContractValidation.ValidateInstance(input, tool.Descriptor["input_schema"]!).Count != 0)
                    throw new InvalidDataException("Invalid or duplicate host MCP action.");
                using var invocation = (transport as IMcpExecutionHooks)?.BeginCall(new(
                    new() { TenantId = context.TenantId, RunId = context.RunId, ExecutionId = context.ExecutionId ?? context.RunId,
                        AgentId = context.AgentId, AgentName = context.AgentName, StepId = context.InvocationId, StepType = "agent.run",
                        ServerName = tool.Binding.Server, MethodName = tool.Binding.Method, Kind = "tool" },
                    progress => context.Progress?.Invoke(new(progress.EventKind ?? "progress", progress.Message)),
                    signal => context.HumanInput?.Invoke(signal.Request, signal.Phase.ToString())));
                await using var client = await transport.GetClientAsync(tool.Binding.Server, lifetime.Token);
                if (client is ILiveMcpToolDiscoverySession discovered) await discovered.EnsureToolsDiscoveredAsync(lifetime.Token);
                var observed = await client.CallToolAsync(tool.Binding.Method, input, lifetime.Token);
                var invalid = !observed.IsError && tool.Descriptor["output_schema"] is JsonObject output &&
                    PlanningContractValidation.ValidateInstance(observed.Content, output).Count != 0;
                if (!observed.IsError && !invalid)
                    evidence.Add(new(id, "mcp.result", toolId, new() { ["request"] = input.DeepClone(), ["result"] = observed.Content?.DeepClone() }));
                receipt = new() { ["id"] = id, ["tool"] = toolId, ["error"] = observed.IsError || invalid, ["result"] = observed.Content?.DeepClone() };
            }
            var response = await run;
            var result = ParseResult(response);
            if (response?["recovered"]?.GetValue<bool>() == true && seen.Count == 0) return result;
            if (result.Status == "completed")
            {
                var recorded = result.Evidence.Where(e => e.Kind == "mcp.result").ToList();
                if (recorded.Count != evidence.Count || !recorded.Zip(evidence).All(pair => pair.First.Id == pair.Second.Id &&
                    pair.First.Subject == pair.Second.Subject && JsonNode.DeepEquals(pair.First.Facts, pair.Second.Facts)))
                    throw new InvalidDataException("The managed receipt differs from the host's observed MCP calls.");
            }
            return result;
        }
        finally
        {
            await lifetime.CancelAsync();
            // The primary dispatch/exchange failure is retained by its owner.
            try { await run; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}
