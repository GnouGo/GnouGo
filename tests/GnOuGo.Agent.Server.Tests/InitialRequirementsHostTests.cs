using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class InitialRequirementsHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedVersionOneBackgroundOwnerCorrectsOrStopsOnceAndRestartDoesNotRedispatch(bool repeatInvalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new InitialClient(repeatInvalid);
        var expectedCalls = repeatInvalid ? 2 : 3;
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        var initial = await RetainedVersionOneConstruction.SeedAsync(fixture, agents, client, "initial-requirements", "Return a literal result", Ct);
        PlanningSession result;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client))
        {
            await service.StartAsync(Ct);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    result = (await service.GetAsync(initial.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(result.Status) || PlanningStatus.IsTerminal(result.Status)) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.Equal(repeatInvalid ? PlanningStatus.Stopped : PlanningStatus.FinalReview, result.Status);
                Assert.Equal(expectedCalls, client.Requests.Count); Assert.Equal(expectedCalls, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
                Assert.Null(result.ApprovedHash); Assert.Null(result.SavedAgentId); Assert.Null(result.PendingCall);
                Assert.Equal(expectedCalls, result.Usage!.Calls); Assert.Equal(expectedCalls * 15, result.Usage.TotalTokens);
                for (var i = 0; i < 3; i++)
                    Assert.Equal(result.Revision, (await service.GetAsync(initial.Request.SessionId, Ct))!.Revision);
                Assert.Equal(expectedCalls, client.Requests.Count);
                if (repeatInvalid)
                {
                    Assert.Null(result.Requirements); Assert.Null(result.Plan); Assert.Null(result.Yaml);
                    Assert.Contains(result.Diagnostics, d => d.Code is "REQUIREMENTS_INVALID" or "REPLAN_NO_PROGRESS");
                }
                else PlanningArtifactApproval.Verify(result);
            }
            finally { await service.StopAsync(Ct); }
        }
        var snapshot = JsonSerializer.Serialize(result, PlanningJsonContext.Default.PlanningSession);
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client);
        await restarted.StartAsync(Ct);
        try
        {
            Assert.Equal(snapshot, JsonSerializer.Serialize(await restarted.GetAsync(result.Request.SessionId, Ct), PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(expectedCalls, client.Requests.Count);
        }
        finally { await restarted.StopAsync(Ct); }
        Assert.Equal(expectedCalls, client.Requests.Count);
        Assert.Equal(expectedCalls, (await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
        Assert.Null(await fixture.Store.LoadAsync("other-tenant", result.Request.SessionId, Ct));
    }

    private sealed class InitialClient(bool repeatInvalid) : ILLMClient
    {
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); Assert.InRange(Requests.Count, 1, repeatInvalid ? 2 : 3);
            var proposal = new PlanningProposal { Plan = PlanningCorpus.LiteralResult(), Requirements = PlanningCorpus.Requirements("local") };
            proposal.Requirements!.Inputs = [];
            proposal.Requirements.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
            var response = PlanningCorpus.OneDraftResponse(request, proposal)!;
            if (Requests.Count == 1 || repeatInvalid) response["requirements"] = null;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
            if (Requests.Count == 2) Assert.Contains("REQUIREMENTS_INVALID", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
