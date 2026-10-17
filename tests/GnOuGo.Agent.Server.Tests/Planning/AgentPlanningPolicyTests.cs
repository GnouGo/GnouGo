using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Agent.Server.Tests.Planning;
public sealed class AgentPlanningPolicyTests
{
    [Fact]
    public async Task IssuedRequestKeepsBusinessIntentAndHostApprovalWithoutCompilerInstructions()
    {
        var policy = AgentPlanningPolicy.Create();
        var actual = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var proposal = new PlanningProposal
        {
            Plan = PlanningCorpus.Greeting("name: original\n"),
            Requirements = new() { Summary = "Return the requested document", Outcomes = [new("document", "Return the requested document")] }
        };
        var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, proposal);
        const string intent = "Return the explicitly requested YAML document, preserving its original content.";
        var state = new PlanningSession { Request = new() { TenantId = "fixture", Prompt = intent, Policy = policy } };
        for (var i = 0; i < 4 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        var request = Assert.Single(runtime.Requests);
        Assert.Contains(intent, request.Prompt);
        Assert.Contains("The host manages approval of the generated plan before execution", request.Prompt);
        Assert.Contains("do not add that approval as a business task", request.Prompt);
        Assert.Contains("No unrequested actions, artifacts", request.Prompt);
        Assert.DoesNotContain("YAML", policy.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("YAML", request.Prompt[..request.Prompt.IndexOf("{\"request\"", StringComparison.Ordinal)], StringComparison.OrdinalIgnoreCase);
        Assert.Null(state.ApprovedHash);
        Assert.Single(state.Plan!.Root.Tasks);
        Assert.Equal("name: original\n", state.Plan.Root.Tasks[0].Outputs[0].Value.Text);
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
