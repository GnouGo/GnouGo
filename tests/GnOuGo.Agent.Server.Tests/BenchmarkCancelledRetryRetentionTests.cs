using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("marker")]
    [InlineData("version")]
    [InlineData("allowance")]
    [InlineData("usage")]
    [InlineData("protocol")]
    [InlineData("assembled")]
    [InlineData("body")]
    [InlineData("cancelled")]
    [InlineData("missing_completed")]
    [InlineData("negative_elapsed")]
    [InlineData("overlapping")]
    public async Task CancelledAfterIndependentRetryRetainsBothHoldsWithoutReplay(string? defect)
    {
        var records = new Records();
        var campaign = await SeedCancelledNativePartialAsync(records, "ChatCompletions", "text/event-stream");
        var run = (await campaign.LoadAsync("planning-evaluation-runs", CancelledRun, Ct))!;
        var capture = run["session"]!["request"]!["options"]![PlanningTransportPolicies.OptionName]!;
        capture["version"] = defect == "version" ? 1 : 2;
        capture["maxUncertainRetries"] = defect is "allowance" or "version" ? 0 : 1;
        capture["independentGenerationMaxElapsedMilliseconds"] = defect == "version" ? null : 1_260_000;
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy[PlanningTransportPolicies.OptionName] = capture.DeepClone();
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var effective = journal["effective_transport_policy"]!;
        effective["planning_transport_policy"] = capture.DeepClone();
        effective["effective_retry_policy"]!["MaxUncertainRetries"] = capture["maxUncertainRetries"]!.DeepClone();
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(effective.ToJsonString());
        var attempts = journal["transport"]!["Attempts"]!.AsArray();
        var previous = attempts[0]!.DeepClone();
        previous["Id"] = RequestId + ":previous";
        previous["StartedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-2);
        previous["CompletedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        previous["Failure"] = "transport";
        previous["RetryDecision"] = "abandoned; independent tool-free generation retry";
        switch (defect)
        {
            case "marker": previous["RetryDecision"] = "other"; break;
            case "usage": previous["UsageKnown"] = true; break;
            case "protocol": previous["ProtocolComplete"] = true; break;
            case "assembled": previous["AssembledBody"] = "{}"; break;
            case "body": previous["Body"] = "{}"; break;
            case "cancelled": previous["Failure"] = "cancelled"; break;
            case "missing_completed": previous["CompletedAtUtc"] = null; break;
            case "negative_elapsed": previous["CompletedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-3); break;
            case "overlapping": previous["CompletedAtUtc"] = DateTimeOffset.UtcNow; break;
        }
        attempts.Insert(0, previous);
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var original = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        if (defect is not null)
            await Assert.ThrowsAnyAsync<Exception>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs",
                CancelledRun, RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention));
        else
        {
            var preview = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
                Ct, BenchmarkCampaign.CancelledPlanningRetention);
            await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
                preview["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
            var restarted = new BenchmarkCampaign(records, "retention");
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
            Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        }
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
    }
}
