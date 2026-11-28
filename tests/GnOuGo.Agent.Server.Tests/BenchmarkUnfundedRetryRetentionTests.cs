using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Fact]
    public async Task UnfundedConfirmedRetryRetentionPreservesAllMoneyAndAllowsAnAffordableIndependentRequest()
    {
        var records = new Records(); var campaign = await SeedUnfundedRetryAsync(records);
        var before = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct,
            BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        Assert.NotNull(proposal["evidence"]!["unfunded_retry_at_inspection"]);
        await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        var restarted = new BenchmarkCampaign(records, "retention");
        Assert.Equal(before, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(new() { ClientRequestId = RequestId },
            _ => Task.CompletedTask, _ => throw new Exception("Must not replay"), Ct));
        var fresh = new BenchmarkHttpJournal(restarted, "independent:1:hash", 10, 10, .25m);
        await fresh.PrepareAsync(Ct);
        await fresh.SaveAsync(new() { Attempts = [new() { Id = "independent:1:hash:1" }] }, Ct);
        Assert.Equal(49.75m, (await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(50m, await restarted.CostCeilingAsync(Ct));
    }

    [Theory]
    [InlineData("affordable")]
    [InlineData("body")]
    [InlineData("receipt")]
    [InlineData("status")]
    [InlineData("failure")]
    public async Task UnfundedRetryRetentionRequiresConfirmedErrorAndCurrentFundingProof(string defect)
    {
        var records = new Records(); var campaign = await SeedUnfundedRetryAsync(records);
        if (defect == "affordable") await SaveKnownCostAsync(records, 47m);
        if (defect == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new JsonObject(), Ct);
        if (defect is "body" or "status")
        {
            var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
            journal["transport"]!["Attempts"]![0]![defect == "body" ? "Body" : "Status"] = defect == "body" ? JsonValue.Create("partial") : JsonValue.Create(200);
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        }
        if (defect == "failure") await campaign.SaveAsync("planning-evaluation-failures", RequestId, new JsonObject(), Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection,
            "run:" + Session, RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedUnfundedRetryAsync(Records records)
    {
        var campaign = await SeedCapturedRetriesAsync(records, 1, false);
        await SaveKnownCostAsync(records, 48.5m);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new() { ["stage"] = "dispatch", ["exception_type"] = "InvalidOperationException" }, Ct);
        return campaign;
    }
    private static Task SaveKnownCostAsync(Records records, decimal cost) => records.UpsertAsync("planning-evaluation-budgets", "benchmark", "retention",
        JsonSerializer.Serialize(new LLMUsageBudgetSnapshot { EstimatedCostCurrency = "EUR", EstimatedCost = cost }, PlanningJsonContext.Default.LLMUsageBudgetSnapshot),
        BenchmarkCampaign.Author, Ct);
}
