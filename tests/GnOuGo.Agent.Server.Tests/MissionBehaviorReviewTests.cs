using System.Text.Json;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MissionBehaviorReviewTests
{
    private static (PlanningBehaviorPlan Plan, PlanningPreparation Preparation) Batch() => (new()
    {
        Workflows =
        [
            new() { Key = "main", Inputs = [new("batchId", "Identifier", true), new("threshold", "Threshold", false)], Outputs = [new("summary", "Summary", true)],
                Steps = [new() { Key = "load", CapabilityId = "read" }, new() { Key = "records", Kind = "loop", Steps = [new() { Key = "classify", Kind = "workflow", WorkflowKey = "classify_record" }] },
                    new() { Key = "summarize", Kind = "workflow", WorkflowKey = "summarize_batch" }], Finally = [new() { Key = "finished", CapabilityId = "set" }] },
            new() { Key = "classify_record", Inputs = [new("record", "Record", true), new("threshold", "Threshold", true)], Outputs = [new("classifiedResult", "Classification", true)] },
            new() { Key = "summarize_batch", Inputs = [new("classifiedResults", "Classifications", true)], Outputs = [new("summary", "Summary", true)] }
        ]
    }, new() { Capabilities = [new() { Id = "read", Server = "fixture", Method = "load_record_batch" }, new() { Id = "set", StepType = "set" }] });

    [Fact]
    public void BatchReviewPreservesTheDeclaredCallBoundariesWithoutMutation()
    {
        var (plan, preparation) = Batch();
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.PlanningBehaviorPlan);
        MissionBehaviorReview.RequireBatchTopology(plan, preparation);
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.PlanningBehaviorPlan));
    }

    [Theory]
    [InlineData("repeated_read")]
    [InlineData("repeated_summary")]
    [InlineData("extra_classification")]
    [InlineData("missing_default_input")]
    [InlineData("external_finalizer")]
    [InlineData("missing_finalizer")]
    [InlineData("extra_callee")]
    public void BatchReviewRejectsChangedBusinessExecution(string defect)
    {
        var (plan, preparation) = Batch(); var main = plan.Workflows[0]; var loop = main.Steps[1];
        if (defect == "repeated_read") { loop.Steps.Add(main.Steps[0]); main.Steps.RemoveAt(0); }
        if (defect == "repeated_summary") { loop.Steps.Add(main.Steps[2]); main.Steps.RemoveAt(2); }
        if (defect == "extra_classification") main.Steps.Add(new() { Kind = "workflow", WorkflowKey = "classify_record" });
        if (defect == "missing_default_input") main.Inputs.RemoveAt(1);
        if (defect == "external_finalizer") preparation.Capabilities[1].StepType = "mcp.call";
        if (defect == "missing_finalizer") main.Finally.Clear();
        if (defect == "extra_callee") plan.Workflows.Add(new() { Key = "extra" });
        Assert.Throws<InvalidOperationException>(() => MissionBehaviorReview.RequireBatchTopology(plan, preparation));
    }

    private static (PlanningBehaviorPlan Plan, PlanningPreparation Preparation, PlanningObligation[] Operations) CodeReview()
    {
        var preparation = new PlanningPreparation
        {
            Capabilities = new[] { "git_clone", "git_compare_refs", "copilot_review", "pull_request_review_write" }
                .Select(method => new PlanningCapability { Id = method, Method = method }).ToList(),
            Decisions = [new() { ContractSource = PlanningDecisionContract.HumanConfirmation, EffectOperationIds = ["publish"], NoEffectValues = ["false"] }]
        };
        var main = new PlanningBehaviorWorkflow
        {
            Steps = preparation.Capabilities.Select(c => new PlanningBehaviorNode { Key = c.Id, CapabilityId = c.Id }).ToList(),
            Finally = [new() { Key = "cleanup", OperationIds = ["owned_cleanup"] }]
        };
        main.Steps.Insert(3, new() { Key = "permission", Kind = "confirmation" });
        return (new() { Workflows = [main] }, preparation, [new("owned_cleanup", ["evidence"], "main", "cleanup", true)]);
    }

    [Fact]
    public void CodeReviewRetainsPermissionAndOwnedCleanup()
    {
        var (plan, preparation, operations) = CodeReview();
        MissionBehaviorReview.RequireCodeReviewTopology(plan, preparation, operations);
    }

    [Theory]
    [InlineData("loop_clone")]
    [InlineData("duplicate_review")]
    [InlineData("missing_permission")]
    [InlineData("missing_rejection")]
    [InlineData("missing_cleanup")]
    public void CodeReviewCannotApproveWeakenedEffectBoundaries(string defect)
    {
        var (plan, preparation, operations) = CodeReview(); var main = plan.Workflows[0];
        if (defect == "loop_clone") { var clone = main.Steps[0]; main.Steps[0] = new() { Kind = "loop", Steps = [clone] }; }
        if (defect == "duplicate_review") main.Steps.Add(new() { CapabilityId = "copilot_review" });
        if (defect == "missing_permission") main.Steps.RemoveAll(n => n.Kind == "confirmation");
        if (defect == "missing_rejection") preparation.Decisions[0].NoEffectValues.Clear();
        if (defect == "missing_cleanup") main.Finally.Clear();
        Assert.Throws<InvalidOperationException>(() => MissionBehaviorReview.RequireCodeReviewTopology(plan, preparation, operations));
    }
}
