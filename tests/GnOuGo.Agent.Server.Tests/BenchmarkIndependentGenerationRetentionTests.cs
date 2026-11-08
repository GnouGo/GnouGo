using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Theory]
    [InlineData(null)] [InlineData("marker")] [InlineData("usage")] [InlineData("completion")]
    public async Task IndependentGenerationRetentionBindsBothUnknownAttemptsWithoutReleasingEitherHold(string? tamper)
    {
        var records = new Records(); var campaign = await SeedCapturedRetriesAsync(records, 2, true);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        var capture = JsonSerializer.SerializeToNode(new PlanningTransportPolicy { Version = 2, MaxUncertainRetries = 1,
            IndependentGenerationMaxElapsedMilliseconds = 1_260_000 }, PlanningJsonContext.Default.PlanningTransportPolicy)!;
        run["session"]!["request"]!["options"]![PlanningTransportPolicies.OptionName] = capture.DeepClone();
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, run, Ct);
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy[PlanningTransportPolicies.OptionName] = capture.DeepClone();
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var effective = journal["effective_transport_policy"]!;
        effective["planning_transport_policy"] = capture.DeepClone(); effective["effective_retry_policy"]!["MaxUncertainRetries"] = 1;
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(effective.ToJsonString());
        var first = journal["transport"]!["Attempts"]![0]!;
        first["Status"] = null; first["Body"] = null; first["Failure"] = "transport";
        first["StartedAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-2); first["CompletedAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-1);
        first["RetryDecision"] = "abandoned; independent tool-free generation retry";
        first["PartialBodyBase64"] = Convert.ToBase64String("data: retained partial bytes"u8);
        if (tamper == "marker") first["RetryDecision"] = "blocked; uncertain outcome";
        if (tamper == "usage") first["UsageKnown"] = true;
        if (tamper == "completion") first["AssembledBody"] = "received completion";
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var original = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        if (tamper is not null)
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
                SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        else
        {
            var preview = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session,
                RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
            await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
                preview["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RequireRunNotRetainedAsync(Session, Ct));
        }
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
    }
}
