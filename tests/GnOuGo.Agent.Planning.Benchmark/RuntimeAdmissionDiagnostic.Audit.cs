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
    // Read-only audit: archived requests keep their original schema and identity.
    internal static async Task AuditAsync(string id, IKeyVaultRecordStore records)
    {
        var ct = CancellationToken.None;
        var captured = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct)
            ?? throw new InvalidOperationException("The captured admission checkpoint is unavailable.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct);
        var state = JsonSerializer.Deserialize(JsonNode.Parse(captured.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        var evidence = new Dictionary<string, (LLMRequest, LLMResponse?)>(StringComparer.Ordinal);
        var checks = new JsonArray();
        var subjectSelections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in state.RequestAccounting.DistinctBy(c => c.Id))
        {
            var request = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct);
            var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct);
            if (request is null) continue;
            var issued = JsonSerializer.Deserialize(request.Value, PlanningJsonContext.Default.LLMRequest)!;
            var response = receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse);
            evidence.Add(call.Id, (issued, response));
            JsonNode? candidate = response?.Json;
            var malformed = false;
            if (candidate is null && !string.IsNullOrWhiteSpace(response?.Text))
                try { candidate = JsonNode.Parse(response.Text); } catch (JsonException) { malformed = true; }
            if (candidate is JsonObject answers)
                foreach (var answer in answers)
                    if (answer.Value is JsonObject body && body["runtime"] is JsonArray runtimeEvidence)
                        for (var i = 0; i < runtimeEvidence.Count; i++)
                            if (runtimeEvidence[i]?["subject"] is not null) subjectSelections.Add(answer.Key + ":" + i);
            checks.Add(new JsonObject { ["requestId"] = call.Id, ["requestFingerprint"] = PlanningGraphCompiler.Fingerprint(request.Value),
                ["receiptFingerprint"] = receipt is null ? null : PlanningGraphCompiler.Fingerprint(receipt.Value),
                ["completionStatus"] = response?.CompletionStatus, ["malformedJson"] = malformed,
                ["schemaFindings"] = candidate is null ? null : PlanningContractValidation.ValidateInstance(candidate, issued.StructuredOutputSchema!).Count });
        }
        var manifest = await records.GetAsync(Collection, Tenant, id[..id.LastIndexOf(':')], Author, ct);
        var frozenModel = manifest is null ? null : JsonNode.Parse(manifest.Value)?["model"] as JsonObject;
        var client = new ReceiptOnlyClient(id, evidence, frozenModel);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        string? code = null, location = null;
        try { await PlanningOperations.ResolveAsync(state, runtime, ct); }
        catch (WorkflowRuntimeException error) { code = error.Code; location = error.Details?["location"]?.ToString(); }
        var after = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct);
        var afterBudget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct);
        if (after?.Value != captured.Value || afterBudget?.Value != budget?.Value) throw new InvalidOperationException("Archived evidence changed during audit.");
        Console.WriteLine(new JsonObject { ["identity"] = id, ["providerDispatches"] = 0, ["sourceUnchanged"] = true,
            ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(captured.Value), ["receiptChecks"] = checks,
            ["historicalSubjectSelections"] = subjectSelections.Count,
            ["replayedReceipts"] = client.Replayed.Count, ["blocker"] = code, ["location"] = location }.ToJsonString());
    }
}
