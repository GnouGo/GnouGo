using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RequestAccountingTests
{
    [Fact]
    public void RepairOnlyExposureCountsVerifiedModelUseWithoutInventingARequestReason()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "repair", workflow.Key, PlanningModelCalls.Request(state, request.Prompt, request.Schema));
        PlanningRepairAllowances.Reserved(state, workflow.Key, PlanningGates.Response);
        PlanningConvergence.Expose(state, [hole], call.Id);
        Assert.Equal(0, state.Construction.Workflows.Single().ModelUsed);
        state = PlanningContext.Clone(state);
        PlanningConvergence.Receipt(state, call, new());
        PlanningConvergence.Receipt(state, call, new());
        Assert.Equal(1, state.Construction.Workflows.Single().ModelUsed);
        Assert.Equal(1, state.Construction.Workflows.Single().ModelHoleExposures);
        Assert.Empty(state.RequestAccounting.Single().HoleReasons);
        Assert.Equal(1, state.RepairAllowances.Single().Attempts);
    }

    [Fact]
    public void PreparationRepairRequestsHaveExplicitDurableAttribution()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "workflow.plan.capability_matching_repair", "", PlanningModelCalls.Request(state, request.Prompt, request.Schema));
        PlanningConvergence.Receipt(state, call, new());
        Assert.Equal(1, Assert.Single(state.RequestCounts).Repairs);
        Assert.Equal("$plan", state.RequestCounts[0].WorkflowKey);
        Assert.Empty(state.RepairAllowances); // Attribution does not replenish or alter the existing preparation policy.
    }
    [Fact]
    public void AReceiptEmitsModelUseWithoutAnotherHoleExposureAndReplayIsSilent()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "construction", workflow.Key, PlanningModelCalls.Request(state, request.Prompt, request.Schema));
        PlanningConvergence.Expose(state, [hole], call.Id);
        PlanningConvergence.AttributeHoles(state, call, workflow, [hole]);
        var before = PlanningContext.Clone(state);
        PlanningConvergence.Receipt(state, call, new());
        var events = new List<string>();
        PlanningConvergenceTelemetry.Observe(before, state, (name, fields) =>
        {
            events.Add(name);
            if (name == "planning.convergence") Assert.Contains(fields, field => field.Key == "model_used" && Equals(field.Value, 1));
        });
        Assert.Contains("planning.convergence", events);
        Assert.Equal(before.Construction.Workflows.Single().ModelHoleExposures, state.Construction.Workflows.Single().ModelHoleExposures);
        events.Clear();
        PlanningConvergenceTelemetry.Observe(state, PlanningContext.Clone(state), (name, _) => events.Add(name));
        Assert.Empty(events);
    }
    [Fact]
    public void RepairAndFailureAttributionRemainPerPhaseAndGateAcrossReplay()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "construction", workflow.Key, PlanningModelCalls.Request(state, request.Prompt, request.Schema), PlanningGates.Response);
        PlanningRepairAllowances.Reserved(state, workflow.Key, PlanningGates.Response);
        PlanningConvergence.AttributeHoles(state, call, workflow, [hole]);
        PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Response, call.Id, [new("INVALID", hole.Path, "First wording")]);
        state = PlanningContext.Clone(state);
        PlanningConvergence.Receipt(state, call, new());
        PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Response, call.Id, [new("INVALID", hole.Path, "Different wording")]);
        var counts = Assert.Single(state.RequestCounts);
        Assert.Equal("construction", counts.Phase);
        Assert.Equal(1, counts.Repairs); Assert.Equal(1, counts.Failures);
        Assert.Equal(1, counts.AvoidableDispatches); // The sole source was already deterministic at reservation.
        Assert.Null(counts.InputTokens); Assert.Null(counts.OutputTokens);
    }
    [Fact]
    public void ReservationsAndReplayedReceiptsHaveDistinctDurableAttribution()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        workflow.Inputs.Add(new() { Name = "alternative", Required = true, Schema = new() { Type = "string" } });
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + hole.NodeKey] = ["source", "alternative"];
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "construction", workflow.Key, PlanningModelCalls.Request(state, request.Prompt, request.Schema));
        PlanningConvergence.Expose(state, [hole], call.Id); PlanningConvergence.AttributeHoles(state, call, workflow, [hole]);
        Assert.Equal(0, state.RequestCounts.Single().ModelUsed);
        Assert.Null(state.RequestCounts.Single().InputTokens);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var response = new LLMResponse { Usage = new JsonObject { ["input_tokens"] = 52, ["output_tokens"] = 9 } };
        PlanningConvergence.Receipt(restored, call, response); PlanningConvergence.Receipt(restored, call, response);
        var counts = Assert.Single(restored.RequestCounts);
        Assert.Equal(1, counts.Reservations); Assert.Equal(1, counts.ModelUsed);
        Assert.Equal(52, counts.InputTokens); Assert.Equal(9, counts.OutputTokens);
        Assert.Equal(0, counts.AvoidableDispatches); Assert.Equal(0, counts.AvoidableExtraRequests);
        Assert.Equal(1, restored.Construction.Workflows.Single().ModelUsed);
        var resolved = restored.Construction.Holes.Single();
        resolved.Resolved = true; resolved.ResolutionOrigin = "model";
        PlanningConvergence.Refresh(restored);
        Assert.Equal(1, restored.Construction.Workflows.Single().ModelRequired);
        Assert.Equal(1, restored.Construction.Workflows.Single().ModelUsed);
    }

    [Fact]
    public void MissingHistoricalAttributionRemainsUnknown()
    {
        var (state, _, hole) = ConvergenceDomainTests.Input();
        hole.ExposedRequests.Add("historical"); PlanningConvergence.Refresh(state);
        Assert.Null(state.Construction.Workflows.Single().ModelUsed);
        hole.Resolved = true; hole.ResolutionOrigin = "model";
        PlanningConvergence.Refresh(state);
        Assert.Null(state.Construction.Workflows.Single().ModelRequired);
        hole.ExposedRequests.Clear();
        PlanningConvergence.Refresh(state);
        Assert.Null(state.Construction.Workflows.Single().ModelUsed);
        Assert.Empty(state.RequestCounts);
    }

    [Fact]
    public void MandatoryReviewIsAttributedSeparatelyAndMissingUsageIsNotZero()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var call = PlanningModelCalls.Reserve(state, "semantic_review", "", PlanningModelCalls.Request(state, request.Prompt, request.Schema), PlanningGates.Semantic);
        PlanningConvergence.Receipt(state, call, new());
        var record = Assert.Single(state.RequestAccounting);
        Assert.Equal("$plan", record.WorkflowKey); Assert.Equal("mandatory_validation", record.Purpose);
        Assert.Empty(record.HoleReasons); Assert.Null(record.InputTokens); Assert.Null(record.OutputTokens);
    }
}
