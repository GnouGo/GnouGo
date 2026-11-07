using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ServerLiveFundingEvidenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedTariffsAndUnresolvedRequestsRemainReadOnlyEvidence(bool writePrice, bool pending)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var options = new LLMOptions { DefaultProvider = "gateway", DefaultModel = "private-deployment",
            Models = { ["gateway"] = new() { Type = "openai" } },
            ModelOverrides = new() { ["openai/private-deployment"] = new() { Id = "private-deployment", ProviderType = "openai",
                ContextWindowTokens = 128000, MaxInputTokens = 120000, MaxOutputTokens = 8000,
                Capabilities = new() { SupportsStructuredOutput = true }, Pricing = new() { Currency = "USD", InputPer1MTokens = 30,
                    OutputPer1MTokens = 180, CacheWriteInputPer1MTokens = writePrice ? 30 : null } } } };
        var state = new PlanningSession { Request = new() { TenantId = "owner", SessionId = "financial", Prompt = "Return a record" }, ModelCalls = pending ? 2 : 1 };
        CapturedPlanningModel.Capture(state.Request, options);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
        await using var initialDb = fixture.CreateDbContext();
        var payloadKey = (await initialDb.Sessions.AsNoTracking().SingleAsync(ct)).PayloadKey;
        var original = (await fixture.Records.GetAsync(EfPlanningSessionStore.Collection, "owner", payloadKey, EfPlanningSessionStore.Author, ct))!.Value;
        await using (var db = fixture.CreateDbContext())
        {
            for (var number = 1; number <= state.ModelCalls; number++)
            {
                var id = state.Request.SessionId + ":" + number; var key = state.Request.SessionId + ":" + id;
                var request = new LLMRequest { ClientRequestId = id, Provider = "gateway", Model = "private-deployment", Prompt = "PRIVATE_TEST_PROMPT" };
                db.Calls.Add(new() { TenantId = "owner", SessionId = "financial", RequestHash = id, PayloadKey = key, Status = number == 1 ? "completed" : "reserved" });
                await fixture.Records.UpsertAsync(PlanningModelJournal.RequestCollection, "owner", key,
                    JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), EfPlanningSessionStore.Author, ct);
                if (number == 1)
                {
                    var response = new LLMResponse { Usage = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 100,
                        ["input_tokens_details"] = new JsonObject { ["cache_write_tokens"] = 500 },
                        ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 99 } } };
                    await fixture.Records.UpsertAsync(PlanningModelJournal.Collection, "owner", key,
                        JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), EfPlanningSessionStore.Author, ct);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        var evidence = await ServerLiveFundingEvidence.ReadAsync(fixture.Records, fixture, "owner", "financial", ct);
        Assert.Equal(writePrice && !pending, evidence["cost_complete"]!.GetValue<bool>());
        Assert.Equal(pending ? 1 : 0, evidence["unresolved_requests"]!.GetValue<int>());
        Assert.Equal(100, evidence["measured_output_tokens_including_reasoning"]!.GetValue<long>());
        Assert.Equal(writePrice ? .048m : 0m, evidence["partially_priced_cost"]!.GetValue<decimal>());
        if (!writePrice) Assert.Equal(1, evidence["unavailable_reasons"]!["cache_write_tariff_unavailable"]!.GetValue<int>());
        Assert.False(evidence["historical_accounting_modified"]!.GetValue<bool>());
        Assert.Equal(0, evidence["model_dispatches_by_inspection"]!.GetValue<int>());
        Assert.DoesNotContain("PRIVATE_TEST_PROMPT", evidence.ToJsonString());
        Assert.Equal(original, (await fixture.Records.GetAsync(EfPlanningSessionStore.Collection, "owner", payloadKey, EfPlanningSessionStore.Author, ct))!.Value);
        await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveFundingEvidence.ReadAsync(fixture.Records, fixture, "another-owner", "financial", ct));
    }
}
