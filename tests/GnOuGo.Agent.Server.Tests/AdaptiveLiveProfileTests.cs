using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AdaptiveLiveProfileTests
{
    [Fact]
    public void FreshDeckCraftInspectionCanReadTheFilteredCommandRegistryWhileRetainedGrantsKeepTheirOriginalTools()
    {
        var current = new AdaptiveLiveProfile("deckcraft");
        Assert.Equal(AdaptiveLiveProfile.DeckCraftInspection, current.ProfileVersion);
        Assert.Contains(new("DeckCraft", "list_commands"), current.Tools);
        Assert.DoesNotContain(new("DeckCraft", "run_command"), current.Tools);
        var retained = new AdaptiveLiveProfile("deckcraft", AdaptiveLiveProfile.Version);
        Assert.Equal(AdaptiveLiveProfile.Version, retained.ProfileVersion);
        Assert.Equal("render_slide", Assert.Single(retained.Tools).Method);
    }

    [Theory]
    [InlineData("amazon", "GnOuGo.Browser.Mcp", "browser_get_content")]
    [InlineData(PublishedReviewQualification.Scenario, "GnOuGo.Git.Mcp", "git_compare_refs")]
    public void ExactReviewedStageCanObserveWhileSubstitutionAndCrossRunCallsAreDenied(string scenario, string server, string method)
    {
        var profile = new AdaptiveLiveProfile(scenario);
        var descriptor = new JsonObject { ["server"] = server, ["method"] = method, ["input_schema"] = new JsonObject { ["type"] = "object" } };
        var id = AgentTaskContracts.McpToolId(descriptor);
        var task = new AgentTaskDefinition { Runner = AdaptiveLiveProfile.Runner, Workspace = ".", Objective = "Collect current evidence", Capabilities = [id],
            OutputSchema = new() { ["type"] = "object" }, Budget = new() { MaxActions = 2 },
            Verification = [new("observed", "mcp.result", id, new() { ["type"] = "object" })] };
        var stage = new PlanningNode { Key = "observe", Type = "agent.run",
            Input = PlanningJsonTransport.Literal(JsonSerializer.SerializeToNode(task, AgentTaskJsonContext.Default.AgentTaskDefinition)) };
        profile.BindStage(stage, new() { Kind = "agent", Method = task.Runner, FixedInput = new() { ["runner"] = task.Runner } });
        var step = "/workflow/main/step/n_" + PlanningGraphCompiler.Fingerprint(stage.Key)[..16];
        var call = new McpCorrelationContext { TenantId = "benchmark", RunId = "run", StepId = step, StepType = "agent.run",
            ServerName = server, MethodName = method, Context = new() { ["agent_capability"] = id } };
        profile.RequireCall(call, "run");
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call, "other"));
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call with { StepType = "mcp.call" }, "run"));
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call with { TenantId = "other" }, "run"));
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call with { StepId = "other" }, "run"));
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call with { MethodName = "unapproved" }, "run"));
        Assert.Throws<PlanningConflictException>(() => profile.RequireCall(call with { Context = new() { ["agent_capability"] = "command.execute" } }, "run"));
    }
}
