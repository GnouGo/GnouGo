using System.Text.Json;
using Xunit;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Tests;

public sealed class ContributionCompositionSerializationTests
{
    [Fact]
    public void GeneratedContractsPreserveCompositionWithoutGrantingChildSupport()
    {
        var parent = new PlanningContributionUnit("request", "requested_execution", "clause", "predicate", ["request-span"],
            ["runtime"], "effect", "requested_owned_occurrence", "owner", "boundary");
        var child = new PlanningContributionUnit("qualifier", "governing_property", "clause", null, ["property-span"],
            ["runtime"], null, "governing_property", null, null) { ParentRequestUnitId = parent.Id };
        var proof = new PlanningExecutionContributionProof(5, "runtime", "decision", "domain", [], "proof")
            { Units = [child, parent], ClauseReference = "clause", RuntimeEvidenceIds = ["runtime"] };
        var json = JsonSerializer.Serialize(proof, PlanningJsonContext.Default.PlanningExecutionContributionProof);
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningExecutionContributionProof)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningExecutionContributionProof));
        Assert.Equal("request", restored.Units[0].ParentRequestUnitId);
        Assert.Null(restored.Units[0].EffectId); Assert.Null(restored.Units[0].PredicateReference);
        Assert.Null(restored.Units[1].ParentRequestUnitId);
        // Historical absence remains absence; serialization does not invent a link.
        var historical = JsonSerializer.SerializeToNode(parent, PlanningJsonContext.Default.PlanningContributionUnit)!;
        historical.AsObject().Remove("parentRequestUnitId");
        Assert.Null(historical.Deserialize(PlanningJsonContext.Default.PlanningContributionUnit)!.ParentRequestUnitId);
    }
}
