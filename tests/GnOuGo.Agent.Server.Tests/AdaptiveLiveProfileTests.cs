using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AdaptiveLiveProfileTests
{
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
            ServerName = AdaptiveLiveProfile.Backend, MethodName = "copilot_task_run" };
        var context = new AgentTaskContext("benchmark", "run", step, task);
        var args = new JsonObject { ["contextJson"] = JsonSerializer.Serialize(context, AgentTaskJsonContext.Default.AgentTaskContext),
            ["toolsJson"] = new JsonObject { [id] = descriptor }.ToJsonString() };
        Assert.Throws<PlanningConflictException>(() => profile.Require("copilot_task_run", args));
        Assert.Throws<PlanningConflictException>(() => profile.Enter(call, "other"));
        Assert.Throws<PlanningConflictException>(() => profile.Enter(call with { StepType = "mcp.call" }, "run"));
        using (profile.Enter(call, "run"))
        {
            profile.Require("copilot_task_run", args);
            descriptor["method"] = "unapproved";
            var changed = args.DeepClone();
            changed["toolsJson"] = new JsonObject { [id] = descriptor.DeepClone() }.ToJsonString();
            Assert.Throws<PlanningConflictException>(() => profile.Require("copilot_task_run", changed));
            args["contextJson"] = JsonSerializer.Serialize(context with { Task = task with { Capabilities = ["command.execute"] } }, AgentTaskJsonContext.Default.AgentTaskContext);
            Assert.Throws<PlanningConflictException>(() => profile.Require("copilot_task_run", args));
        }
        Assert.Throws<PlanningConflictException>(() => profile.Require("copilot_task_run", args));
    }
}
