using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LiveIntentAgentGenerationTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ConstructionStrategies_CompareSamePullRequestScenario()
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_PLANNING_COMPARISON") != "1") return;
        var sourceRoot = FindSourceRoot();
        var ledgerPath = ResolveBudgetStatePath(sourceRoot);
        // Never create a new ledger implicitly for this comparison.
        if (!File.Exists(ledgerPath)) throw new InvalidOperationException("The comparison requires the existing cumulative campaign ledger.");
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_INTENT_AGENT_AMEND_AUTHORIZED_LIMITS") == "1")
            throw new InvalidOperationException("Comparison runs cannot amend the cumulative budget.");
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var attempts = Enumerable.Range(1, 3).SelectMany(index => new[]
        {
            new ComparisonAttempt(PlanningConstructionStrategies.TypedUnitsV2, index, "units-" + id + "-" + index),
            new ComparisonAttempt(PlanningConstructionStrategies.JavaScriptV1, index, "js-" + id + "-" + index)
        }).ToArray();
        var reportPath = ledgerPath + ".comparison-" + id + ".json";
        var failures = new List<Exception>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(ResolveLiveCycleElapsedLimit());
        try
        {
            using var lease = new FileStream(ledgerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var attempt in attempts)
            {
                try { await RunCampaignAsync(2, comparison: attempt, comparisonLease: lease, comparisonCancellation: deadline.Token); }
                catch (Exception ex)
                {
                    attempt.FailureType = ex.GetType().Name;
                    if (attempt.Outcome == "not_run") attempt.Outcome = "incomplete";
                    failures.Add(ex);
                }
                await WriteComparisonReportAsync(reportPath, attempts);
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        finally { await WriteComparisonReportAsync(reportPath, attempts); }
        ThrowCapturedFailures(failures);
        Assert.All(attempts.Where(a => a.Strategy == PlanningConstructionStrategies.JavaScriptV1), a => Assert.Equal("passed", a.Outcome));
    }

    private static Task WriteComparisonReportAsync(string path, IEnumerable<ComparisonAttempt> attempts)
    {
        var all = attempts.ToArray();
        var report = new JsonObject
        {
            ["scenario"] = "existing-pull-request-review-acceptance",
            ["promptFingerprint"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(AcceptancePrompt),
            ["targetActiveMinutes"] = 5, ["maxActiveMinutes"] = 15, ["maxPlanningCalls"] = 100,
            ["inputTokenCeiling"] = 12_000, ["outputTokenCeiling"] = 8_192, ["reasoning"] = "low",
            ["complete"] = all.All(a => a.Outcome is "passed" or "failed" or "cleanup_failed"),
            ["javascriptAccepted"] = all.Where(a => a.Strategy == PlanningConstructionStrategies.JavaScriptV1).All(a => a.Outcome == "passed"),
            ["attempts"] = new JsonArray(all.Select(a => (JsonNode)a.ToJson()).ToArray())
        };
        return File.WriteAllTextAsync(path, report.ToJsonString(new() { WriteIndented = true }), CancellationToken.None);
    }

    private sealed class ComparisonAttempt(string strategy, int index, string cohort)
    {
        public string Strategy { get; } = strategy;
        public string Cohort { get; } = cohort;
        public string Outcome { get; set; } = "not_run";
        public string? FailureType { get; set; }
        public bool ExecutionStarted { get; set; }
        public double ActiveMilliseconds { get; set; }
        private PlanningSnapshot? _snapshot;
        private LLMUsageBudgetSnapshot? _before, _after;
        private decimal _reserveBefore, _reserveAfter;
        public void BeginUsage(LLMUsageBudgetSnapshot snapshot, decimal reserve) { _before = _after = snapshot; _reserveBefore = _reserveAfter = reserve; }
        public void EndUsage(LLMUsageBudgetSnapshot snapshot, decimal reserve) { _after = snapshot; _reserveAfter = reserve; }
        public void Capture(PlanningSnapshot snapshot)
        {
            _snapshot = snapshot;
            ActiveMilliseconds = Math.Max(ActiveMilliseconds, snapshot.ActiveMilliseconds);
        }
        public JsonObject ToJson() => new()
        {
            ["strategy"] = Strategy, ["repetition"] = index, ["cohort"] = Cohort, ["outcome"] = Outcome,
            ["failureType"] = FailureType, ["failedPhase"] = Outcome == "passed" ? null : Outcome == "cleanup_failed" ? "cleanup" : ExecutionStarted ? "execution" : _snapshot is null ? "preflight" : PlanningPhase.Resolve(_snapshot),
            ["diagnosticCodes"] = new JsonArray((_snapshot?.Diagnostics ?? []).Select(d => (JsonNode?)JsonValue.Create(d.Code)).ToArray()),
            ["calls"] = _snapshot?.Usage?.Calls ?? 0, ["inputTokens"] = _snapshot?.Usage?.InputTokens ?? 0, ["outputTokens"] = _snapshot?.Usage?.OutputTokens ?? 0,
            ["estimatedCost"] = _snapshot?.Usage?.EstimatedCost ?? 0, ["currency"] = _snapshot?.Usage?.EstimatedCostCurrency,
            ["activeMilliseconds"] = ActiveMilliseconds, ["withinTarget"] = Outcome == "passed" && ActiveMilliseconds <= 300_000,
            ["repairCalls"] = _snapshot is null ? 0 : Strategy == PlanningConstructionStrategies.JavaScriptV1
                ? _snapshot.SourceCandidates.Sum(c => Math.Max(0, c.Calls - 1)) : _snapshot.ConstructionUnits.Sum(c => c.RepairCalls),
            ["model"] = _snapshot?.Request.Options["generator"]?["model"]?.DeepClone(),
            ["provider"] = _snapshot?.Request.Options["generator"]?["provider"]?.DeepClone(),
            ["preparationFingerprint"] = _snapshot?.Preparation?.Fingerprint,
            ["sourceCalls"] = Strategy == PlanningConstructionStrategies.JavaScriptV1 && _snapshot is not null
                ? JsonValue.Create(_snapshot.SourceCandidates.Sum(c => c.Calls)) : null,
            ["totalAttemptCalls"] = (_after?.Calls ?? _before?.Calls ?? 0) - (_before?.Calls ?? 0),
            ["totalAttemptEstimatedCost"] = (_after?.EstimatedCost ?? _before?.EstimatedCost ?? 0) - (_before?.EstimatedCost ?? 0),
            ["unverifiedCostReserveDelta"] = _reserveAfter - _reserveBefore
        };
    }
}
