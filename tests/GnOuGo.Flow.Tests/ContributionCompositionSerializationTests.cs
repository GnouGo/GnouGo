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
        var proof = new PlanningExecutionContributionProof(6, "runtime", "decision", "domain", [], "proof")
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

    [Fact]
    public void GeneratedContractsPreserveSourceOnlyFallbackAndNullableRuntimeProvenance()
    {
        var binding = new PlanningContributionSourceBinding("fallback-span", "clause", "fallback-obligation", "source-proof");
        var contribution = new PlanningExecutionContribution("fallback", "fallback-span", "governing_property", null,
            "governing_property", null, null, PlanningContributionOrigin.ModelQualification)
            { SourceBindings = [binding], GoverningKind = "runtime_fallback" };
        var proof = new PlanningExecutionContributionProof(6, null, "decision", "domain", [contribution], "proof")
            { SourceBindings = [binding], ClauseReference = "clause" };
        var json = JsonSerializer.Serialize(proof, PlanningJsonContext.Default.PlanningExecutionContributionProof);
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningExecutionContributionProof)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningExecutionContributionProof));
        Assert.Null(restored.RuntimeEvidenceId); Assert.Empty(restored.RuntimeEvidenceIds);
        Assert.Equal(binding, Assert.Single(restored.Contributions[0].SourceBindings));
        Assert.Equal("runtime_fallback", restored.Contributions[0].GoverningKind);
        var applicability = new PlanningGoverningApplicabilityProof(2, "fallback", null, "fallback-span", "active", ["effect"], null,
            [new("effect", ["fallback-span"])], [], null, PlanningApplicabilityOrigin.ModelApplicability, "applicability", "realized", "domain", "proof")
            { SourceBindings = [binding] };
        var applied = JsonSerializer.Serialize(applicability, PlanningJsonContext.Default.PlanningGoverningApplicabilityProof);
        var reloaded = JsonSerializer.Deserialize(applied, PlanningJsonContext.Default.PlanningGoverningApplicabilityProof)!;
        Assert.Null(reloaded.RuntimeEvidenceId); Assert.Equal(binding, Assert.Single(reloaded.SourceBindings));
    }
}
