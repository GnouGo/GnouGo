using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveDiagnostic
{
    internal static async Task RunAsync(JsonObject manifest, PlanningSnapshot state, string pageId, IDbContextFactory<PlanningDbContext> contexts,
        IKeyVaultRecordStore records, SecureWorkflowRuntimeSession runtime, IExchangeRateProvider rates, CancellationToken ct)
    {
        var page = state.DecisionPages.Single(p => p.Id == pageId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Calls.AsNoTracking().SingleAsync(c => c.TenantId == state.Request.TenantId && c.SessionId == state.Request.SessionId && c.RequestHash == page.RequestId, ct);
        var source = await records.GetAsync(PlanningModelJournal.RequestCollection, state.Request.TenantId, row.PayloadKey, EfPlanningSessionStore.Author, ct);
        var original = await records.GetAsync(PlanningModelJournal.Collection, state.Request.TenantId, row.PayloadKey, EfPlanningSessionStore.Author, ct);
        var request = ProgressiveRules.DiagnosticRequest(state, page, JsonSerializer.Deserialize(source!.Value, PlanningJsonContext.Default.LLMRequest)!,
            original is null ? null : JsonSerializer.Deserialize(original.Value, PlanningJsonContext.Default.LLMResponse), manifest["abUsed"]!.GetValue<bool>());
        var metadata = await runtime.LlmCapabilityResolver!.SupportedReasoningLevelsAsync(request.Provider, request.Model, ct);
        if (metadata?.Contains("medium", StringComparer.Ordinal) != true) throw new InvalidOperationException("Medium reasoning is not established by provider metadata.");
        var captured = new List<PlanningSnapshot>();
        foreach (var record in await records.ListAsync(EfPlanningSessionStore.Collection, state.Request.TenantId, EfPlanningSessionStore.Author, ct))
            if (record.Key.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal))
            {
                var snapshot = PlanningSnapshotPayload.Decode(record.Value);
                if (snapshot.Construction.PendingCalls.Any(c => c.Id == page.RequestId)) captured.Add(snapshot);
            }
        var checkpoint = captured.OrderByDescending(s => s.Revision).FirstOrDefault() ?? throw new InvalidOperationException("The original reserved revision is unavailable for a bounded comparison.");
        var savedBudget = await records.GetAsync(PlanningBudgetSink.Collection, state.Request.TenantId, state.Request.SessionId, EfPlanningSessionStore.Author, ct)
            ?? throw new InvalidOperationException("The existing budget is required; no diagnostic budget reset is permitted.");
        var settings = ProgressiveRules.Settings();
        var configured = PlanningBudgetOptions.Parse(state.Request.Options);
        var budget = new LLMUsageBudgetScope(new LLMUsageBudgetLimits
        { MaxCalls = Math.Min(configured?.MaxCalls ?? settings.MaxModelCalls, settings.MaxModelCalls), MaxTotalTokens = Math.Min(configured?.MaxTotalTokens ?? settings.MaxTotalTokens, settings.MaxTotalTokens),
            MaxEstimatedCost = configured?.MaxEstimatedCost ?? new WorkflowPlanningBudgetSettings().CreateLimit() },
            JsonSerializer.Deserialize(savedBudget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), sink: new PlanningBudgetSink(records, state.Request.TenantId, state.Request.SessionId), exchangeRateProvider: rates);
        manifest["abUsed"] = true; manifest["diagnostic"] = new JsonObject { ["sourceRequest"] = page.RequestId, ["request"] = request.ClientRequestId, ["revision"] = checkpoint.Revision };
        await records.UpsertAsync(ProgressiveCampaign.CampaignCollection, ProgressiveCampaign.Tenant, ProgressiveCampaign.CampaignId, manifest.ToJsonString(), ProgressiveCampaign.Author, ct);
        var journal = new PlanningModelJournal(runtime.LlmClient, contexts, records, state.Request.TenantId, state.Request.SessionId, budget, new ModelMetadataUsageCostEstimator(runtime.Options), state.Request.Generation);
        var response = await journal.CallAsync(request, ct);
        Console.WriteLine("Diagnostic receipt retained separately; the live session remains stopped.");
        await OfflineReplay.RunAsync(contexts, records, state.Request.TenantId, state.Request.SessionId, checkpoint.Revision, ct, page.RequestId, response);
    }
}
