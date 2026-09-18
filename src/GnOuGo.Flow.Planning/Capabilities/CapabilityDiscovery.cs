using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityDiscovery
{
    internal static async Task<PlanningCatalog> DiscoverAsync(WorkflowEngine engine, PlanningRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var catalog = new PlanningCatalog { Policy = request.Policy };
        foreach (var (type, contract) in engine.Registry.GetContracts().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (type is "workflow.plan" or "workflow.execute" || request.Policy.AllowedStepTypes.Count > 0 && !request.Policy.AllowedStepTypes.Contains(type)) continue;
            catalog.AllowedStepTypes.Add(type);
            catalog.StepContracts[type] = new JsonObject { ["input"] = contract.InputSchema.DeepClone(), ["output"] = contract.OutputSchema.DeepClone() };
        }
        if (engine.McpClientFactory is not { } factory || !catalog.AllowedStepTypes.Contains("mcp.call")) return catalog;
        foreach (var server in factory.ServerMetadata.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(server.DiscoveryTimeoutSeconds ?? 30));
            await using var session = await factory.GetClientAsync(server.Name, deadline.Token);
            foreach (var tool in await session.ListToolsAsync(deadline.Token))
            {
                if (string.Equals(tool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>(), "management_only", StringComparison.OrdinalIgnoreCase)) continue;
                if (tool.ArtifactContract?.Errors.Count > 0) throw new InvalidOperationException("Invalid declared artifact contract: " + server.Name + "/" + tool.Name);
                var capability = Tool(server.Name, tool);
                if (PlanningContractValidation.ValidateSchema(capability.InputSchema).Count != 0 || capability.OutputSchema.Count > 0 && PlanningContractValidation.ValidateSchema(capability.OutputSchema).Count != 0)
                    throw new InvalidOperationException("Invalid declared capability schema: " + capability.Id);
                if (!request.Policy.DeniedCapabilityIds.Contains(capability.Id)) catalog.Capabilities.Add(capability);
            }
            foreach (var prompt in await session.ListPromptsAsync(deadline.Token))
            {
                var capability = new PlanningCapability
                {
                    Id = Identity(server.Name, "prompt", prompt.Name), StepType = "mcp.call", Server = server.Name, Method = prompt.Name, Kind = "prompt",
                    Description = prompt.Description ?? "", EffectKind = "read", InputSchema = new()
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JsonObject((prompt.Arguments ?? []).Select(a => new KeyValuePair<string, JsonNode?>(a.Name, new JsonObject { ["type"] = "string" }))),
                        ["required"] = new JsonArray((prompt.Arguments ?? []).Where(a => a.Required).Select(a => (JsonNode?)JsonValue.Create(a.Name)).ToArray())
                    }
                };
                if (!request.Policy.DeniedCapabilityIds.Contains(capability.Id)) catalog.Capabilities.Add(capability);
            }
        }
        if (catalog.Capabilities.Select(c => c.Id).Distinct().Count() != catalog.Capabilities.Count) throw new InvalidOperationException("Duplicate capability identities.");
        catalog.Capabilities = catalog.Capabilities.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return catalog;
    }
    internal static string Identity(string server, string kind, string method) => "cap_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(new[] { server, kind, method }, PlanningJsonContext.Default.StringArray))[..24];
    internal static PlanningCapability Tool(string server, McpToolInfo tool) => new()
    {
        Id = Identity(server, "tool", tool.Name), StepType = "mcp.call", Server = server, Kind = "tool", Method = tool.Name,
        Description = tool.Description ?? "", InputSchema = tool.InputSchema?.DeepClone() as JsonObject ?? new() { ["type"] = "object" },
        OutputSchema = McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone() as JsonObject ?? new(),
        ArtifactContract = tool.ArtifactContract?.Contract, Metadata = tool.Meta?.DeepClone(), ExampleResponse = tool.ExampleResponse?.DeepClone(),
        EffectKind = tool.EffectKind is "read" or "write" or "execute" or "lifecycle" or "none" ? tool.EffectKind : "unknown"
    };
}
