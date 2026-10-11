using GnOuGo.Agent.Server.Planning;
namespace GnOuGo.Agent.Server.Tests.Planning;
public sealed class AgentPlanningPolicyTests
{
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
