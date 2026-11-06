using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    private const string CancelledRun = "server-cancelled-run";

    [Fact]
    public async Task CancelledServerGenerationRetentionPreservesReservationAndForbidsReplayAcrossRestart()
    {
        var records = new Records(); var campaign = await SeedCancelledServerAsync(records);
        var original = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId, Ct,
            BenchmarkCampaign.CancelledPlanningRetention);
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        Assert.Equal(1m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await restarted.RequireRunNotRetainedAsync("independent-fresh-session", Ct);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("session")]
    [InlineData("configuration")]
    [InlineData("provider")]
    [InlineData("metadata")]
    [InlineData("elapsed")]
    [InlineData("status")]
    [InlineData("execution")]
    [InlineData("tools")]
    [InlineData("receipt")]
    [InlineData("usage")]
    [InlineData("body")]
    [InlineData("failure")]
    public async Task CancelledServerRetentionRejectsSubstitutionEffectsAndReceivedOutcomes(string defect)
    {
        var records = new Records(); var campaign = await SeedCancelledServerAsync(records);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", CancelledRun, Ct))!;
        switch (defect)
        {
            case "host": run["host"] = "other"; break;
            case "session": run["session_id"] = "other"; break;
            case "configuration": run["campaign_configuration"] = new string('d', 64); break;
            case "provider": run["provider"] = null; break;
            case "metadata": run["session"]!["request"]!["options"]!["captured_model_metadata"]!["model"] = "other"; break;
            case "elapsed": run["session"]!["request"]!["options"]!["llm_budget"]!["max_elapsed_ms"] = 1; break;
            case "status": run["session"]!["status"] = PlanningStatus.Generating; break;
            case "execution": run["execution_started"] = "issued"; break;
            case "tools": run["session"]!["pendingCall"]!["request"]!["tools"] = new JsonArray(new JsonObject()); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new JsonObject(), Ct); break;
            case "usage":
            case "body":
                var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
                if (defect == "usage") journal["usage"] = new JsonObject();
                else journal["transport"]!["Attempts"]![0]!["Body"] = "partial";
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct); break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", RequestId, new JsonObject(), Ct); break;
        }
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun,
            RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedCancelledServerAsync(Records records)
    {
        var campaign = await SeedCapturedRetriesAsync(records, 1, true);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        run["session"]!["status"] = PlanningStatus.Cancelled;
        run["session"]!["request"]!["options"]!["captured_model_metadata"] = new JsonObject { ["provider"] = "OpenAi", ["model"] = "private-deployment" };
        run["session"]!["pendingCall"]!["request"]!["provider"] = "OpenAi";
        run["session"]!["pendingCall"]!["request"]!["model"] = "private-deployment";
        run["result"]!["status"] = PlanningStatus.Cancelled;
        run["host"] = "GnOuGo.Agent.Server"; run["session_id"] = Session;
        run["provider"] = "OpenAi"; run["model"] = "private-deployment";
        run["campaign_configuration"] = new string('c', 64);
        await campaign.SaveAsync("planning-evaluation-configuration", "configuration:" + new string('c', 64), new()
        { ["configuration"] = new JsonObject { ["provider"] = "OpenAi", ["model"] = "private-deployment" } }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", RequestId, run["session"]!["pendingCall"]!["request"]!.DeepClone().AsObject(), Ct);
        run["session"]!["request"]!["options"]!.AsObject().Remove("benchmark_planning_elapsed_ms");
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy["elapsed_limit_origin"] = "captured_server_session_budget";
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Failure"] = null;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new()
        { ["stage"] = "dispatch", ["exception_type"] = "OperationCanceledException" }, Ct);
        return campaign;
    }
}
