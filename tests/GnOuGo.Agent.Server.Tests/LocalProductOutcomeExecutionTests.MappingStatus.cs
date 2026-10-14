using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("missing")]
    [InlineData("incomplete")]
    public Task FactsExtractionAndDeterministicStatusPreserveWorkbookAndRecovery(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", viaHost: variant != "incomplete", producerStatus: true);

    private static void AddProducerStatus(PlanningProposal proposal)
    {
        var scope = proposal.Plan!.Root.Tasks.Single(t => t.Id == "scrape_products").Body!;
        scope.Outputs.Add(new("status", V10Output("extract_product_detail", "status")));
        // Apply the same explicit test-only composition edit as the retained replay.
        // The production compiler does not rewrite mapping contracts.
        MappingStatusComposition.Separate(scope, "open_product_page", "extract_product_detail", "assemble_acquisition_outcome");
        proposal.Plan.Root.Outputs.Add(new("acquisitions", V10Output("scrape_products", "status")));
        proposal.Requirements!.Outputs!.Add(new()
        {
            Name = "acquisitions", Type = new() { Kind = "array", Items = ProductTransformationPlan.Obj(
                ("success", new() { Kind = "boolean" }), ("availability", new() { Kind = "boolean" }),
                ("completeness", new() { Kind = "boolean" }), ("message", new() { Nullable = true })) }
        });
    }
}
