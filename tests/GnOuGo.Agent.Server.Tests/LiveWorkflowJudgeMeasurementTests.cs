using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveWorkflowJudgeMeasurementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LiveJudgeReceiptRetainsItsStageUsageAndReplayDoesNotDoubleCount()
    {
        var campaign = new BenchmarkCampaign(new Records(), "judge-measurement");
        var request = new LLMRequest { ClientRequestId = "run:2:judge", Prompt = "retained review", MaxTokens = 32768 };
        var response = new LLMResponse
        {
            Json = new JsonObject { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified." },
            Usage = new JsonObject { ["input_tokens"] = 42L, ["output_tokens"] = 7L,
                ["benchmark_cost_eur"] = .02m, ["transport_attempts"] = 1, ["uncertain_attempts"] = 0 }
        };
        await campaign.SaveAsync("planning-evaluation-receipts", request.ClientRequestId,
            JsonSerializer.SerializeToNode(response, PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        var provider = new NeverDispatch();
        using var model = new KeyVaultBenchmarkModel(campaign, Options(), provider);
        var run = Run();
        var measured = new LiveWorkflowEvaluation.PlanningModel(model, run);

        await measured.CallAsync(request, Ct);
        await measured.CallAsync(request, Ct);
        var result = new JsonObject { ["mode"] = "live" };
        PlanningBenchmarkMeasurements.ProposalMetrics(run, result);

        var stage = Assert.IsType<JsonObject>(result["planning_stages"]?["judge"]);
        Assert.Equal(1, stage["logical_calls"]!.GetValue<int>());
        Assert.Equal(1, stage["physical_attempts"]!.GetValue<int>());
        Assert.Equal(42L, stage["input_tokens"]!.GetValue<long>());
        Assert.Equal(7L, stage["output_tokens"]!.GetValue<long>());
        Assert.Equal(.02m, stage["estimated_cost_eur"]!.GetValue<decimal>());
        Assert.True(stage["elapsed_ms"]!.GetValue<double>() >= 0);
        Assert.True(stage["usage_complete"]!.GetValue<bool>());
        Assert.Null(run["first_proposal"]);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ClosedUncertainJudgeRetainsStageAndUnknownUsageInsteadOfZero()
    {
        var campaign = new BenchmarkCampaign(new Records(), "judge-uncertain");
        var request = new LLMRequest { ClientRequestId = "run:2:judge", Prompt = "retained review", MaxTokens = 32768 };
        await campaign.SaveAsync("planning-evaluation-closures", request.ClientRequestId,
            new JsonObject { ["outcome"] = "inconclusive" }, Ct);
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, new JsonObject
        {
            ["input_ceiling"] = 96000L, ["output_ceiling"] = 32768L, ["cost_ceiling_eur"] = .5m,
            ["transport"] = new JsonObject { ["Attempts"] = new JsonArray(new JsonObject { ["Status"] = null }) }
        }, Ct);
        var provider = new NeverDispatch();
        using var model = new KeyVaultBenchmarkModel(campaign, Options(), provider);
        var run = Run();
        var measured = new LiveWorkflowEvaluation.PlanningModel(model, run);

        await Assert.ThrowsAsync<InvalidOperationException>(() => measured.CallAsync(request, Ct));
        var result = new JsonObject { ["mode"] = "live" };
        PlanningBenchmarkMeasurements.ProposalMetrics(run, result);

        var stage = Assert.IsType<JsonObject>(result["planning_stages"]?["judge"]);
        Assert.Equal(1, stage["logical_calls"]!.GetValue<int>());
        Assert.Equal(1, stage["physical_attempts"]!.GetValue<int>());
        Assert.Null(stage["input_tokens"]);
        Assert.Null(stage["output_tokens"]);
        Assert.Null(stage["estimated_cost_eur"]);
        Assert.False(stage["usage_complete"]!.GetValue<bool>());
        Assert.Equal(96000L, stage["reserved_input_tokens"]!.GetValue<long>());
        Assert.Equal(32768L, stage["reserved_output_tokens"]!.GetValue<long>());
        Assert.Equal(.5m, stage["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null(run["first_proposal"]);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(true, 0, "true_negatives")]
    [InlineData(true, 1, "false_positives")]
    [InlineData(false, 0, "false_negatives")]
    [InlineData(false, 1, "true_positives")]
    public void IndependentExecutionOracleLabelsOnlyExecutedCandidate(bool correct, int findings, string metric)
    {
        var state = new PlanningSession { Plan = PlanningCorpus.LiteralResult(), Requirements = PlanningCorpus.Requirements("local") };
        var hash = PlanningBenchmarkMeasurements.CandidateHash(state);
        var run = new JsonObject
        {
            ["oracle_version"] = "independent-execution-fixture-v1",
            ["current_candidate_hash"] = hash,
            ["candidate_reviews"] = new JsonArray(
                new JsonObject { ["candidate_hash"] = "prior-unexecuted", ["judge_findings"] = 0, ["business_correct"] = null },
                new JsonObject { ["candidate_hash"] = hash, ["judge_findings"] = findings, ["business_correct"] = null }),
            ["first_proposal"] = new JsonObject { ["candidate_hash"] = hash,
                ["artifact_hash"] = "before-approval-bookkeeping", ["business_correct"] = null }
        };

        LiveWorkflowEvaluation.RecordExecutionOracle(run, state, new JsonObject { ["passed"] = correct });

        var metrics = PlanningBenchmarkMeasurements.JudgeMetrics(run);
        Assert.Equal(correct, metrics["converged"]!.GetValue<bool>());
        Assert.Equal(1, metrics[metric]!.GetValue<int>());
        Assert.Equal(1, metrics["unknown_candidates"]!.GetValue<int>());
        Assert.Null(run["candidate_reviews"]![0]!["business_correct"]);
        Assert.Equal("independent-execution-fixture-v1", run["candidate_reviews"]![1]!["oracle_version"]!.ToString());
        Assert.Equal(correct, run["first_proposal"]!["business_correct"]!.GetValue<bool>());
    }

    [Fact]
    public void ArtifactIdentityCannotRelabelAnIdentifiedDifferentCandidate()
    {
        var state = new PlanningSession { Yaml = "version: 1", Plan = PlanningCorpus.LiteralResult(), Requirements = PlanningCorpus.Requirements("local") };
        var first = new JsonObject { ["artifact_hash"] = state.ComputeArtifactHash(), ["business_correct"] = null };
        first["candidate_hash"] = PlanningBenchmarkMeasurements.CandidateHash(state);
        state.Request.Prompt = "A different retained business request.";
        Assert.Equal(first["artifact_hash"]!.ToString(), state.ComputeArtifactHash());
        Assert.NotEqual(first["candidate_hash"]!.ToString(), PlanningBenchmarkMeasurements.CandidateHash(state));
        var run = new JsonObject { ["first_proposal"] = first };

        LiveWorkflowEvaluation.RecordExecutionOracle(run, state, new JsonObject { ["passed"] = true });

        Assert.Null(first["business_correct"]);
    }

    [Fact]
    public void MissingArtifactIdentitiesDoNotEstablishAnOracleAssociation()
    {
        var run = new JsonObject { ["first_proposal"] = new JsonObject { ["business_correct"] = null } };
        LiveWorkflowEvaluation.RecordExecutionOracle(run, new PlanningSession(), new JsonObject { ["passed"] = true });
        Assert.Null(run["first_proposal"]!["business_correct"]);
    }

    private static JsonObject Run() => new()
    {
        ["session"] = new JsonObject { ["modelCalls"] = 2,
            ["pendingCall"] = new JsonObject { ["purpose"] = "judge" } }
    };

    private static LLMOptions Options() => new()
    {
        DefaultProvider = "fixture", DefaultModel = "pinned",
        Models = { ["fixture"] = new() { Type = "openai" } }
    };

    private sealed class NeverDispatch : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("The measurement fixture must use retained evidence only."); }
    }

    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<(string Collection, string Tenant, string Key, string Author), KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenant, string key, string author, CancellationToken ct = default)
            => Task.FromResult(_rows.GetValueOrDefault((collection, tenant, key, author)));
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenant, string key, string value, string author, CancellationToken ct = default)
        {
            var row = new KeyVaultRecordValue(collection, tenant, key, value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _rows[(collection, tenant, key, author)] = row;
            return Task.FromResult(row);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenant, string author, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Where(p => p.Key.Collection == collection && p.Key.Tenant == tenant && p.Key.Author == author).Select(p => p.Value).ToArray());
        public Task<bool> DeleteAsync(string collection, string tenant, string key, string author, CancellationToken ct = default)
            => Task.FromResult(_rows.Remove((collection, tenant, key, author)));
    }
}
