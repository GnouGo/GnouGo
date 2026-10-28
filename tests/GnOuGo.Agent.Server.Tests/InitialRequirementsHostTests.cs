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
    public async Task CurrentBackgroundOwnerCorrectsOriginalCoverageOrStopsWithoutRedispatch(bool repeatInvalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        using var client = new JournaledPlanningModel(new InitialClient(repeatInvalid));
        var expectedCalls = repeatInvalid ? 2 : 6;
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        PlanningSession initial;
        using (var creation = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client, settings: new() { BackgroundProcessingEnabled = false }))
            initial = await creation.StartAsync("initial-requirements", "Return the required public numeric result named result with value 42.", false, Ct);
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
                    Assert.Contains(result.Diagnostics, d => d.Code is "CONSTRUCTION_SOURCE_COVERAGE" or "REPLAN_NO_PROGRESS");
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
        private readonly CurrentPlanningFixture.LiteralModel _literal = new();
        private int _indexCalls;
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var response = await _literal.CallAsync(request, ct);
            if (request.StructuredOutputSchema?["properties"]?["clauses"] is not null && (++_indexCalls == 1 || repeatInvalid))
                response.Json!["clauses"]![0]!["text"] = "Omitted exact original evidence";
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json!, request.StructuredOutputSchema!));
            return response;
        }
    }
}
