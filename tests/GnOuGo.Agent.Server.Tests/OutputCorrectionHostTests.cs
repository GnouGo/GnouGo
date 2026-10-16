using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class OutputCorrectionHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundOwnerCorrectsOrStopsOnceAndRestartDoesNotRedispatch(bool repeatInvalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new OutputClient(repeatInvalid);
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        PlanningSession result;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client))
        {
            await service.StartAsync(Ct);
            try
            {
                var initial = await service.StartAsync("output-correction", "Return only the requested result", false, Ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    result = (await service.GetAsync(initial.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(result.Status) || PlanningStatus.IsTerminal(result.Status)) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.True(result.Status == (repeatInvalid ? PlanningStatus.Stopped : PlanningStatus.FinalReview), string.Join(';', result.Diagnostics));
                Assert.True(client.Requests.Count == 2, string.Join(';', result.Diagnostics));
                Assert.Equal(2, client.Requests.Count); Assert.Equal(2, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
                Assert.Null(result.ApprovedHash); Assert.Null(result.SavedAgentId); Assert.Null(result.PendingCall);
                Assert.Equal(2, result.Usage!.Calls); Assert.Equal(30, result.Usage.TotalTokens);
                for (var i = 0; i < 3; i++)
                    Assert.Equal(result.Revision, (await service.GetAsync(initial.Request.SessionId, Ct))!.Revision);
                Assert.Equal(2, client.Requests.Count);
                if (repeatInvalid)
                {
                    Assert.Null(result.Yaml);
                    Assert.Contains(result.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
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

    private sealed class OutputClient(bool repeatInvalid) : ILLMClient
    {
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); Assert.InRange(Requests.Count, 1, 2);
            var plan = PlanningCorpus.LiteralResult();
            if (Requests.Count == 1 || repeatInvalid) plan.Root.Outputs.Add(new("extra", new() { Kind = "string", Text = "unrequested" }));
            var proposal = new PlanningProposal { Plan = plan, Requirements = new()
            {
                Summary = "Return only the requested result", Inputs = [], Outcomes = [new("result", "Return the requested number")],
                Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }]
            } };
            var response = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            if (Requests.Count == 2)
            {
                Assert.Contains("Remove only the diagnosed unaccepted exports", request.Prompt);
                Assert.Contains("Preserve all operation arguments verbatim", request.Prompt);
            }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema));
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
