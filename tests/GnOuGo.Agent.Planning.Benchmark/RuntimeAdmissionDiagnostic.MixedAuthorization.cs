using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Read-only authorization evidence. No old answer or operation enters MIXED.
    private static async Task<JsonObject> RequireAcceptedLocalAsync(IKeyVaultRecordStore records, CancellationToken ct)
    {
        using var stream = typeof(RuntimeAdmissionDiagnostic).Assembly.GetManifestResourceStream("AcceptedLocalReport")
            ?? throw new InvalidOperationException("Missing accepted LOCAL report.");
        var report = JsonNode.Parse(stream)!.AsObject();
        RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report);
        var id = RuntimeAdmissionDiagnosticRules.ComparisonIdentity + ":local";
        var expected = report["liveEvidence"]!["cases"]!.AsArray().Single(c => c!["case"]!.ToString() == "local")!;
        var manifest = await records.GetAsync(Collection, Tenant, RuntimeAdmissionDiagnosticRules.ComparisonIdentity, Author, ct)
            ?? throw new InvalidOperationException("Missing accepted LOCAL manifest.");
        var archived = await records.GetAsync(Collection, Tenant, id + ":report", Author, ct)
            ?? throw new InvalidOperationException("Missing accepted LOCAL report.");
        var checkpoint = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct)
            ?? throw new InvalidOperationException("Missing accepted LOCAL checkpoint.");
        var budgetRecord = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, Author, ct)
            ?? throw new InvalidOperationException("Missing accepted LOCAL accounting.");
        if (!JsonNode.DeepEquals(JsonNode.Parse(archived.Value), expected) ||
            !JsonNode.DeepEquals(JsonNode.Parse(manifest.Value), report["liveEvidence"]!["manifest"]))
            throw new InvalidOperationException("Accepted LOCAL report or frozen settings changed.");
        var envelope = JsonNode.Parse(checkpoint.Value)!;
        var state = JsonSerializer.Deserialize(envelope["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        var budget = JsonSerializer.Deserialize(budgetRecord.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (envelope["phase"]?.ToString() != "completed" ||
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot)) != expected["readOnlyRestart"]!["snapshotFingerprint"]!.ToString() ||
            budget.Calls != expected["durableBudgetCalls"]!.GetValue<long>() ||
            budget.InputTokens != expected["inputTokens"]!.GetValue<long>() || budget.OutputTokens != expected["outputTokens"]!.GetValue<long>())
            throw new InvalidOperationException("Accepted LOCAL checkpoint or accounting changed.");
        PlanningOperations.RequireCurrent(state);
        return new() { ["identity"] = id, ["outcome"] = "LOCAL PASS",
            ["manifestFingerprint"] = PlanningGraphCompiler.Fingerprint(manifest.Value),
            ["reportFingerprint"] = PlanningGraphCompiler.Fingerprint(archived.Value),
            ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(checkpoint.Value),
            ["budgetFingerprint"] = PlanningGraphCompiler.Fingerprint(budgetRecord.Value),
            ["snapshotFingerprint"] = expected["readOnlyRestart"]!["snapshotFingerprint"]!.DeepClone() };
    }
}
