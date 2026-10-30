using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkQualificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Source => new('a', 40);
    private static string Artifact => new('b', 64);

    [Fact]
    public async Task SixConsecutiveExecutionsRequireBothCollectionsVariantsAndDoNotApplyEfficiencyGates()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "qualification");
        await SeedAsync(campaign);
        var before = records.Snapshot();
        var report = await Report(campaign);
        Assert.True(report["complete"]!.GetValue<bool>());
        Assert.Equal(6, report["passed"]!.GetValue<int>());
        Assert.Equal(3, report["qualification"]!.AsObject().Count);
        Assert.Equal(before, records.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionWarningsComeFromTheOwnedJournalEvenWhenTheReportWasInterrupted(bool interrupted)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "qualification");
        await SeedAsync(campaign);
        const string id = "gateamazon-amazon-1";
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + id, Ct))!;
        run["execution_model_usage"] = new JsonObject { ["calls"] = 0 };
        if (interrupted) run.Remove("execution_ms");
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + id, run, Ct);
        var store = new InMemoryWorkflowRunStore();
        foreach (var tenant in new[] { "benchmark", "other" })
            await store.CreateAsync(new WorkflowRun
            {
                TenantId = tenant, RunId = id, Limits = new() { TenantId = tenant, RunId = id },
                ModelUsage = new() { StartedAtUtc = DateTimeOffset.UnixEpoch, Calls = 2, InputTokens = 100, OutputTokens = 2,
                    TotalTokens = 102, Warnings = [new("request_input_tokens", 100, 80, "measured", "execution")
                    { RequestId = tenant + ":runtime:1" }] }
            }, Ct);
        var before = records.Snapshot();
        var report = await LiveCampaignEvidence.ReportAsync(campaign, source: Source, amazonCohort: "gateamazon",
            codeCohort: "gatecode", collectionsCohort: "gatecollections", executionStore: store);
        var row = report["runs"]!.AsArray().Single(value => value?["run"]?.ToString() == id)!;
        Assert.Equal(2, row["execution_model_usage"]!["calls"]!.GetValue<int>());
        Assert.Equal("benchmark:runtime:1", row["execution_model_usage"]!["warnings"]![0]!["requestId"]!.ToString());
        Assert.Equal(interrupted ? "interrupted" : "completed", row["execution_status"]!.ToString());
        Assert.Equal(!interrupted, row["qualifying_success"]!.GetValue<bool>());
        Assert.Equal(before, records.Snapshot());
        Assert.Single((await store.ReadAsync("benchmark", id, Ct))!.ModelUsage!.Warnings!);
    }

    [Theory]
    [InlineData("alternate")]
    [InlineData("effect")]
    [InlineData("oracle")]
    [InlineData("final_review")]
    [InlineData("operator_revision")]
    [InlineData("approval_hash")]
    [InlineData("pending_call")]
    [InlineData("artifact")]
    [InlineData("unfinished")]
    public async Task AClaimedSuccessCannotReplaceRequiredExecutionEvidence(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "qualification");
        await SeedAsync(campaign);
        var key = CollectionKey(2);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", key, Ct))!;
        switch (defect)
        {
            case "alternate": run["result"]!["execution_variants"]!.AsArray().RemoveAt(1); break;
            case "effect": run["result"]!["execution_variants"]![1]!["effects"]!.AsArray().Add("unexpected"); break;
            case "oracle": run["result"]!["artifact_oracle"]!["passed"] = false; break;
            case "final_review": run["result"]!["final_review"] = false; break;
            case "operator_revision": run["planning_revisions"] = new JsonArray(new JsonObject()); break;
            case "approval_hash": run["session"]!["approvalReview"]!["artifactHash"] = new string('c', 64); break;
            case "pending_call": run["session"]!["pendingCall"] = new JsonObject(); break;
            case "artifact": run["session"]!["yaml"] = null; break;
            case "unfinished": run.Remove("result"); break;
        }
        await campaign.SaveAsync("planning-evaluation-runs", key, run, Ct);
        var report = await Report(campaign);
        Assert.False(report["complete"]!.GetValue<bool>());
        Assert.Equal(0, report["qualification"]!["collections"]!["consecutive_successes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task AFailedAttemptResetsTheStreakAndTwoLaterSuccessesCanQualify(bool secondSuccess, int expected)
    {
        var campaign = new BenchmarkCampaign(new Records(), "qualification");
        await SeedAsync(campaign);
        var key = "run:gateamazon-amazon-" + (secondSuccess ? "1" : "2");
        var failed = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, key, Ct))!;
        failed["oracle"]!["passed"] = false;
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, key, failed, Ct);
        await SaveLive(campaign, "amazon", 3);
        var report = await Report(campaign);
        Assert.Equal(expected, report["qualification"]!["amazon"]!["consecutive_successes"]!.GetValue<int>());
        Assert.Equal(secondSuccess, report["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MissingAttemptsAndChangedProfilesCannotBePooled()
    {
        var campaign = new BenchmarkCampaign(new Records(), "qualification");
        await SaveLive(campaign, "amazon", 1);
        await SaveLive(campaign, "amazon", 3);
        await SaveCollection(campaign, 1);
        await SaveCollection(campaign, 2);
        var changed = (await campaign.LoadAsync("planning-evaluation-runs", CollectionKey(2), Ct))!;
        changed["result"]!["model"] = "changed-model";
        await campaign.SaveAsync("planning-evaluation-runs", CollectionKey(2), changed, Ct);
        var report = await Report(campaign);
        Assert.Equal(1, report["qualification"]!["amazon"]!["consecutive_successes"]!.GetValue<int>());
        Assert.Equal(1, report["qualification"]!["collections"]!["consecutive_successes"]!.GetValue<int>());
        Assert.False(report["complete"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("manifest")]
    [InlineData("configuration")]
    public async Task ChangedFrozenProvenanceIsRejectedWithoutChangingRecords(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "qualification");
        await SeedAsync(campaign);
        if (defect == "configuration")
        {
            var run = (await campaign.LoadAsync("planning-evaluation-runs", CollectionKey(2), Ct))!;
            run["result"]!["configuration"]!["calls"] = 100;
            await campaign.SaveAsync("planning-evaluation-runs", CollectionKey(2), run, Ct);
        }
        else
        {
            var key = defect == "source" ? "gatecode-manifest" : "run:gatecode-code-2";
            var record = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, key, Ct))!;
            if (defect == "source") record["production_sha"] = new string('c', 40);
            else record["manifest"]!["model"] = "changed-model";
            await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, key, record, Ct);
        }
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Report(campaign));
        Assert.Equal(before, records.Snapshot());
    }

    [Fact]
    public async Task HistoricalTwoByThreeManifestRemainsHistoricalAndDoesNotReplaceCollections()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "qualification");
        foreach (var scenario in new[] { "amazon", "code" })
            for (var repetition = 1; repetition <= 3; repetition++) await SaveLive(campaign, scenario, repetition);
        var before = records.Snapshot();
        var report = await Report(campaign);
        Assert.False(report["complete"]!.GetValue<bool>());
        Assert.Equal(4, report["passed"]!.GetValue<int>());
        Assert.Equal(before, records.Snapshot());
    }

    private static Task<JsonObject> Report(BenchmarkCampaign campaign) => LiveCampaignEvidence.ReportAsync(campaign,
        source: Source, amazonCohort: "gateamazon", codeCohort: "gatecode", collectionsCohort: "gatecollections");

    private static async Task SeedAsync(BenchmarkCampaign campaign)
    {
        for (var repetition = 1; repetition <= 2; repetition++)
        {
            await SaveLive(campaign, "amazon", repetition);
            await SaveLive(campaign, "code", repetition);
            await SaveCollection(campaign, repetition);
        }
    }

    private static JsonObject Session(string id) => new()
    {
        ["schemaVersion"] = 11, ["status"] = PlanningStatus.Approved,
        ["request"] = new JsonObject { ["sessionId"] = id }, ["approvedHash"] = Artifact,
        ["plan"] = new JsonObject(), ["graph"] = new JsonObject(), ["yaml"] = "retained executable artifact",
        ["construction"] = new JsonObject { ["complete"] = true },
        ["approvalReview"] = new JsonObject { ["origin"] = "delegated_test", ["artifactHash"] = Artifact }
    };

    private static async Task SaveLive(BenchmarkCampaign campaign, string scenario, int repetition)
    {
        var cohort = "gate" + scenario; var label = cohort + "-" + scenario + "-" + repetition;
        var manifest = new JsonObject { ["production_sha"] = Source, ["harness_sha"] = Source,
            ["provider"] = "frozen", ["model"] = "frozen", ["cohort"] = cohort,
            ["scenario_count"] = 2, ["repetitions"] = 3 };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, cohort + "-manifest", manifest, Ct);
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + label, new JsonObject
        {
            ["source"] = Source, ["label"] = label, ["scenario"] = scenario, ["manifest"] = manifest,
            ["session"] = Session(label), ["artifact_hash"] = Artifact,
            ["execution_started"] = "recorded", ["execution_ms"] = 1,
            ["execution"] = new JsonObject { ["success"] = true }, ["oracle"] = new JsonObject { ["passed"] = true },
            ["result"] = new JsonObject { ["mode"] = "live", ["execution_status"] = "completed", ["execution_oracle"] = true,
                ["calls"] = 99, ["judge_calls"] = 9, ["max_judge_bytes"] = 50_000 }
        }, Ct);
    }

    private static string CollectionKey(int repetition) => Source + ":fixture:cohort:gatecollections:collections:" + repetition;
    private static async Task SaveCollection(BenchmarkCampaign campaign, int repetition)
    {
        var configuration = new JsonObject { ["calls"] = 64, ["phase_policy_hash"] = "frozen" };
        await campaign.SaveAsync("planning-evaluation-configuration", "corpus-settings:" + Source + ":fixture:control:cohort:gatecollections", configuration, Ct);
        var variants = new JsonArray(new[] { "nominal", "alternate" }.Select(name => (JsonNode)new JsonObject
        { ["variant"] = name, ["correct"] = true, ["runtime_success"] = true, ["violations"] = new JsonArray(), ["effects"] = new JsonArray() }).ToArray());
        await campaign.SaveAsync("planning-evaluation-runs", CollectionKey(repetition), new JsonObject
        {
            ["session"] = Session(PlanningGraphCompiler.Fingerprint(campaign.Id + ":" + CollectionKey(repetition))),
            ["execution_started"] = true, ["artifact_oracle_version"] = "frozen-oracle",
            ["result"] = new JsonObject { ["mode"] = "live", ["source_commit"] = Source, ["harness_commit"] = Source,
                ["case"] = "collections", ["cohort"] = "gatecollections", ["repetition"] = repetition,
                ["provider"] = "frozen", ["model"] = "frozen", ["configuration"] = configuration, ["manifest"] = "frozen",
                ["final_review"] = true, ["artifact_oracle"] = new JsonObject { ["passed"] = true },
                ["execution_correct"] = true, ["safety_violations"] = new JsonArray(), ["execution_variants"] = variants }
        }, Ct);
    }

    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<(string Collection, string Tenant, string Key, string Author), KeyVaultRecordValue> _rows = [];
        internal string Snapshot() => string.Join('\n', _rows.OrderBy(r => r.Key).Select(r => r.Value.Value));
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default)
            => Task.FromResult(_rows.GetValueOrDefault((c, t, k, a)));
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default)
        {
            var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _rows[(c, t, k, a)] = row; return Task.FromResult(row);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Where(r => r.Key.Collection == c && r.Key.Tenant == t && r.Key.Author == a).Select(r => r.Value).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
