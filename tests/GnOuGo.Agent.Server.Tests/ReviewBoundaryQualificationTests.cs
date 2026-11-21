using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ReviewBoundaryQualificationTests
{
    [Theory]
    [InlineData("{\"lineTotal\":41,\"invoiceTotal\":42}", "nominal", true)]
    [InlineData("{\"lineTotal\":-5,\"invoiceTotal\":-4}", "alternate", true)]
    [InlineData("{\"lineTotal\":41,\"invoiceTotal\":42}", "alternate", false)]
    [InlineData("{\"lineTotal\":\"41\",\"invoiceTotal\":42}", "nominal", false)]
    [InlineData("{\"lineTotal\":41,\"invoiceTotal\":42,\"extra\":0}", "nominal", false)]
    public void RuntimeOracleChecksBothExactNumericOutputs(string json, string variant, bool expected)
    {
        Assert.Equal(expected, ReviewBoundaryQualification.Outputs(true, JsonNode.Parse(json), variant));
        Assert.False(ReviewBoundaryQualification.Outputs(false, JsonNode.Parse(json), variant));
    }

    [Fact]
    public void ArtifactOracleRejectsDefaultsAndExternalDependencies()
    {
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "unitPrice", Type = new() { Kind = "number" } }],
            Root = new() { Outputs = [new("lineTotal", new() { Kind = "number", Number = 41 }), new("invoiceTotal", new() { Kind = "number", Number = 42 })] }
        };
        Assert.Empty(ReviewBoundaryQualification.Artifact(plan, null));
        plan.Inputs[0].Default = new() { Kind = "number", Number = 20.5 };
        Assert.Contains("wrong_runtime_price_input", ReviewBoundaryQualification.Artifact(plan, null));
        plan.Inputs[0].Default = null;
        plan.Root.Tasks.Add(new() { Id = "external", Kind = "operation" });
        Assert.Contains("unavailable_runtime_dependency", ReviewBoundaryQualification.Artifact(plan, null));
    }

    [Fact]
    public void AcceptedUnchangedClaimRequiresLocalReviewWithNoDraftingEcho()
        => Assert.True(ReviewBoundaryQualification.Evidence(Trace())["observed"]!.GetValue<bool>());

    [Theory]
    [InlineData("changed_plan")]
    [InlineData("added_task")]
    [InlineData("root_closed")]
    [InlineData("requirement_closed")]
    [InlineData("wrong_owner")]
    [InlineData("invalidated")]
    [InlineData("global_review")]
    [InlineData("needs_context")]
    [InlineData("draft_echo")]
    [InlineData("review_scope_closed")]
    public void ArithmeticOrOrdinaryReviewCannotSubstituteForTheRequiredBoundary(string defect)
    {
        var state = Trace(); var c = state.Construction!; var claim = c.Checkpoints[1];
        switch (defect)
        {
            case "changed_plan": claim.PlanHash = "different"; break;
            case "added_task": claim.TaskIds.Add("marker"); break;
            case "root_closed": c.Checkpoints[0].ClosedScopes.Add("/root"); break;
            case "requirement_closed": claim.ClosedRequirementIds!.Add("line"); break;
            case "wrong_owner": c.Checkpoints[0].Step!.BlockId = "other"; break;
            case "invalidated": c.Checkpoints[0].InvalidatedTaskIds.Add("line_total"); break;
            case "global_review": claim.Reviews[0]["purpose"] = "construction_global_judge"; break;
            case "needs_context": claim.Reviews[0]["outcome"] = "needs_context"; break;
            case "draft_echo": state.RequestMeasurements![0].Phase = "construction_draft"; break;
            case "review_scope_closed": claim.Reviews[0]["openScopes"] = new JsonArray(); break;
        }
        Assert.False(ReviewBoundaryQualification.Evidence(state)["observed"]!.GetValue<bool>());
    }

    private static PlanningSession Trace() => new()
    {
        RequestMeasurements = [new() { RequestId = "owned:4:selection", Phase = "construction_discovery" }],
        Construction = new()
        {
            Compact = new() { Blocks = [new() { Id = "line", CompletionCheckpoint = 2 }] },
            Checkpoints =
            [
                new() { Sequence = 1, BaseHash = "empty", PlanHash = "accepted", TaskIds = ["line_total"],
                    Step = new() { BlockId = "line" }, ClosedRequirementIds = [] },
                new() { Sequence = 2, BaseHash = "accepted", PlanHash = "accepted", ClosedRequirementIds = [],
                    Step = new() { BlockId = "line", Kind = "close", Scope = "/root", CloseScopes = [] },
                    Fragment = new() { ["completeBlock"] = true, ["complete"] = false, ["businessReviewPending"] = false,
                        ["tasks"] = new JsonArray(), ["outputs"] = new JsonArray() },
                    Reviews = [new() { ["purpose"] = "construction_local_judge", ["candidateHash"] = "accepted", ["baselineHash"] = "accepted",
                        ["requestId"] = "owned:5:review", ["outcome"] = "ok", ["reviewedTaskIds"] = new JsonArray("line_total"),
                        ["openScopes"] = new JsonArray("/root"), ["claims"] = new JsonObject { ["completeBlock"] = true,
                            ["complete"] = false, ["closedScopes"] = new JsonArray(), ["closedRequirements"] = new JsonArray() } }] }
            ]
        }
    };
}
