using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FieldRepairEscalationTests
{
    [Theory]
    [InlineData("payload", "message", false)]
    [InlineData("resultat", "message", false)]
    [InlineData("payload", "message", true)]
    public async Task OldRepairCountsCannotEscalateANewUndeclaredFieldBeforeItsTargetedRepair(string parameter, string field, bool oversizedAssessment)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); state.Graph = Graph();
        var workflow = state.Graph.Workflows[0]; var node = workflow.Steps[0];
        workflow.Inputs = [new() { Name = "source", Schema = new() { Type = "object", Properties = [new() { Name = field, Schema = new() { Type = "string" } }] } }];
        state.BehaviorPlan!.Workflows[0].Inputs = [new("source", "Dynamic input", true)];
        state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = ["source"];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        node.OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        node.Input = Obj(("message", new() { Kind = "compute", Text = parameter + ".response." + field,
            Members = [new(parameter, new() { Kind = "input", Source = "source" })] }));
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = [node.Key], Status = "invalid",
            ContractVersion = PlanningDataflow.ContractVersion, Calls = 37, RepairCalls = 30, RepairCallsAtRetry = 30 };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(state.Graph, unit, PlanningConstruction.Values(workflow, unit), state.Preparation!);
        unit.CandidateHash = PlanningGraphCompiler.Fingerprint(unit.Candidate.ToJsonString());
        unit.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Preparation!).Where(d => d.Code == "COMPUTATION_FIELD_UNDECLARED").ToList();
        Assert.Single(unit.Diagnostics); state.ConstructionUnits = [unit];
        state.Dataflow = PlanningDataflow.Describe(state.Graph, state.Preparation!);
        if (oversizedAssessment)
        {
            // A previously repaired defect may warrant an early observation review,
            // but an oversized optional review must not suppress the next field repair.
            unit.Candidate["functions"] = "// " + new string('x', 80_000) + "\n";
            unit.CandidateHash = PlanningGraphCompiler.Fingerprint(unit.Candidate.ToJsonString());
            TypedWorkflowPlanner.RecordFieldRepair(state, unit); unit.RepairedFieldCandidateHash = unit.CandidateHash;
        }
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("repair_unit", phase);
            Assert.True(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()) <= 12_000);
            var coordinate = Assert.Single(request.StructuredOutputSchema["properties"]!["changes"]!["properties"]!.AsObject()).Key;
            var replacement = unit.Candidate["nodes"]![node.Key]!["values"]!["message"]!.DeepClone();
            replacement["text"] = parameter + "." + field;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = new JsonObject { [coordinate] = replacement }, ["remove"] = new JsonArray() } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "repair_unit" }, runtime.Phases);
        Assert.Equal("validated", state.ConstructionUnits[0].Status);
        Assert.Equal(38, state.ConstructionUnits[0].Calls); Assert.Equal(31, state.ConstructionUnits[0].RepairCalls);
        Assert.Equal(0, state.PreparationReassessments); Assert.NotNull(state.ApprovedBehaviorHash);
        if (oversizedAssessment) Assert.Contains(state.Attempts, a => a.Phase == "construction_observation_review_deferred");
    }

    [Fact]
    public void EscalationRequiresAReceivedCandidateWithTheSameFindingAndDependencyContract()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton();
        var finding = new PlanningDiagnostic("COMPUTATION_FIELD_UNDECLARED", "/workflows/0/steps/0/input", "Undeclared envelope.");
        var unit = new PlanningConstructionUnit { Key = "unit", Kind = "implementation", NodeKeys = ["greeting"], CandidateHash = "before", Diagnostics = [finding], RepairCalls = 100 };
        Assert.False(TypedWorkflowPlanner.RepairedCurrentField(state, unit, finding));
        TypedWorkflowPlanner.RecordFieldRepair(state, unit);
        Assert.False(TypedWorkflowPlanner.RepairedCurrentField(state, unit, finding));
        unit.CandidateHash = "received"; unit.RepairedFieldCandidateHash = "received";
        Assert.True(TypedWorkflowPlanner.RepairedCurrentField(state, unit, finding));
        Assert.False(TypedWorkflowPlanner.RepairedCurrentField(state, unit, finding with { Message = "A different missing field." }));
        state.Preparation!.Fingerprint = "changed-catalog";
        Assert.False(TypedWorkflowPlanner.RepairedCurrentField(state, unit, finding));
    }
}
