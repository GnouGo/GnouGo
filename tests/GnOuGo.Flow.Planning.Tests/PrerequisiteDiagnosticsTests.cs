using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PrerequisiteDiagnosticsTests
{
    private static PlanningSession State()
    {
        var state = SemanticGroundingTests.State();
        state.Catalog!.Capabilities[0].InputSchema = JsonNode.Parse("""{"type":"object","properties":{"source":{"type":"string"}}}""")!.AsObject();
        state.Grounding = new() { Selections = [new("collect", ["cap_0"], "Selected"), new("release", ["cap_1"], "Selected")] };
        return state;
    }

    private static JsonArray Blockers() => JsonNode.Parse("""
        [{"actionId":"collect","reason":"Missing original observation","prerequisite":{"kind":"missing_observation","description":"A location does not establish its contents","output":"evidence","consumerCapability":"cap_0","contractPath":"/source","rootActionId":null}},
         {"actionId":"release","reason":"Waiting for the producer","prerequisite":{"kind":"blocked_dependency","description":"Resource unavailable","output":null,"consumerCapability":null,"contractPath":null,"rootActionId":"collect"}}]
        """)!.AsArray();

    [Fact]
    public void RootAndDependentSurviveSerializationWithoutLosingBlockingStatus()
    {
        var state = State(); state.Diagnostics = PlanningPrerequisites.Read(state, Blockers());
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal("collect", restored.Diagnostics[1].Prerequisite!.RootActionId);
        Assert.All(restored.Diagnostics, d => Assert.True(d.Required));
        Assert.False(PlanningDecisions.Eligible(restored));
        Assert.Equal(2, PlanningJsonTransport.Diagnostics(restored.Diagnostics).Count);
    }

    [Theory]
    [InlineData("output", "invented")]
    [InlineData("consumerCapability", "cap_1")]
    [InlineData("contractPath", "/invented")]
    [InlineData("rootActionId", "collect")]
    [InlineData("kind", "permissions")]
    public void InvalidEvidenceCannotBecomeARepairOrDecision(string field, string value)
    {
        var blockers = Blockers(); blockers[0]!["prerequisite"]![field] = value;
        Assert.Equal("BINDING_BLOCKER_INVALID", Assert.Single(Assert.Throws<PlanningResponseException>(() => PlanningPrerequisites.Read(State(), blockers)).Diagnostics).Code);
    }

    [Fact]
    public void CyclesAndOutOfBatchBlockersAreRejected()
    {
        var blockers = Blockers(); blockers[0]!["prerequisite"]!["rootActionId"] = "release";
        Assert.Throws<PlanningResponseException>(() => PlanningPrerequisites.Read(State(), blockers));
        var state = State(); state.BindingProgress = new() { CurrentActions = ["collect"] };
        Assert.Throws<PlanningResponseException>(() => PlanningPrerequisites.Read(state, Blockers()));
    }

    [Fact]
    public void LegacyTechnicalBlockersRemainReadableAndCannotAskQuestions()
    {
        var state = State(); state.Diagnostics = PlanningPrerequisites.Read(state, JsonNode.Parse("""[{"actionId":"collect","reason":"Missing prerequisite"}]""")!.AsArray());
        Assert.Null(state.Diagnostics[0].Prerequisite); Assert.False(PlanningDecisions.Eligible(state));
        Assert.False(JsonSerializer.SerializeToNode(state.Diagnostics[0], PlanningJsonContext.Default.PlanningDiagnostic)!.AsObject().ContainsKey("prerequisite"));
        Assert.DoesNotContain("pendingRepair", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }
}
