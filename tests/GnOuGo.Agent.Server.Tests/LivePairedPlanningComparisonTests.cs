using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LiveIntentAgentGenerationTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ConstructionFormats_ComparePairedApprovedSubworkflows()
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_PAIRED_PLANNING_COMPARISON") != "1") return;
        var ledger = ResolveBudgetStatePath(FindSourceRoot());
        if (!File.Exists(ledger)) throw new InvalidOperationException("The comparison requires the existing cumulative campaign ledger.");
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_INTENT_AGENT_AMEND_AUTHORIZED_LIMITS") == "1")
            throw new InvalidOperationException("Comparison runs cannot amend the cumulative budget.");
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var path = ledger + ".paired-comparison-" + id + ".json";
        var environment = new FrozenComparisonEnvironment();
        var pairs = Enumerable.Range(1, 3).Select(index => new ComparisonPair(index,
            new(PlanningConstructionStrategies.TypedWorkflowsV1, index, "prepare-" + id + "-" + index)
            { PreparationOnly = true, InputTokenCeiling = 32_000, Environment = environment })).ToArray();
        foreach (var pair in pairs)
        {
            var strategies = new[] { PlanningConstructionStrategies.TypedWorkflowsV1, PlanningConstructionStrategies.JavaScriptV1 };
            if (pair.Index % 2 == 0) Array.Reverse(strategies);
            foreach (var strategy in strategies)
                pair.Arms.Add(new(strategy, pair.Index, strategy + "-" + id + "-" + pair.Index)
                { InputTokenCeiling = 32_000, Environment = environment });
        }
        var failures = new List<Exception>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(ResolveLiveCycleElapsedLimit());
        using var lease = new FileStream(ledger + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var pair in pairs)
            {
                await Run(pair.Preparation);
                var seed = pair.Preparation.Outcome == "passed" ? pair.Preparation.Snapshot : null;
                foreach (var arm in pair.Arms)
                { arm.Seed = seed; if (seed is null) arm.Outcome = "blocked_preparation"; }
                await WriteReport();
                foreach (var arm in pair.Arms.Where(a => a.Outcome == "not_run"))
                {
                    if (arm.RemainingCalls <= 0 || arm.PrefixActiveMilliseconds >= 900_000)
                    { arm.Outcome = "incomplete"; arm.FailureType = "SharedPreparationBudgetExhausted"; continue; }
                    await Run(arm);
                }
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        finally { await WriteReport(); }
        ThrowCapturedFailures(failures);
        Assert.All(pairs, pair => Assert.All(pair.Arms, arm => Assert.Equal("passed", arm.Outcome)));

        async Task Run(ComparisonAttempt attempt)
        {
            try { await RunCampaignAsync(2, comparison: attempt, comparisonLease: lease, comparisonCancellation: deadline.Token); }
            catch (Exception ex)
            {
                attempt.FailureType ??= ex.GetType().Name;
                if (attempt.Outcome == "not_run") attempt.Outcome = "incomplete";
                failures.Add(ex);
            }
            await WriteReport();
            deadline.Token.ThrowIfCancellationRequested();
        }

        Task WriteReport()
        {
            var arms = pairs.SelectMany(p => p.Arms).ToArray();
            var attempts = pairs.SelectMany(p => new[] { p.Preparation }.Concat(p.Arms)).ToArray();
            var report = new JsonObject
            {
                ["protocol"] = "paired-whole-subworkflows-v1", ["scenario"] = "existing-pull-request-review-acceptance",
                ["promptFingerprint"] = PlanningGraphCompiler.Fingerprint(AcceptancePrompt),
                ["inputTokenCeiling"] = pairs[0].Preparation.InputTokenCeiling,
                ["outputTokenCeiling"] = pairs[0].Preparation.OutputTokenCeiling, ["reasoning"] = pairs[0].Preparation.Reasoning,
                ["targetActiveMinutes"] = 5, ["maxActiveMinutesIncludingPreparation"] = 15, ["maxCallsIncludingPreparation"] = 100,
                ["generator"] = environment.Generator?.DeepClone(), ["catalogFingerprint"] = environment.CatalogFingerprint,
                ["complete"] = arms.Length == 6 && attempts.All(a => a.Outcome is "passed" or "failed" or "cleanup_failed" or "blocked_preparation"),
                ["typedWorkflowsAccepted"] = arms.Count(a => a.Strategy == PlanningConstructionStrategies.TypedWorkflowsV1 && a.Outcome == "passed") == 3,
                ["javascriptAccepted"] = arms.Count(a => a.Strategy == PlanningConstructionStrategies.JavaScriptV1 && a.Outcome == "passed") == 3,
                // Each actual receipt belongs to exactly one preparation or arm. Prefix usage is never billed twice.
                ["totalCampaignCalls"] = attempts.Sum(a => a.ToJson()["totalAttemptCalls"]!.GetValue<long>()),
                ["totalCampaignEstimatedCost"] = attempts.Sum(a => a.ToJson()["totalAttemptEstimatedCost"]!.GetValue<decimal>()),
                ["pairs"] = new JsonArray(pairs.Select(p => (JsonNode)new JsonObject
                {
                    ["pair"] = p.Index, ["preparation"] = p.Preparation.ToJson(),
                    ["arms"] = new JsonArray(p.Arms.Select(a => (JsonNode)a.ToJson()).ToArray())
                }).ToArray())
            };
            return File.WriteAllTextAsync(path, report.ToJsonString(new() { WriteIndented = true }), CancellationToken.None);
        }
    }

    private sealed record ComparisonPair(int Index, ComparisonAttempt Preparation)
    {
        public List<ComparisonAttempt> Arms { get; } = [];
    }

    private sealed class FrozenComparisonEnvironment
    {
        public JsonNode? Generator { get; private set; }
        public string? CatalogFingerprint { get; private set; }
        public void Check(PlanningSnapshot state)
        {
            var generator = state.Request.Options["generator"] ?? throw new InvalidOperationException("A comparison model must be configured.");
            Generator ??= generator.DeepClone();
            if (!JsonNode.DeepEquals(Generator, generator)) throw new InvalidOperationException("The configured comparison model changed.");
            if (state.PreparationCheckpoint?.ValidatedResults["discovery"] is not { } catalog) return;
            var fingerprint = PlanningPreparationCheckpoint.CatalogHash(catalog);
            CatalogFingerprint ??= fingerprint;
            if (CatalogFingerprint != fingerprint) throw new InvalidOperationException("The comparison catalog changed; no further planning is allowed.");
        }
    }

    private static string ComparisonCheckpointFingerprint(PlanningSnapshot snapshot) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(snapshot.Preparation, PlanningJsonContext.Default.PlanningPreparation) + "\n" +
        JsonSerializer.Serialize(snapshot.Graph, PlanningJsonContext.Default.PlanningGraph) + "\n" +
        snapshot.ApprovedBehaviorHash + "\n" + snapshot.Request.Prompt + "\n" +
        string.Join("\n", snapshot.Answers.Select(a => a.Question + a.Answers.ToJsonString())));

    // Test harness only. Production sessions cannot import checkpoints or switch strategy.
    private static PlanningSnapshot ForkComparisonCheckpoint(PlanningSnapshot fresh, PlanningSnapshot approved)
    {
        if (fresh.Status != PlanningStatus.Created || fresh.Request.TenantId != approved.Request.TenantId ||
            fresh.Request.SessionId == approved.Request.SessionId || fresh.Request.Prompt != approved.Request.Prompt ||
            approved.Status != PlanningStatus.Generating || approved.BehaviorPlan is null || approved.Preparation is null || approved.Graph is null ||
            approved.SourceCandidates.Count != 0 || approved.ConstructionUnits.Count != 0 || approved.ApprovedHash is not null ||
            approved.ApprovedBehaviorHash != PlanningBehaviorPlans.Fingerprint(approved.BehaviorPlan) ||
            PlanningBehaviorPlans.Validate(approved.BehaviorPlan, approved.Preparation).Any(d => d.Required))
            throw new InvalidOperationException("Only a fresh session and an approved, unconstructed checkpoint can form a comparison arm.");
        if (!JsonNode.DeepEquals(fresh.Request.Options["generator"], approved.Request.Options["generator"]) ||
            JsonSerializer.Serialize(fresh.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions) !=
            JsonSerializer.Serialize(approved.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions))
            throw new InvalidOperationException("Comparison generation settings differ from shared preparation.");
        fresh = JsonSerializer.Deserialize(JsonSerializer.Serialize(fresh, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(approved, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        fresh.Preparation = copy.Preparation; fresh.PreparationCheckpoint = copy.PreparationCheckpoint;
        fresh.BehaviorPlan = copy.BehaviorPlan; fresh.Graph = copy.Graph; fresh.Answers = copy.Answers;
        fresh.IntentChecked = copy.IntentChecked; fresh.ClarificationForms = copy.ClarificationForms; fresh.ClarificationQuestions = copy.ClarificationQuestions;
        fresh.ApprovedBehaviorHash = copy.ApprovedBehaviorHash; fresh.SourceBehaviorHash = copy.ApprovedBehaviorHash;
        fresh.Status = PlanningStatus.Generating; fresh.CurrentPhase = PlanningStatus.Generating;
        fresh.Request.Options["comparison_origin"] = new JsonObject { ["sessionId"] = approved.Request.SessionId,
            ["revision"] = approved.Revision, ["checkpointFingerprint"] = ComparisonCheckpointFingerprint(approved) };
        fresh.Revision++;
        if (ComparisonCheckpointFingerprint(fresh) != ComparisonCheckpointFingerprint(approved))
            throw new InvalidOperationException("The comparison checkpoint changed while forking.");
        return fresh;
    }
}
