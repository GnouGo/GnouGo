using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionReferenceTests
{
    [Theory]
    [InlineData("Inspect the record. Preserve the original artifact.")]
    [InlineData("Vérifier le résultat. Conserver l’original 🧪.")]
    [InlineData("結果を検査する。元の成果物を保持する。")]
    public void ReferencesResolveExactSourceTextAndRejectForeignOrChangedSources(string text)
    {
        var refs = PlanningReferences.Issue("tenant/session", 3, "intent", "user_request", text);
        Assert.Equal(text, string.Concat(refs.Select(r => PlanningReferences.Resolve(r, "tenant/session", 3, "intent", text))));
        Assert.Equal(refs, PlanningReferences.Issue("tenant/session", 3, "intent", "user_request", text));
        Assert.All(refs, r =>
        {
            Assert.Throws<PlanningConflictException>(() => PlanningReferences.Resolve(r, "other/session", 3, "intent", text));
            Assert.Throws<PlanningConflictException>(() => PlanningReferences.Resolve(r, "tenant/session", 4, "intent", text));
            Assert.Throws<PlanningConflictException>(() => PlanningReferences.Resolve(r, "tenant/session", 3, "intent", text + " altered"));
        });
        var schema = PlanningReferences.Schema(refs);
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(refs[0].Id), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create(text), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["excerpt"] = text }, schema));
    }

    [Fact]
    public void TypedOutcomesRoundTripWithoutConflatingTechnicalFailureAndUnsupported()
    {
        PlanningOutcome[] outcomes = [new PlanningValidWorkflow("hash"), new PlanningNeedUserClarification(new("decision", ["evidence"], new(), ["obligation"])),
            new PlanningUnsupported([new("obligation", "MISSING_DECLARED_CONTRACT", ["evidence"])])];
        foreach (var outcome in outcomes)
        {
            var json = JsonSerializer.Serialize(outcome, PlanningJsonContext.Default.PlanningOutcome);
            var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningOutcome);
            Assert.Equal(outcome.GetType(), restored!.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningOutcome));
        }
    }
}
