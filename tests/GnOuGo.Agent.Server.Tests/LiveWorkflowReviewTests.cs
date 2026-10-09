using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveWorkflowReviewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void LivePromptRetainsBusinessConstraintsWithoutInjectingNoticeHandling(int productLimit)
    {
        const string relative = "workflows/campaign/fresh-run";
        var prompt = LiveWorkflowEvaluation.AmazonEvaluationPrompt(relative, productLimit);
        Assert.StartsWith(LiveWorkflowEvaluation.AmazonPrompt, prompt, StringComparison.Ordinal);
        Assert.Contains("au maximum les " + productLimit + " premiers produits", prompt, StringComparison.Ordinal);
        Assert.Contains(relative + "/products.xlsx", prompt, StringComparison.Ordinal);
        Assert.Contains("query", prompt, StringComparison.Ordinal);
        Assert.Contains("Ferme le navigateur même en cas d’échec", prompt, StringComparison.Ordinal);
        Assert.Contains("jamais inventés", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consent", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CAPTCHA", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stale_revision")]
    [InlineData("stale_artifact")]
    [InlineData("already_started")]
    [InlineData("tenant")]
    [InlineData("session")]
    [InlineData("intent_change")]
    public async Task LiveCorrectionRetainsRequirementsWithoutApprovingOrResettingBudgets(string variant)
    {
        var ct = TestContext.Current.CancellationToken; var checkpoints = new List<PlanningSession>();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (s, _) => { checkpoints.Add(s); return Task.CompletedTask; });
        var state = new PlanningSession { IntentVersion = 2, Revision = 3, ModelCalls = 2, ReplanAttempts = 1,
            Request = new() { TenantId = "benchmark", SessionId = "fresh-correction", Name = "Correction fixture", Prompt = "Return a value" },
            Status = PlanningStatus.FinalReview, Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult() };
        state.Catalog = await runtime.DiscoverAsync(state.Request, ct);
        state.Graph = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Graph!;
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name);
        var command = new PlanningCommand { Kind = "revise", PreserveRequirements = true, ExpectedRevision = state.Revision,
            ArtifactHash = state.ComputeArtifactHash(), Text = "Bind the actual requested input; preserve the accepted intent." };
        if (variant == "stale_revision") command.ExpectedRevision--;
        if (variant == "stale_artifact") command.ArtifactHash = "old";
        if (variant == "tenant") state.Request.TenantId = "someone-else";
        if (variant == "session") state.Request.SessionId = "another-run";
        if (variant == "intent_change") command.PreserveRequirements = false;
        var run = new JsonObject { ["label"] = "fresh-correction", ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) };
        if (variant == "already_started") run["execution_started"] = "2026-10-04T00:00:00Z";
        var before = run.ToJsonString();
        if (variant == "valid")
        {
            var revised = await LiveWorkflowEvaluation.ReviseAsync(run, command, runtime, ct);
            Assert.Equal(PlanningStatus.Generating, revised.Status); Assert.Null(revised.ApprovedHash); Assert.Null(revised.Yaml);
            Assert.Equal(2, revised.ModelCalls); Assert.Equal(1, revised.ReplanAttempts); Assert.NotNull(revised.Request.Baseline);
            Assert.Equal(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements), JsonSerializer.Serialize(revised.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            Assert.Single(checkpoints);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => LiveWorkflowEvaluation.ReviseAsync(run, command, runtime, ct));
            Assert.Empty(checkpoints);
        }
        Assert.Equal(before, run.ToJsonString());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("stale_revision")]
    [InlineData("stale_artifact")]
    [InlineData("not_approval")]
    [InlineData("already_started")]
    public async Task LiveApprovalUsesExplicitCommandAndNeverReplaysStartedExecution(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpoints = new List<PlanningSession>();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (s, _) => { checkpoints.Add(s); return Task.CompletedTask; });
        var state = new PlanningSession { IntentVersion = 2, Revision = 3,
            Request = new() { TenantId = "benchmark", SessionId = "fresh-run", Name = "Review fixture", Prompt = "Return a value" },
            Status = PlanningStatus.FinalReview, Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult() };
        state.Catalog = await runtime.DiscoverAsync(state.Request, ct);
        state.Graph = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Graph!;
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name);
        var run = new JsonObject { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) };
        var id = Assert.Single(state.Requirements.Outcomes).Id;
        var command = new PlanningCommand { Kind = "approve", ExpectedRevision = state.Revision,
            ArtifactHash = state.ComputeArtifactHash(), ReviewedRequirementIds = [id] };
        switch (variant)
        {
            case "missing": command.ReviewedRequirementIds = null; break;
            case "duplicate": command.ReviewedRequirementIds = [id, id]; break;
            case "unknown": command.ReviewedRequirementIds = ["unaccepted"]; break;
            case "stale_revision": command.ExpectedRevision--; break;
            case "stale_artifact": command.ArtifactHash = "another-artifact"; break;
            case "not_approval": command.Kind = "advance"; break;
            case "already_started": run["execution_started"] = "2026-10-04T00:00:00Z"; break;
        }
        // Exercise the same source-generated command serialization used by --review-command.
        var json = JsonSerializer.Serialize(command, PlanningJsonContext.Default.PlanningCommand);
        command = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningCommand)!;
        var before = run.ToJsonString();
        if (variant == "valid")
        {
            var approved = await LiveWorkflowEvaluation.ApproveAsync(run, command, runtime, ct);
            Assert.Equal(PlanningStatus.Approved, approved.Status);
            Assert.Equal(command.ArtifactHash, approved.ApprovedHash);
            Assert.Equal("requirement:" + id, Assert.Single(approved.ValidationResults, v => v.Outcome == "human_reviewed").Id);
            Assert.Single(checkpoints);
        }
        else
        {
            if (variant == "not_approval")
                await Assert.ThrowsAsync<ArgumentException>(() => LiveWorkflowEvaluation.ApproveAsync(run, command, runtime, ct));
            else if (variant == "already_started")
                await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowEvaluation.ApproveAsync(run, command, runtime, ct));
            else
                await Assert.ThrowsAsync<PlanningConflictException>(() => LiveWorkflowEvaluation.ApproveAsync(run, command, runtime, ct));
            Assert.Empty(checkpoints);
        }
        Assert.Equal(before, run.ToJsonString());
        Assert.Equal(json, JsonSerializer.Serialize(command, PlanningJsonContext.Default.PlanningCommand));
    }
}
