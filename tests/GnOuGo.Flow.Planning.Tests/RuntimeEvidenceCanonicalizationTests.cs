using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic responses reproduce duplicate noise without substituting historical receipts.</summary>
public sealed class RuntimeEvidenceCanonicalizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningSnapshot State() => OperationAdmissionTests.State("Transform the supplied value.");
    private static JsonObject Role(string role) => new() { ["role"] = role };
    private static JsonObject Action() => JsonNode.Parse("""
        {"role":"local_behavior","kind":"local_processing","action":{"start":"b0","end":"b4"},
         "execution":"generated_workflow","evidence":"action","required":true,"baseline":null}
        """)!.AsObject();
    private static List<PlanningRuntimeEvidence> Parse(PlanningSnapshot state, params JsonObject[] entries)
    {
        var scope = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        return PlanningOperations.ParseRuntime(state, scope.Clause, scope.Select, new JsonArray(entries.Select(e => e.DeepClone()).ToArray()));
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("policy")]
    [InlineData("planning_directive")]
    public void RepeatedNonExecutableRolesAreIdempotent(string role)
    {
        var state = State();
        var canonical = Assert.Single(Parse(state, Role(role), Role(role), Role(role)));
        Assert.Equal(Assert.Single(Parse(state, Role(role))), canonical);
        Assert.Equal(role, canonical.Role); Assert.Null(canonical.ActionReference);
        Assert.Empty(state.RequestAccounting); Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public void ExecutableDuplicatesCompareNormalizedFieldsRegardlessOfJsonPropertyOrder()
    {
        var state = State(); var first = Action();
        var reordered = new JsonObject(first.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        var canonical = Assert.Single(Parse(state, first, reordered, first));
        Assert.Equal(Assert.Single(Parse(state, first)), canonical);
        PlanningOperations.ValidateRuntime(state, canonical);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("baseline")]
    [InlineData("resourceAction")]
    [InlineData("ownership")]
    public void IdentityCollisionCannotHideConflictingSemanticFields(string field)
    {
        var state = State(); var first = Action();
        if (field is "resourceAction" or "ownership")
        {
            first["role"] = "runtime_action"; first["kind"] = "resource_lifecycle";
            first["resource"] = new JsonObject { ["start"] = "b2", ["end"] = "b4" };
            first["resourceAction"] = "create"; first["ownership"] = "workflow_runtime_resource";
        }
        var second = first.DeepClone().AsObject();
        switch (field)
        {
            case "required": second[field] = false; break;
            case "baseline": first[field] = "baseline_one"; second[field] = "baseline_two"; break;
            case "resourceAction": second[field] = "delete"; break;
            case "ownership": second[field] = "foreign_resource"; break;
        }
        // These fields deliberately are not in the stable evidence ID. Full normalized
        // contract equality, rather than DistinctBy(Id), must guard their collision.
        var a = Assert.Single(Parse(state, first)); var b = Assert.Single(Parse(state, second));
        Assert.Equal(a.Id, b.Id); Assert.NotEqual(a, b);
        var error = Assert.Throws<WorkflowRuntimeException>(() => Parse(state, first, second));
        Assert.Equal("INTENT_RUNTIME_EVIDENCE_CONFLICT", error.Code);
        Assert.StartsWith("/operations/@", error.Details!["location"]!.ToString());
        Assert.Empty(state.RequestAccounting); Assert.Empty(state.RepairAllowances);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("kind")]
    [InlineData("evidence")]
    public void DifferentOwnedEvidenceIsNotCollapsed(string field)
    {
        var state = State(); var first = Action(); var second = first.DeepClone().AsObject();
        if (field == "action") second["action"]!["start"] = "b1";
        else if (field == "kind") { second["role"] = "runtime_action"; second["kind"] = "external_read"; }
        else second["evidence"] = "governing";
        var values = Parse(state, first, second, first);
        Assert.Equal(2, values.Count); Assert.NotEqual(values[0].Id, values[1].Id);
    }

    [Fact]
    public void FirstSeenOrderingAndFingerprintSurviveSerializedRestart()
    {
        var state = State(); var entries = new[] { Role("policy"), Role("contract"), Role("policy"), Role("planning_directive"), Role("contract") };
        state.RuntimeEvidence = Parse(state, entries); state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        Assert.Equal(["policy", "contract", "planning_directive"], state.RuntimeEvidence.Select(e => e.Role));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(state.RuntimeEvidence, Parse(restored, entries));
        Assert.Equal(state.RuntimeEvidenceFingerprint, PlanningOperations.RuntimeFingerprint(restored));
        PlanningOperations.RequireRuntimeEvidence(restored);
        var differentMultiplicity = Parse(restored, Role("policy"), Role("contract"), Role("planning_directive"));
        Assert.Equal(state.RuntimeEvidence, differentMultiplicity);
    }

    [Fact]
    public async Task CompletedDuplicateReceiptReplaysWithoutCorrectionOrAnotherModelRequest()
    {
        var state = State(); state.Request.MaxRepairsPerWorkflowGate = 0;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        {
            Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonObject { ["obligations"] = new JsonArray(), ["runtime"] = new JsonArray(Role("contract"), Role("contract"), Role("contract")) })))
        }) };
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        Assert.Single(state.RuntimeEvidence); Assert.Single(runtime.Requests); Assert.Empty(state.RepairAllowances);
        Assert.All(state.DecisionPages, p => Assert.False(p.Correction));
        var restored = PlanningContext.Clone(state); var count = restored.RequestAccounting.Count;
        var offline = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("A completed receipt cannot redispatch.") };
        await PlanningSourceDecisions.InterpretAsync(restored, offline, Ct);
        Assert.Empty(offline.Requests); Assert.Empty(restored.RepairAllowances); Assert.Equal(count, restored.RequestAccounting.Count);
        Assert.Equal(state.RuntimeEvidence, restored.RuntimeEvidence); Assert.Equal(state.RuntimeEvidenceFingerprint, restored.RuntimeEvidenceFingerprint);
    }
}
