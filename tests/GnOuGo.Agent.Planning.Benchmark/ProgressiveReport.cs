using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveReport
{
    internal static long? Usage(LLMResponse? response, bool output)
    {
        foreach (var name in output ? new[] { "output_tokens", "completion_tokens", "outputTokens" } : new[] { "input_tokens", "prompt_tokens", "inputTokens" })
            if (response?.Usage?[name] is JsonValue value && value.TryGetValue<long>(out var count)) return count;
        return null;
    }
    internal static JsonObject Build(PlanningSnapshot state, IReadOnlyDictionary<string, LLMResponse?> receipts)
    {
        var calls = state.RequestAccounting.DistinctBy(r => r.Id).ToArray();
        var verified = receipts.Where(r => r.Value is not null).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var pages = state.DecisionPages.DistinctBy(p => p.Id).ToArray();
        var holes = state.Construction.Holes.Where(h => !h.Superseded).DistinctBy(h => (h.WorkflowKey, h.Id)).ToArray();
        long? Sum(IEnumerable<long?> values) { var all = values.ToArray(); return all.Length == 0 ? 0 : all.Any(v => v is null) ? null : all.Sum(v => v!.Value); }
        var result = new JsonObject
        {
            ["session"] = state.Request.SessionId, ["revision"] = state.Revision, ["status"] = state.Status, ["outcome"] = state.Outcome?.Name,
            ["phase"] = state.CurrentPhase, ["artifactHash"] = state.ArtifactHash, ["approvedHash"] = state.ApprovedHash,
            ["modelCalls"] = calls.Count(r => verified.Contains(r.Id)), ["reservations"] = calls.Length,
            ["unverifiableDispatches"] = calls.Count(r => r.Evidence == "unverifiable" && !verified.Contains(r.Id)),
            ["journalReservationsWithoutReceipt"] = receipts.Count(r => r.Value is null),
            ["decisionPages"] = pages.Length,
            ["pagesByPhase"] = new JsonArray(pages.GroupBy(p => (p.Phase, p.Status, p.Correction)).Select(g => (JsonNode)new JsonObject
            { ["phase"] = g.Key.Phase, ["status"] = g.Key.Status, ["correction"] = g.Key.Correction, ["pages"] = g.Count(), ["splitChildren"] = g.Count(p => p.ParentId is not null) }).ToArray()),
            ["engineResolvedExecutableDecisions"] = holes.Count(h => h.Resolved && h.ResolutionOrigin == "deterministic"),
            ["otherEngineDecisions"] = null,
            ["modelDecisionIds"] = new JsonArray(pages.Where(p => p.RequestId is not null && verified.Contains(p.RequestId)).SelectMany(p => p.Decisions.Select(d => p.WorkflowKey + ":" + d))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["modelExecutableDecisions"] = holes.Count(h => h.Resolved && h.ResolutionOrigin == "model"),
            ["executableHoleExposures"] = holes.Sum(h => h.ExposedRequests.Distinct(StringComparer.Ordinal).Count(verified.Contains)),
            ["userClarifications"] = state.Intent.Questions, ["answeredForms"] = state.Intent.Answers.Count,
            ["clarificationDecision"] = (state.Outcome as PlanningNeedUserClarification)?.Decision.DecisionId,
            ["largestEstimatedInput"] = calls.Length == 0 ? null : calls.Max(c => (int?)c.EstimatedInputTokens),
            ["largestActualInput"] = calls.Select(c => c.InputTokens).DefaultIfEmpty(null).Max(),
            ["inputTokens"] = Sum(calls.Where(c => verified.Contains(c.Id)).Select(c => c.InputTokens)),
            ["outputTokens"] = Sum(calls.Where(c => verified.Contains(c.Id)).Select(c => c.OutputTokens)),
            ["effectiveReasoning"] = new JsonArray(calls.Select(c => c.Reasoning).Distinct(StringComparer.Ordinal).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["requestsByPhase"] = new JsonArray(calls.GroupBy(c => (c.WorkflowKey, c.Phase, c.Gate)).Select(g => (JsonNode)new JsonObject
            {
                ["workflow"] = g.Key.WorkflowKey, ["phase"] = g.Key.Phase, ["gate"] = g.Key.Gate,
                ["reservations"] = g.Count(), ["calls"] = g.Count(c => verified.Contains(c.Id)), ["repairReservations"] = g.Count(c => c.Repair == true),
                ["inputTokens"] = Sum(g.Where(c => verified.Contains(c.Id)).Select(c => c.InputTokens)), ["outputTokens"] = Sum(g.Where(c => verified.Contains(c.Id)).Select(c => c.OutputTokens))
            }).ToArray()),
            ["repairAllowances"] = new JsonArray(state.RepairAllowances.Select(a => (JsonNode)new JsonObject { ["workflow"] = a.WorkflowKey, ["gate"] = a.Gate, ["consumed"] = a.Attempts }).ToArray()),
            ["failuresByGate"] = new JsonArray(state.GateProgress.Select(g => (JsonNode)new JsonObject { ["workflow"] = g.WorkflowKey, ["gate"] = g.Gate, ["failures"] = g.Failures }).ToArray()),
            ["firstFailure"] = state.TechnicalStop is { } stop ? new JsonObject { ["code"] = stop.Code, ["phase"] = stop.Phase, ["location"] = stop.Location, ["unverifiable"] = stop.Unverifiable } : null,
            ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["location"] = d.Location, ["rule"] = d.Rule }).ToArray())
        };
        result["modelDecisions"] = result["modelDecisionIds"]!.AsArray().Count;
        return result;
    }
}
