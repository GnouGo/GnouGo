using System.Text.Json;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Agent.Server.Tests.Planning;
public sealed class AgentPlanningPolicyTests
{
    [Fact]
    public async Task CurrentRequestsPreserveCapturedTransportAndSeparateArtifactApproval()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("fixture", "host-policy", AgentPlanningPolicy.Create(), TestContext.Current.CancellationToken);
        Assert.Equal(11, state.SchemaVersion);
        Assert.NotNull(PlanningTransportPolicies.Read(state.Request));
        Assert.NotNull(PlanningConstructionPhasePolicies.Read(state.Request));
        Assert.Equal(5, state.ModelCalls);
        Assert.Null(state.ApprovedHash);
        Assert.Single(state.Plan!.Root.Tasks);
        Assert.Equal(42, state.Plan.Root.Tasks[0].Outputs[0].Value.Number);
        Assert.DoesNotContain("YAML", state.Request.Policy.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(state.Plan.Root.Tasks, task => task.Operation == "workflow.execute" || task.Operation == "workflow.plan");
        PlanningArtifactApproval.Verify(state);
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Equal(exact, JsonSerializer.Serialize(PlanningSessionStorage.Read(exact, "fixture", "host-policy"), PlanningJsonContext.Default.PlanningSession));
    }

    [Fact] public void HostPolicyOmitsRedundantConfirmationButAllowsExplicitOptIn()
    {
        var policy = AgentPlanningPolicy.Create();
        Assert.False(policy.RequireExternalConfirmation);
        policy.RequireExternalConfirmation = true; Assert.True(policy.RequireExternalConfirmation);
        Assert.DoesNotContain("workflow.plan", policy.AllowedStepTypes);
        Assert.DoesNotContain("workflow.execute", policy.AllowedStepTypes);
        Assert.Contains("mcp.call", policy.AllowedStepTypes);
    }
}
