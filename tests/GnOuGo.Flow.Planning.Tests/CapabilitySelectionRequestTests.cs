using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilitySelectionRequestTests
{
    [Theory]
    [InlineData(12000)]
    [InlineData(3000)]
    public void CompleteRequestsFitTheConfiguredCeilingAndCoverEveryEntryOnce(int ceiling)
    {
        var catalog = Catalog();
        var pages = CapabilitySelectionRequests.Build(Inventory(), catalog, false, Operations, Constraints, ceiling);
        Assert.True(pages.Count > 1);
        Assert.Equal(catalog.Entries.Select(e => e.Id), pages.SelectMany(p => p.CatalogIds));
        foreach (var page in pages)
        {
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(page.Prompt, page.Schema), 1, ceiling);
            Assert.Empty(PlanningContractValidation.ValidateSchema(page.Schema, true));
            Assert.DoesNotContain("unrelated policy", page.Prompt);
            Assert.DoesNotContain("source_id", page.Prompt);
            Assert.Equal(1, page.Prompt.Split("Shared declared obligation").Length - 1);
        }
        var repeated = CapabilitySelectionRequests.Build(Inventory(), catalog, false, Operations, Constraints, ceiling);
        Assert.Equal(pages.Select(p => p.Prompt + p.Schema.ToJsonString()), repeated.Select(p => p.Prompt + p.Schema.ToJsonString()));
    }

    [Fact]
    public void ResponseSchemaRejectsIdsOutsideTheCurrentPageAndInventoryScope()
    {
        var page = CapabilitySelectionRequests.Build(Inventory(), Catalog(), false, Operations, Constraints, 3000)[0];
        var response = Response(page.CatalogIds.First());
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, page.Schema));
        response["operation_candidates"]![0]!["catalog_ids"] = new JsonArray("physical_000100");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, page.Schema));
        response = Response(page.CatalogIds.First());
        response["operation_candidates"]![0]!["operation_id"] = "unknown";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, page.Schema));
        response = Response(page.CatalogIds.First());
        response["constraint_candidates"] = new JsonArray(new JsonObject { ["constraint_id"] = "policy", ["catalog_ids"] = new JsonArray() });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, page.Schema));
    }

    [Fact]
    public void UnpageableEntryStopsBeforeDispatchWithoutDroppingContractText()
    {
        var catalog = new PhysicalCapabilityCatalog([new("physical", "provider", "tool", "method", new string('x', 100000), [])], 100000);
        var error = Assert.Throws<WorkflowRuntimeException>(() => CapabilitySelectionRequests.Build(Inventory(), catalog, false, Operations, Constraints, 12000));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Contains("physical", error.Message);
    }

    [Fact]
    public async Task RestartReusesCompletedPagesAndRestoresTheirSelections()
    {
        var ctx = new StepExecutionContext
        {
            Engine = new WorkflowEngine(), Step = new() { Source = new() { Id = "planning", Type = "workflow.plan" } },
            PlanningGeneration = new() { MaxInputTokensPerRequest = 3000 }, PreparationCheckpoint = new()
        };
        var client = new Selector();
        using var span = ctx.BeginTelemetrySpan("test", "test", []);
        Dictionary<string, HashSet<string>> Selections() => Operations.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var first = Selections();
        ctx.PersistPreparation = ct => ctx.PreparationCheckpoint.ValidatedResults.Count == 2
            ? Task.FromException(new OperationCanceledException(ct)) : Task.CompletedTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CapabilityDiscovery.RunPhysicalCandidateSelectionPassAsync(ctx, client, Inventory(), Catalog(), null, "fixture", "low", false, Operations, Constraints, first, [], span, TestContext.Current.CancellationToken));
        Assert.Equal(2, client.Calls);
        var checkpoint = JsonSerializer.Deserialize(JsonSerializer.Serialize(ctx.PreparationCheckpoint, PlanningJsonContext.Default.PlanningPreparationCheckpoint), PlanningJsonContext.Default.PlanningPreparationCheckpoint)!;
        ctx.PreparationCheckpoint = checkpoint; ctx.PersistPreparation = _ => Task.CompletedTask;
        var resumed = Selections();
        await CapabilityDiscovery.RunPhysicalCandidateSelectionPassAsync(ctx, client, Inventory(), Catalog(), null, "fixture", "low", false, Operations, Constraints, resumed, [], span, TestContext.Current.CancellationToken);
        var pages = CapabilitySelectionRequests.Build(Inventory(), Catalog(), false, Operations, Constraints, 3000);
        Assert.Equal(pages.Count, client.Calls);
        Assert.Subset(resumed["op"], first["op"]);
        Assert.Equal(pages.Count, resumed["op"].Count);
    }

    private static readonly HashSet<string> Operations = new(["op", "op2"], StringComparer.Ordinal);
    private static readonly HashSet<string> Constraints = new(StringComparer.Ordinal);
    private static CapabilityInventory Inventory() => new(true,
        Operations.Select(id => new CapabilityInventoryOperation(id, "Declared external read", true, "external_effect", "read")
        { CoverageRequirements = ["Shared declared obligation"], CoverageRequirementEvidence = [new("e", "source", 0, 26, "Shared declared obligation")] }).ToArray(),
        [new("policy", "unrelated policy", false, "workflow_policy")], []);
    private static PhysicalCapabilityCatalog Catalog() => new(Enumerable.Range(1, 100)
        .Select(i => new PhysicalCapabilityEntry($"physical_{i:D6}", "provider", "tool", "m" + i, "Declared metadata: " + new string('x', 900), [])).ToArray(), 100000);
    private static JsonObject Response(string id) => new()
    {
        ["operation_candidates"] = new JsonArray(new JsonObject { ["operation_id"] = "op", ["catalog_ids"] = new JsonArray(id) }),
        ["constraint_candidates"] = new JsonArray()
    };
    private sealed class Selector : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = Response(request.StructuredOutputSchema!["$defs"]!["candidates"]!["items"]!["enum"]![0]!.GetValue<string>()) });
        }
    }
}
