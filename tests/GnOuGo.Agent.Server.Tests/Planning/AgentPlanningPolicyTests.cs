using GnOuGo.Agent.Server.Planning;
namespace GnOuGo.Agent.Server.Tests.Planning;
public sealed class AgentPlanningPolicyTests
{
    [Fact] public void HostPolicyRequiresConfirmationAndExcludesRecursivePlanning()
    {
        var policy = AgentPlanningPolicy.Create();
        Assert.True(policy.RequireExternalConfirmation);
        Assert.DoesNotContain("workflow.plan", policy.AllowedStepTypes);
        Assert.DoesNotContain("workflow.execute", policy.AllowedStepTypes);
        Assert.Contains("mcp.call", policy.AllowedStepTypes);
    }
}
