using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CompactConstructionHostTests
{
    private const string Objective = "Return the required public numeric result named result with value 42.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshCompactBackgroundPipelineRestoresEncryptedEvidenceWithoutApprovalOrEffects()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var effects = 0;
        var catalog = PlanningSessionLifecycleTests.AgentCatalog().OnTool("unused_write", (_, _) =>
        { effects++; throw new InvalidOperationException("Construction must not execute effects."); }).WithTool("unused_write", "An unrelated write operation",
            JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""));
        using var model = new JournaledPlanningModel(new CurrentPlanningFixture.LiteralModel());
        PlanningSession state;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model))
        {
            await service.StartAsync(Ct);
            try
            {
                state = await service.StartAsync("compact-host", Objective, false, Ct);
                Assert.Equal(11, state.SchemaVersion);
                Assert.Equal(64, state.Request.MaxModelCalls);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                do
                {
                    state = (await service.GetAsync(state.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) break;
                    await Task.Delay(25, deadline.Token);
                } while (true);
                Assert.True(state.Status == PlanningStatus.FinalReview, $"{state.Status}: {string.Join(';', state.Diagnostics)}");
            }
            finally { await service.StopAsync(Ct); }
        }
        Assert.Equal(new[] { "construction_intent", "construction_select", "construction_draft", "construction_global_judge" }, model.Requests.Select(request => request.StructuredOutputSchema!["title"]!.ToString()));
        Assert.Equal(4, state.ModelCalls);
        Assert.Equal(0, effects);
        Assert.Null(state.ApprovedHash); Assert.Null(state.SavedAgentId);
        Assert.True(state.Construction!.Complete);
        Assert.True(state.Construction.Compact!.Sources.All(source => source.Indexed));
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Single(state.Construction.Checkpoints);
        Assert.Null(state.Construction.Candidate);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal(4, state.RequestMeasurements!.Count);
        Assert.All(state.RequestMeasurements, m =>
        {
            Assert.True(m.EstimatedInputTokens <= m.InputLimit);
            Assert.Equal(m.CompleteRequestBytes, m.Components.Sum(c => c.SerializedBytes));
            Assert.Equal("admitted", m.Admission);
        });
        var stored = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement),
            JsonSerializer.Serialize(stored.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement));
        Assert.Equal(state.ComputeArtifactHash(), stored.ComputeArtifactHash());
        Assert.Null(await fixture.Store.LoadAsync("another-tenant", state.Request.SessionId, Ct));
        var receipts = await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct);
        Assert.Equal(4, receipts.Count);
        using var reopened = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model);
        await reopened.StartAsync(Ct);
        try
        {
            var recovered = (await reopened.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Equal(state.Revision, recovered.Revision);
            Assert.Equal(4, model.Requests.Count);
            Assert.Equal(0, effects);
            Assert.Equal(state.ComputeArtifactHash(), recovered.ComputeArtifactHash());
            foreach (var receipt in receipts)
                Assert.Equal(receipt.Value, (await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests", receipt.Key, EfPlanningSessionStore.Author, Ct))!.Value);
        }
        finally { await reopened.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tasks")]
    public async Task CompactJournalRejectsUnboundPhaseBeforeReservationOrDispatch(string? title)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planning = new PlanningRequest { TenantId = "tenant", SessionId = "compact-title" };
        var schema = JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")!.AsObject();
        if (title is not null) schema["title"] = title;
        var request = PlanningGenerationPolicy.Apply(new() { Prompt = "Bounded request", StructuredOutputSchema = schema }, planning.Generation);
        request.ClientRequestId = planning.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        using var model = new JournaledPlanningModel(new CurrentPlanningFixture.LiteralModel());
        var journal = new PlanningModelJournal(model, fixture, fixture.Records, "tenant", planning.SessionId,
            new LLMUsageBudgetScope(new() { MaxCalls = 64 }), new Price(), planning.Generation, planning: planning);
        await Assert.ThrowsAsync<PlanningConflictException>(() => journal.CallAsync(request, Ct));
        Assert.Empty(model.Requests);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Empty(await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "tenant", EfPlanningSessionStore.Author, Ct));
    }

    private sealed class Price : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => .001m;
    }

}
