using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class TypedPlanningDiagnosticTests
{
    [Theory]
    [InlineData(false, false, "intent")]
    [InlineData(true, false, "capabilities")]
    [InlineData(true, true, "behavior")]
    public void PendingPhaseDoesNotKeepTheLastCompletedAssessmentLabel(bool intentChecked, bool prepared, string phase)
    {
        var state = new PlanningSnapshot { Status = PlanningStatus.Created, CurrentPhase = PlanningPhase.Intent, IntentChecked = intentChecked, Preparation = prepared ? new() : null };
        Assert.Equal(phase, PlanningPhase.Resolve(state));
        state.Status = PlanningStatus.Recovery; state.CurrentPhase = PlanningPhase.Capabilities;
        Assert.Equal(PlanningPhase.Capabilities, PlanningPhase.Resolve(state));
    }

    [Fact]
    public void DecisionLineageFindingsPermitRepairOfTheDiscriminator()
    {
        var diagnostic = WorkflowPlanExecutor.TypedArtifactDiagnostic(new JsonObject
        {
            ["workflow"] = "main", ["switch_id"] = "decision", ["message"] = "Preserve the declared boundary.",
            ["validation_issue"] = "conditional_decision_lineage_unproven"
        }, "CONTRACT_INVALID", PlanningValidationStage.ConditionalActivation);
        Assert.Equal("workflow:main/step:decision/field:expr", diagnostic.Location);
    }

    [Fact]
    public void MissingReducerDependencyTargetsTheProducerInsteadOfImmutableRouting()
    {
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
        version: 1
        workflows:
          main:
            steps:
              - id: reducer
                type: decision.evaluate
                input:
                  decisions:
                    outcome:
                      allowed_values: [YES, NO]
                      cases: []
                      default: NO
        """);
        var finding = new JsonObject { ["workflow"] = "main", ["switch_id"] = "route", ["decision_field"] = "outcome" };
        WorkflowPlanExecutor.RetargetTypedDecisionFinding(finding, document);
        var diagnostic = WorkflowPlanExecutor.TypedArtifactDiagnostic(finding, "DEPENDENCY_INVALID", PlanningValidationStage.ConditionalActivation);
        Assert.Equal("workflow:main/step:reducer/field:input.decisions.outcome", diagnostic.Location);
        Assert.Contains("Preserve every required permission", diagnostic.Message);
    }

    [Theory]
    [InlineData("${Boolean(data.steps.observe)}", true)]
    [InlineData("${Boolean(data.steps.unknown)}", false)]
    [InlineData("${'data.steps.observe'.length > 0}", false)]
    [InlineData("${Boolean(data.steps['observe'])}", true)]
    [InlineData("${Boolean(data.steps.each)}", true)]
    [InlineData("${data.steps.each.results.length > 0}", true)]
    [InlineData("${data.steps.each.count > 0}", false)]
    public void DecisionDependenciesRecognizeExactWholeResultsAndLoopResultCollections(string expression, bool expected)
    {
        var observe = new GnOuGo.Flow.Core.Models.StepDef { Id = "observe", Type = "mcp.call" };
        var document = new GnOuGo.Flow.Core.Models.WorkflowDocument { Workflows = new() { ["main"] = new() { Steps = [new() { Id = "each", Type = "loop.sequential", Steps = [observe] }] } } };
        Assert.Equal(expected, WorkflowPlanExecutor.LocalDecisionExpressionDependsOnSources(document,
            new Dictionary<string, IReadOnlyList<(string Workflow, GnOuGo.Flow.Core.Models.StepDef Call)>>(), [("main", observe)], "main", expression, new(StringComparer.Ordinal)));
    }

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
