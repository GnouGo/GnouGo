using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityScopeTests
{
    [Theory]
    [InlineData("opaque policy description")]
    [InlineData("renamed workflow guarantee")]
    public async Task LocalOperationsAndStructuralPoliciesDoNotRequestPhysicalCapabilitySelection(string description)
    {
        var inventory = new CapabilityInventory(true, [new("local", "Pure transformation", true, "local_processing", "none")],
            [new("policy", description, true, "workflow_policy")], []);
        var client = new UnexpectedClient();
        var selected = await CapabilityDiscovery.SelectPhysicalCapabilityCandidatesAsync(client, inventory, [new() { Name = "opaque-provider", Discovered = true, Tools = [new() { Name = "unrelated-tool", Description = "Unrelated declared effect", InputSchema = new System.Text.Json.Nodes.JsonObject() { ["type"] = "object" } }] }], "intent", "context", null, "test", "low", null!, null!, TestContext.Current.CancellationToken);
        Assert.Empty(selected); Assert.Equal(0, client.Calls);
        Assert.Single(inventory.Constraints);
    }
    private sealed class UnexpectedClient : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No physical capability request is needed for this validated inventory."); }
    }
}
