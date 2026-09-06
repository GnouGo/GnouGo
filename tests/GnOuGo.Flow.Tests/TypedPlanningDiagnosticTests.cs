using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class TypedPlanningDiagnosticTests
{
    [Fact]
    public void ConditionalFindingsIdentifyTheSwitchAndDeclaredDecisionField()
    {
        var diagnostic = WorkflowPlanExecutor.TypedArtifactDiagnostic(new JsonObject
        {
            ["workflow"] = "main", ["switch_id"] = "decision", ["message"] = "Use the declared producer.",
            ["validation_issue"] = "decision_source_invalid", ["decision_field"] = "outcome"
        }, "CONTRACT_INVALID", PlanningValidationStage.ConditionalActivation);
        Assert.Equal("workflow:main/step:decision", diagnostic.Location);
        Assert.Contains("decision_source_invalid", diagnostic.Message);
        Assert.Contains("outcome", diagnostic.Message);
        Assert.Equal(PlanningValidationStage.ConditionalActivation, diagnostic.ValidationStage);
    }

    [Fact]
    public void ArtifactFindingsPreserveLocationExpectedContractAndStage()
    {
        var diagnostic = WorkflowPlanExecutor.TypedArtifactDiagnostic(new JsonObject
        {
            ["code"] = "MCP_ARTIFACT_PROVENANCE_UNPROVEN", ["workflow"] = "main", ["consumer_step"] = "consumer",
            ["request_pointer"] = "/artifact", ["artifact_kind"] = "opaque.resource", ["expected"] = "Use the unchanged declared producer value.",
            ["invalid_path"] = "data.item.invented", ["allowed_paths"] = new JsonArray("data.item.producer.response.value"), ["hint"] = "Preserve the producer envelope."
        }, "fallback", PlanningValidationStage.CapabilityContracts);
        Assert.Equal("workflow:main/step:consumer/field:input.request.artifact", diagnostic.Location);
        Assert.Contains("opaque.resource", diagnostic.Message);
        Assert.Contains("unchanged declared producer", diagnostic.Message);
        Assert.Contains("data.item.producer.response.value", diagnostic.Message);
        Assert.Contains("Preserve the producer envelope", diagnostic.Message);
        Assert.Equal(PlanningValidationStage.CapabilityContracts, diagnostic.ValidationStage);
    }

    [Fact]
    public void AdditiveArtifactContractsAndDiagnosticStagesRoundTripWithoutSnapshotMigration()
    {
        var snapshot = JsonSerializer.Deserialize("""{"schemaVersion":2,"diagnostics":[{"code":"OLD","location":"$","message":"Old finding"}]}""", PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Null(snapshot.Diagnostics[0].ValidationStage);
        snapshot.Preparation = new() { Capabilities = [new() { ArtifactContract = new(1, [new("opaque.resource", "/result", "materialize")], [new("opaque.resource", "/argument", true)]) }] };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal("/result", Assert.Single(restored.Preparation!.Capabilities[0].ArtifactContract!.Produces).Pointer);
        Assert.Equal(2, restored.SchemaVersion);
    }
}
