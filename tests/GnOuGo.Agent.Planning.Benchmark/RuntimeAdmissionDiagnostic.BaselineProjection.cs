using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Read-only, original-schema evidence audit and request-domain comparison.
    // No live transport, journal, session start or record mutation exists on this path.
    internal static async Task BaselineProjectionAuditAsync(string id, IKeyVaultRecordStore records)
    {
        var ct = CancellationToken.None;
        var checkpoint = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct)
            ?? throw new InvalidOperationException("The retained checkpoint is required.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct);
        var manifest = await records.GetAsync(Collection, Tenant, id[..id.LastIndexOf(':')], Author, ct);
        var archived = JsonSerializer.Deserialize(JsonNode.Parse(checkpoint.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        var receipts = new Dictionary<string, (LLMRequest, LLMResponse?)>(StringComparer.Ordinal);
        var checks = new JsonArray();
        foreach (var call in archived.RequestAccounting.DistinctBy(c => c.Id))
        {
            var request = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct);
            var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct);
            if (request is null) continue;
            var issued = JsonSerializer.Deserialize(request.Value, PlanningJsonContext.Default.LLMRequest)!;
            var response = receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse);
            receipts.Add(call.Id, (issued, response));
            var candidate = response?.Json ?? (string.IsNullOrWhiteSpace(response?.Text) ? null : JsonNode.Parse(response.Text));
            checks.Add(new JsonObject { ["requestFingerprint"] = PlanningGraphCompiler.Fingerprint(request.Value),
                ["receiptFingerprint"] = receipt is null ? null : PlanningGraphCompiler.Fingerprint(receipt.Value),
                ["originalSchemaFindings"] = candidate is null ? null : PlanningContractValidation.ValidateInstance(candidate, issued.StructuredOutputSchema!).Count });
        }
        var current = new PlanningSnapshot { Request = PlanningContext.Clone(archived).Request };
        var decisions = PlanningSourceDecisions.InterpretationDecisions(current);
        var structural = PlanningIntentAssessment.IntentSources(current).Count(s => s.Structural);
        var historicalBaselineScopes = archived.RuntimeEvidence.Where(e => archived.References.Single(r => r.Id == e.SourceReference).SourceId == "existing")
            .Select(e => e.SourceReference).Distinct(StringComparer.Ordinal).Count();
        var client = new ReceiptOnlyClient(id, receipts, manifest is null ? null : JsonNode.Parse(manifest.Value)?["model"]?.AsObject());
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        string? stop = null;
        try { await PlanningSourceDecisions.InterpretAsync(current, runtime, ct); }
        catch (WorkflowRuntimeException error) { stop = error.Code; }
        if (stop is not ("REPLAY_EVIDENCE_REQUIRED" or "REPLAY_REQUEST_CHANGED")) throw new InvalidOperationException("Changed-request replay must stop at unavailable matching evidence: " + stop);
        if ((await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct))?.Value != checkpoint.Value ||
            (await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct))?.Value != budget?.Value)
            throw new InvalidOperationException("Archive integrity check failed.");
        Console.WriteLine(new JsonObject
        {
            ["evidence"] = "Original-schema historical audit; current request-domain comparison and strict receipt-only replay. No synthetic responses or live evidence.",
            ["identity"] = id, ["providerDispatches"] = 0, ["archiveUnchanged"] = true,
            ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(checkpoint.Value),
            ["budgetFingerprint"] = budget is null ? null : PlanningGraphCompiler.Fingerprint(budget.Value),
            ["originalReceipts"] = checks,
            ["historicalInterpretationDecisions"] = archived.DecisionPages.Where(p => p.Phase == "intent" && p.Status == "completed").SelectMany(p => p.Decisions).Distinct().Count(),
            ["historicalBaselineDecisions"] = historicalBaselineScopes,
            ["currentInterpretationDecisions"] = decisions.Length,
            ["currentBaselineAnnotationDecisions"] = decisions.Count(d => d.Context["structuralOwner"] is not null),
            ["currentStructuralInterpretationDecisions"] = 0, ["engineStructuralUnits"] = structural,
            ["historicalVerifiedRequests"] = receipts.Count(p => p.Value.Item2 is not null),
            ["currentPackedInterpretationPages"] = PlanningDecisionPages.PackedPageCount(current, decisions),
            ["strictReplayStop"] = stop, ["reusedMatchingReceipts"] = client.Replayed.Count,
            ["liveCallSavings"] = null
        }.ToJsonString());
    }
}
