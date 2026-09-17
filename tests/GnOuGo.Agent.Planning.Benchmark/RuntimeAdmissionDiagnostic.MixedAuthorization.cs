using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Acceptance is a separate immutable adjudication. The historical LOCAL
    // report remains stopped; no old answer or operation enters the new state.
    private static async Task<JsonObject> RequireAcceptedLocalAsync(IKeyVaultRecordStore records, CancellationToken ct)
    {
        using var stream = typeof(RuntimeAdmissionDiagnostic).Assembly.GetManifestResourceStream("RetainedLocalAdjudication")
            ?? throw new InvalidOperationException("Missing accepted LOCAL adjudication.");
        var report = JsonNode.Parse(stream)!["currentAdjudication"]!.AsObject();
        RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report);
        var id = RuntimeAdmissionDiagnosticRules.ComparisonIdentity + ":local";
        foreach (var (collection, key, fingerprint) in new[]
        {
            (Collection, id + ":checkpoint", "checkpointFingerprint"),
            (Collection, id + ":report", "originalReportFingerprint"),
            (PlanningBudgetSink.Collection, id, "budgetFingerprint")
        })
        {
            var archived = await records.GetAsync(collection, Tenant, key, Author, ct)
                ?? throw new InvalidOperationException("Missing immutable LOCAL evidence.");
            if (PlanningGraphCompiler.Fingerprint(archived.Value) != report[fingerprint]!.ToString())
                throw new InvalidOperationException("Accepted LOCAL evidence or accounting changed.");
        }
        return report;
    }
}
