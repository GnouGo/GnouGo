using System.Text.Json;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Agent.Server.Tests.Planning;
public sealed class AgentPlanningPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedRequestKeepsBusinessIntentAndHostApprovalWithoutCompilerInstructions(bool capturedTransport)
    {
        var policy = AgentPlanningPolicy.Create();
        var actual = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var proposal = new PlanningProposal
        {
            Plan = PlanningCorpus.Greeting("name: original\n"),
            Requirements = new() { Summary = "Return the requested document", Inputs = [],
                Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }], Outcomes = [new("document", "Return the requested document")] }
        };
        var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, proposal);
        runtime.Patch = request => PlanningCorpus.OneDraftResponse(request, proposal)!.AsObject();
        const string intent = "Return the explicitly requested YAML document, preserving its original content.";
        var state = new PlanningSession { Request = new() { TenantId = "fixture", Prompt = intent, Policy = policy } };
        if (capturedTransport) PlanningTransportPolicies.Capture(state.Request);
        state = RetainedVersionOneConstruction.Restore(state);
        for (var i = 0; i < 4 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, runtime.Requests.Count);
        var request = runtime.Requests[0];
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var legacyHostRequest = JsonSerializer.Deserialize(exact, PlanningJsonContext.Default.LLMRequest)!;
        PlanningGenerationPolicy.Apply(legacyHostRequest, state.Request.Generation);
        Assert.Equal(exact, JsonSerializer.Serialize(legacyHostRequest, PlanningJsonContext.Default.LLMRequest));
        Assert.True(request.DisableTransportRetries);
        Assert.True(request.UseBackgroundMode);
        Assert.Null(request.RequiredReviewCalls);
        Assert.DoesNotContain("requiredReviewCalls", exact, StringComparison.Ordinal);
        Assert.Null(PlanningTransportPolicies.Read(state.Request));
        Assert.Contains(intent, request.Prompt);
        Assert.Contains("The host manages approval of the generated plan before execution", request.Prompt);
        Assert.Contains("do not add that approval as a business task", request.Prompt);
        Assert.Contains("No extra effects/artifacts", request.Prompt);
        Assert.DoesNotContain("YAML", policy.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow:", request.Prompt[..request.Prompt.IndexOf("{\"request\"", StringComparison.Ordinal)], StringComparison.OrdinalIgnoreCase);
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
