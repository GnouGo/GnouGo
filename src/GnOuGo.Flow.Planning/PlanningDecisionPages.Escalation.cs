using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningDecisionPages
{
    private static bool TryEscalate(PlanningSnapshot state, Dispatch dispatch, LLMResponse response)
    {
        var page = dispatch.Page; var parent = dispatch.Call;
        if (dispatch.Decisions.Length != 1 || page.Decisions.Count != 1 || page.OutputBudgetEscalation is not null ||
            parent.Request.MaxTokens != PlanningGenerationPolicy.NormalOutputTokens || state.Request.Generation.MaxOutputTokens != PlanningGenerationPolicy.NormalOutputTokens)
            return false;
        var decision = dispatch.Decisions[0]; var canonical = decision.CorrectionId ?? decision.Id;
        var owners = CorrectionIdentities(decision).ToHashSet(StringComparer.Ordinal);
        if (state.DecisionPages.Any(p => p.WorkflowKey == page.WorkflowKey && p.OutputBudgetEscalation is { } used &&
            (used.CanonicalDecisionId == canonical || (p.SourceDecisionIds ?? [used.CanonicalDecisionId]).Any(owners.Contains)) &&
            used.EvidenceFingerprint == decision.EvidenceFingerprint)) return false;
        var accounting = state.RequestAccounting.SingleOrDefault(a => a.Id == parent.Id);
        if (accounting?.Evidence != "receipt" || accounting.ReceiptFingerprint != PlanningGenerationPolicy.ReceiptFingerprint(response) ||
            parent.Id != page.RequestId || parent.RequestHash != PlanningGenerationPolicy.RequestFingerprint(parent.Request) ||
            parent.ScopeFingerprint != page.Id || !page.Decisions.SequenceEqual(dispatch.Decisions.Select(d => d.Id), StringComparer.Ordinal))
            throw new PlanningConflictException("The singleton escalation lacks current owned receipt evidence.");
        var proof = new PlanningOutputBudgetEscalation(page.Id, parent.Id, parent.RequestHash, accounting.ReceiptFingerprint,
            decision.Id, canonical, decision.EvidenceFingerprint);
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(parent.Request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        request.ClientRequestId = null; request.MaxTokens = PlanningGenerationPolicy.EscalatedOutputTokens; request.OutputBudgetEscalation = proof;
        PlanningGenerationPolicy.ValidateOutputEscalation(request, parent.Request, response, state.Request.SessionId);
        var phase = dispatch.SourcePhase;
        var child = Page(state, phase, page.WorkflowKey, dispatch.Decisions, page.Id, page.Correction, page.Gate, PlanningDecisionPageOrigin.OutputBudgetEscalation);
        var call = PlanningModelCalls.Reserve(state, page.Phase, page.WorkflowKey, request, page.Gate, child.Id, repair: false);
        child.OutputBudgetEscalation = proof; child.RequestId = call.Id; child.EffectiveOutputTokens = call.Request.MaxTokens;
        page.OutputEscalationChildId = child.Id; page.Status = "escalating";
        if (decision.HoleId is { } holeId)
        {
            PlanningConvergence.Expose(state, state.Construction.Holes.Where(h => h.Id == holeId && h.WorkflowKey == page.WorkflowKey), call.Id);
            foreach (var (id, reason) in accounting.HoleReasons) state.RequestAccounting.Single(a => a.Id == call.Id).HoleReasons[id] = reason;
        }
        return true;
    }

    private static PlanningDecisionPage EscalationChild(PlanningSnapshot state, PlanningDecisionPage parent, string phase, Decision[] decisions)
    {
        var child = Page(state, phase, parent.WorkflowKey, decisions, parent.Id, parent.Correction, parent.Gate,
            PlanningDecisionPageOrigin.OutputBudgetEscalation, create: false);
        var proof = child.OutputBudgetEscalation;
        var original = state.RequestAccounting.SingleOrDefault(a => a.Id == parent.RequestId);
        if (decisions.Length != 1 || parent.PartitionChildren.Count != 0 || parent.OutputEscalationChildId != child.Id || proof is null || proof.Level != 1 ||
            proof.ParentPageId != parent.Id || proof.ParentRequestId != parent.RequestId || original?.Evidence != "receipt" ||
            proof.ParentReceiptFingerprint != original.ReceiptFingerprint || !proof.ParentRequestId.EndsWith(":" + proof.ParentRequestHash, StringComparison.Ordinal) ||
            proof.DecisionId != decisions[0].Id || proof.CanonicalDecisionId != (decisions[0].CorrectionId ?? decisions[0].Id) ||
            proof.EvidenceFingerprint != decisions[0].EvidenceFingerprint || child.EffectiveOutputTokens != PlanningGenerationPolicy.EscalatedOutputTokens ||
            state.DecisionPages.Count(p => p.ParentId == parent.Id && p.Origin == PlanningDecisionPageOrigin.OutputBudgetEscalation) != 1)
            throw new PlanningConflictException("The retained escalation does not match its parent and canonical decision evidence.");
        return child;
    }
}
