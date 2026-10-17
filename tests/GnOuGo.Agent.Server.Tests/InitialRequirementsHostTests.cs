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
    public async Task BackgroundOwnerCorrectsOrStopsOnceAndRestartDoesNotRedispatch(bool repeatInvalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new InitialClient(repeatInvalid);
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        PlanningSession result;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client))
        {
            await service.StartAsync(Ct);
            try
            {
                var initial = await service.StartAsync("initial-requirements", "Return a literal result", false, Ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    result = (await service.GetAsync(initial.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(result.Status) || PlanningStatus.IsTerminal(result.Status)) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.Equal(repeatInvalid ? PlanningStatus.Stopped : PlanningStatus.FinalReview, result.Status);
                Assert.Equal(2, client.Requests.Count); Assert.Equal(2, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
                Assert.Null(result.ApprovedHash); Assert.Null(result.SavedAgentId); Assert.Null(result.PendingCall);
                Assert.Equal(2, result.Usage!.Calls); Assert.Equal(30, result.Usage.TotalTokens);
                for (var i = 0; i < 3; i++)
                    Assert.Equal(result.Revision, (await service.GetAsync(initial.Request.SessionId, Ct))!.Revision);
                Assert.Equal(2, client.Requests.Count);
                if (repeatInvalid)
                {
                    Assert.Null(result.Requirements); Assert.Null(result.Plan); Assert.Null(result.Yaml);
                    Assert.Contains(result.Diagnostics, d => d.Code == "REQUIREMENTS_INVALID" && d.Message.Contains("first TaskPlan", StringComparison.Ordinal));
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
            Assert.Equal(2, client.Requests.Count);
        }
        finally { await restarted.StopAsync(Ct); }
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(2, (await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
        Assert.Null(await fixture.Store.LoadAsync("other-tenant", result.Request.SessionId, Ct));
    }

    private sealed class InitialClient(bool repeatInvalid) : ILLMClient
    {
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); Assert.InRange(Requests.Count, 1, 2);
            var proposal = new PlanningProposal { Plan = PlanningCorpus.LiteralResult(), Requirements = PlanningCorpus.Requirements("local") };
            var response = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            if (Requests.Count == 1 || repeatInvalid) response["requirements"] = null;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema));
            if (Requests.Count == 2) Assert.Contains("REQUIREMENTS_INVALID", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
