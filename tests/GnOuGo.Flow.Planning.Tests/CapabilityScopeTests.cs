using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityScopeTests
{
    [Theory]
    [InlineData("opaque policy description", "workflow_policy")]
    [InlineData("renamed workflow guarantee", "workflow_policy")]
    [InlineData("unconditional denied capability", "exact_denial")]
    public async Task LocalOperationsAndStructuralPoliciesDoNotRequestPhysicalCapabilitySelection(string description, string enforcement)
    {
        var inventory = new CapabilityInventory(true, [new("local", "Pure transformation", true, "local_processing", "none")],
            [new("policy", description, true, enforcement)], []);
        var client = new UnexpectedClient();
        var selected = await CapabilityDiscovery.SelectPhysicalCapabilityCandidatesAsync(client, inventory, [new() { Name = "opaque-provider", Discovered = true, Tools = [new() { Name = "unrelated-tool", Description = "Unrelated declared effect", InputSchema = new System.Text.Json.Nodes.JsonObject() { ["type"] = "object" } }] }], null, "test", "low", null!, null!, TestContext.Current.CancellationToken);
        Assert.Empty(selected); Assert.Equal(0, client.Calls);
        Assert.Single(inventory.Constraints);
    }
    [Fact]
    public void EmptyPhysicalAuthorityEnforcesDenialsWithoutInventedCatalogIds()
    {
        var inventory = new CapabilityInventory(true, [new("local", "Pure transformation", true, "local_processing", "none")],
            [new("denial", "Explicitly prohibited capability", true, "exact_denial")], []);
        var catalog = CapabilityCatalogBuilder.BuildSchemaAwareCapabilityCatalog([], new HashSet<string>(["set"], StringComparer.Ordinal), []);
        var schema = CapabilityMatchAssessment.BuildTypedCapabilityMatchingSchema(inventory, catalog);
        var response = JsonNode.Parse("""{"operation_matches":{"local":{"status":"local","catalog_ids":[],"candidate_catalog_ids":[],"decision_operation_id":"","conditional_mode":"","reason":"Local"}},"constraint_matches":{"denial":{"status":"enforced","denied_catalog_ids":[],"candidate_catalog_ids":[],"reason":"Empty physical allowlist"}}}""")!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        Assert.True(CapabilityMatchAssessment.ParseCapabilityMatchingEvaluation(response, inventory, catalog).ContractValid);
        response["constraint_matches"]!["denial"]!["denied_catalog_ids"] = new JsonArray("invented");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        response["constraint_matches"]!["denial"]!["denied_catalog_ids"] = new JsonArray();
        var physical = new CapabilityCatalog([new("physical", "mcp", "provider", "tool", "method", "Declared effect", [], "", [], [], null, null)], "");
        Assert.False(CapabilityMatchAssessment.ParseCapabilityMatchingEvaluation(response, inventory, physical).ContractValid);
    }

    [Fact]
    public void MatchingSharesRepeatedLockedDomainsWithoutWideningThem()
    {
        var inventory = new CapabilityInventory(true, [new("local", "Pure transformation", true, "local_processing", "none")],
            Enumerable.Range(0, 32).Select(i => new CapabilityInventoryConstraint("p" + i, "Declared policy " + i, true, "workflow_policy")).ToArray(), []);
        var catalog = CapabilityCatalogBuilder.BuildSchemaAwareCapabilityCatalog([], new HashSet<string>(["set"], StringComparer.Ordinal), []);
        var schema = CapabilityMatchAssessment.BuildTypedCapabilityMatchingSchema(inventory, catalog);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
        var fields = schema["properties"]!["constraint_matches"]!["properties"]!.AsObject();
        Assert.Single(fields.Select(p => p.Value!["$ref"]!.ToString()).Distinct(StringComparer.Ordinal));
        Assert.True(schema.ToJsonString().Length < 6000);
    }

    private sealed class UnexpectedClient : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No physical capability request is needed for this validated inventory."); }
    }
}
