using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnstartedServerRequestIncludesItsSeparateReservationInFreshAdmission(bool changeBudget)
    {
        var records = new Records();
        var campaign = await SeedUnstartedPlanningAsync(records);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        const string runKey = "fresh-server-hold";
        run["run"] = runKey;
        run["campaign"] = campaign.Id;
        run["host"] = "GnOuGo.Agent.Server";
        run["session_id"] = Session;
        var hold = new JsonObject { ["inputTokenCeiling"] = 300, ["outputTokenCeiling"] = 100,
            ["verifiedUsage"] = null, ["reservedEstimatedCost"] = .95m };
        var budget = new JsonObject { ["estimatedCostCurrency"] = "EUR", ["uncertainCalls"] = 1,
            ["journaledAttempts"] = new JsonObject { [RequestId + ":attempt:1"] = hold } };
        run["session"]!["usage"] = budget.DeepClone();
        await campaign.SaveAsync("planning-evaluation-runs", runKey, run, Ct);
        await records.UpsertAsync("agent-planning-budgets-v11", "benchmark", Session, budget.ToJsonString(), BenchmarkCampaign.Author, Ct);
        var kind = BenchmarkCampaign.UnstartedPlanningRetention;
        var preview = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", runKey, RequestId, Ct, kind);
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", runKey, RequestId, preview["evidence_hash"]!.ToString(), Operator, Authorization, Ct, kind);
        var cold = new BenchmarkCampaign(records, campaign.Id);
        if (changeBudget)
        {
            hold["reservedEstimatedCost"] = .2m;
            await records.UpsertAsync("agent-planning-budgets-v11", "benchmark", Session, budget.ToJsonString(), BenchmarkCampaign.Author, Ct);
            await Assert.ThrowsAsync<WorkflowRuntimeException>(() => cold.RetainedApplicationReservationsAsync(Ct));
            return;
        }
        var originals = records.Snapshot();
        Assert.Equal(.95m, await cold.RetainedApplicationReservationsAsync(Ct));
        await cold.RequireRetainedApplicationHoldsAsync(.95m, Ct, requireComplete: true);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => cold.RequireRetainedApplicationHoldsAsync(0m, Ct, requireComplete: true));
        Assert.Equal(0m, (await BenchmarkHttpJournal.AccountingAsync(cold, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(originals, records.Snapshot());
    }
}
