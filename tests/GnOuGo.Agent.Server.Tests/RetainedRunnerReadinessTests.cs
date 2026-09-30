using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RetainedRunnerReadinessTests
{
    [Fact]
    public void OriginalReceiptsRetainTwoFailuresAndTheIndependentLocationMismatch()
    {
        var retained = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RunnerReadiness", "retained-execution.json")))!;
        Assert.Equal("default", retained["tenantId"]!.ToString());
        Assert.NotEmpty(retained["runId"]!.ToString());
        var invocations = retained["invocations"]!.AsArray();
        var work = invocations.Single(i => i!["stepType"]!.ToString() == "agent.run" && !i["isFinalization"]!.GetValue<bool>())!;
        var cleanup = invocations.Single(i => i!["isFinalization"]!.GetValue<bool>())!;
        var clone = invocations.Single(i => i!["stepType"]!.ToString() == "mcp.call")!;
        var observation = JsonSerializer.Deserialize(work["observation"], AgentTaskJsonContext.Default.AgentTaskResult)!;
        Assert.Equal("failed", observation.Status); Assert.Null(observation.Failure); // Original receipt stays historical.
        Assert.Equal(0, observation.Usage.ModelCalls); Assert.Equal(0, observation.Usage.TotalTokens);
        Assert.Contains("mandatory host sandbox", observation.Message);
        Assert.Equal("AGENT_TASK_FAILED", work["error"]!["code"]!.ToString());
        Assert.Equal("INPUT_VALIDATION", cleanup["error"]!["code"]!.ToString());
        Assert.Contains("tenant and run identities", cleanup["error"]!["message"]!.ToString());
        Assert.Equal("rejected_before_dispatch", cleanup["observation"]!["status"]!.ToString());
        var location = clone["output"]!["response"]!["projectRootRelative"]!.ToString();
        Assert.Equal(location, work["resolvedInput"]!["workspace"]!.ToString());
        Assert.NotEqual(location, cleanup["resolvedInput"]!["inputs"]!["targetDirectory"]!.ToString());
        Assert.EndsWith("pr-610", location);
        Assert.EndsWith("pr-606", cleanup["resolvedInput"]!["inputs"]!["targetDirectory"]!.ToString());
    }
}
