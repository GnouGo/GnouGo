using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>The single eligibility gate for automatic business decisions and human clarification.</summary>
internal static class PlanningClarifications
{
    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, bool allowQuestions, CancellationToken ct)
    {
        if (state.Preparation is null) return;
        bool changed;
        do
        {
            changed = false;
            foreach (var obligation in state.Obligations.Where(o => o.Kind == "business_choice").OrderBy(o => o.Id, StringComparer.Ordinal).ToArray())
            {
                var id = "question_" + obligation.Id;
                var decision = state.BusinessDecisions.SingleOrDefault(d => d.Id == id);
                if (decision is null)
                {
                    if (obligation.EvidenceReferences.Count == 0) Fail(state, null, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "The candidate has no subject evidence.");
                    var subject = PlanningChoiceEvidence.Parent(state, obligation.EvidenceReferences[0]);
                    decision = new() { Id = id, ObligationId = obligation.Id, SubjectReference = subject.Id, EvidenceReferences = [subject.Id],
                        DependencyFingerprint = PlanningChoiceEvidence.Fingerprint(state) };
                    state.BusinessDecisions.Add(decision);
                }
                if (decision.Status == "superseded")
                {
                    decision.SubjectReference = PlanningChoiceEvidence.Parent(state, obligation.EvidenceReferences[0]).Id;
                    decision.EvidenceReferences = [decision.SubjectReference]; decision.Alternatives.Clear(); decision.Constraints.Clear();
                    decision.SelectedChoiceId = null; decision.CustomAnswer = null; decision.Status = "pending";
                    decision.AffectedObligations.Clear(); decision.CompleteDomain = false; decision.ValuePresence = "unknown";
                }
                decision.DependencyFingerprint = PlanningChoiceEvidence.Fingerprint(state);
                if (!decision.EvidenceReferences.All(r => PlanningChoiceEvidence.Current(state, r)))
                    Fail(state, decision, "BUSINESS_CHOICE_EVIDENCE_STALE", "The business decision evidence changed. Reassess the governing intent.");
                if (decision.Status == "runtime" && decision.ResolutionOrigin == "locked_runtime_contract" && !ResolveLockedRuntime(state, obligation, decision))
                    Fail(state, decision, "BUSINESS_CHOICE_PROOF_STALE", "The locked runtime decision contract is no longer established.");
                if (decision.Status == "pending")
                {
                    if (!ResolveLockedRuntime(state, obligation, decision))
                    {
                        try { await PlanningBusinessAnalysis.AnalyzeAsync(state, runtime, decision, ct); }
                        catch (WorkflowRuntimeException error) when (error.Code.StartsWith("BUSINESS_CHOICE_", StringComparison.Ordinal))
                        { Fail(state, decision, error.Code, error.Message); }
                        catch (PlanningConflictException)
                        { Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "The semantic interpretation does not identify valid owned evidence."); }
                    }
                }
                var eligible = Eligible(state, decision);
                if (decision.Status is "runtime" or "resolved")
                {
                    if (decision.SelectedChoiceId is { } selected && !eligible.Any(a => a.Id == selected))
                        Fail(state, decision, "BUSINESS_CHOICE_SELECTION_INVALID", "The selected business behavior no longer satisfies its constraints.");
                    PlanningChoiceEvidence.Event(state, decision, "resolved_" + decision.ResolutionOrigin);
                    continue;
                }
                if (eligible.Count == 0)
                {
                    if (!decision.CompleteDomain || decision.Alternatives.Count == 0 || decision.Alternatives.Any(a => a.ExclusionReferences.Count == 0))
                        Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "An incomplete choice domain is not proof of unsupportedness.");
                    state.Outcome = new PlanningUnsupported([new(decision.ObligationId, "BUSINESS_CHOICE_UNSUPPORTED",
                        decision.Alternatives.SelectMany(a => a.ExclusionReferences).Distinct(StringComparer.Ordinal).ToList())]);
                    state.Status = PlanningStatus.Unsupported;
                    PlanningChoiceEvidence.Event(state, decision, "unsupported"); return;
                }
                if (eligible.Count == 1 || Equivalent(state, eligible))
                {
                    changed = true;
                    Select(state, decision, eligible.OrderBy(a => a.Id, StringComparer.Ordinal).First(), eligible.Count == 1 ? "constraints" : "equivalence");
                    continue;
                }
                decision.Status = "eligible";
            }
        } while (changed);
        var pending = state.BusinessDecisions.Where(d => d.Status == "eligible").OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        foreach (var decision in pending)
            if (!MateriallyDifferent(state, Eligible(state, decision)))
                Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "No distinct observable business outcomes were established; implementation questions are forbidden.");
        if (allowQuestions && pending.FirstOrDefault() is { } question) Ask(state, question, Eligible(state, question));
        await runtime.CheckpointAsync(state, ct);
    }

    private static bool ResolveLockedRuntime(PlanningSnapshot state, PlanningObligation candidate, PlanningBusinessDecision decision)
    {
        var scope = state.References.Single(r => r.Id == candidate.EvidenceReferences[0]);
        var governed = state.Obligations.Where(o => o.Id != candidate.Id && o.EvidenceReferences.Any(id =>
        {
            var reference = state.References.Single(r => r.Id == id);
            return reference.SourceId == scope.SourceId && reference.SourceFingerprint == scope.SourceFingerprint &&
                reference.Start <= scope.Start && reference.Start + reference.Length >= scope.Start + scope.Length;
        })).ToArray();
        var operation = governed.FirstOrDefault(o => state.Preparation!.Interactions.Any(i => i.OperationId == o.Id) ||
            state.Preparation!.Decisions.Any(d => d.SourceOperationId == o.Id || d.EffectOperationIds.Contains(o.Id)));
        if (operation is null) return false;
        var reference = operation.EvidenceReferences[0];
        decision.Status = "runtime"; decision.ResolutionOrigin = "locked_runtime_contract";
        if (!decision.EvidenceReferences.Contains(reference)) decision.EvidenceReferences.Add(reference);
        decision.Alternatives = [new() { Id = "choice_" + PlanningGraphCompiler.Fingerprint(reference)[..16], EvidenceReference = reference,
            Label = PlanningBusinessAnalysis.Label(PlanningChoiceEvidence.Text(state, reference)) }];
        decision.SelectedChoiceId = decision.Alternatives[0].Id;
        return true;
    }

    internal static List<PlanningBusinessAlternative> Eligible(PlanningSnapshot state, PlanningBusinessDecision decision)
    {
        if (decision.Alternatives.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != decision.Alternatives.Count)
            Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "Choice identities must be unique.");
        var capabilities = state.Preparation?.Capabilities ?? [];
        foreach (var rule in decision.Constraints)
        {
            if (!PlanningChoiceEvidence.Current(state, rule.SourceReference) || !decision.EvidenceReferences.Contains(rule.SourceReference) ||
                rule.Origin != PlanningChoiceEvidence.Origin(state, rule.SourceReference) ||
                !decision.Alternatives.Any(a => a.Id == rule.ChoiceId) || rule.Kind is not ("require" or "deny" or "prefer") ||
                rule.Kind == "prefer" && !PlanningBusinessGovernance.CanPrefer(state, rule.SourceReference))
                Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "A constraint lacks current governing evidence.");
            if (rule.Kind != "prefer" && (rule.Applicability == "unknown" || rule.Applicability is "omitted" or "explicit_null" && decision.ValuePresence == "unknown"))
                Fail(state, decision, "BUSINESS_CHOICE_APPLICABILITY_UNPROVEN", "The governing condition needs a deterministic applicability proof before any choice can be authorized.");
        }
        foreach (var alternative in decision.Alternatives)
        {
            alternative.ExclusionCode = null; alternative.ExclusionReferences.Clear();
            if (!PlanningChoiceEvidence.Current(state, alternative.EvidenceReference))
                Fail(state, decision, "BUSINESS_CHOICE_EVIDENCE_STALE", "An alternative is outside the current evidence.");
            foreach (var operation in alternative.OperationIds)
                if (!capabilities.Any(c => c.OperationIds.Contains(operation)) || !state.Obligations.Any(o => o.Id == operation && PlanningSourceDecisions.IsOperation(o)))
                    Fail(state, decision, "BUSINESS_CHOICE_PROOF_UNAVAILABLE", "An alternative lacks a validated operation contract.");
            foreach (var governing in state.BusinessDecisions.Where(d => d.Id != decision.Id && d.Status == "resolved"))
            {
                var selected = governing.Alternatives.Single(a => a.Id == governing.SelectedChoiceId);
                var needed = alternative.OperationIds.Concat(capabilities.Where(c => c.OperationIds.Any(alternative.OperationIds.Contains)).SelectMany(c => c.InputOperationIds));
                if (needed.Any(id => governing.AffectedObligations.Contains(id) && !selected.OperationIds.Contains(id)))
                    Exclude(alternative, "GOVERNING_DECISION", governing.EvidenceReferences);
            }
            foreach (var capability in capabilities.Where(c => c.Required && c.OperationIds.Any(decision.AffectedObligations.Contains) &&
                         c.OperationIds.Any(id => decision.AffectedObligations.Contains(id) && !alternative.OperationIds.Contains(id))))
                Exclude(alternative, "REQUIRED_CAPABILITY", capability.OperationIds.SelectMany(id => state.Obligations.Single(o => o.Id == id).EvidenceReferences));
            foreach (var capability in capabilities.Where(c => c.OperationIds.Any(alternative.OperationIds.Contains) &&
                         c.OperationIds.Any(id => decision.AffectedObligations.Contains(id) && !alternative.OperationIds.Contains(id))))
                Exclude(alternative, "INDIVISIBLE_CAPABILITY", capability.OperationIds.SelectMany(id => state.Obligations.Single(o => o.Id == id).EvidenceReferences));
            foreach (var required in state.Obligations.Where(o => decision.AffectedObligations.Contains(o.Id) && o.Required && !alternative.OperationIds.Contains(o.Id)))
                Exclude(alternative, "REQUIRED_OPERATION", required.EvidenceReferences);
            foreach (var capability in capabilities.Where(c => c.OperationIds.Any(alternative.OperationIds.Contains)))
                foreach (var dependency in capability.InputOperationIds.Where(id => decision.AffectedObligations.Contains(id) && !alternative.OperationIds.Contains(id)))
                    Exclude(alternative, "OPERATION_DEPENDENCY", state.Obligations.Single(o => o.Id == dependency).EvidenceReferences);
            foreach (var rule in PlanningBusinessGovernance.Applicable(state, decision))
            {
                if (rule.Kind == "deny" && rule.ChoiceId == alternative.Id || rule.Kind == "require" && rule.ChoiceId != alternative.Id)
                    Exclude(alternative, "BUSINESS_POLICY", [rule.SourceReference]);
            }
        }
        foreach (var alternative in decision.Alternatives.Where(a => a.ExclusionCode is not null)) PlanningChoiceEvidence.Event(state, decision, "excluded_" + alternative.ExclusionCode);
        return decision.Alternatives.Where(a => a.ExclusionCode is null).ToList();
    }

    private static bool Equivalent(PlanningSnapshot state, IReadOnlyList<PlanningBusinessAlternative> alternatives)
        => alternatives.Select(a => string.Join('|', a.OperationIds.Order(StringComparer.Ordinal)) + ":" + PlanningChoiceEvidence.Text(state, a.EvidenceReference)).Distinct(StringComparer.Ordinal).Count() == 1;

    private static bool MateriallyDifferent(PlanningSnapshot state, IReadOnlyList<PlanningBusinessAlternative> alternatives)
    {
        if (alternatives.Select(a => string.Join('|', a.OperationIds.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count() != alternatives.Count) return false;
        var varying = alternatives.SelectMany(a => a.OperationIds).Where(id => alternatives.Any(a => !a.OperationIds.Contains(id))).Distinct(StringComparer.Ordinal);
        return varying.Any() && varying.All(id => state.Obligations.Single(o => o.Id == id).Kind is "external_read" or "external_write" or "external_execute" &&
            !state.Preparation!.Decisions.Any(d => d.SourceOperationId == id || d.EffectOperationIds.Contains(id)) &&
            state.Preparation!.Capabilities.Where(c => c.OperationIds.Contains(id)).All(c =>
            c.EffectKind is "read" or "write" or "execute" or "lifecycle" && c.Resolution != "local"));
    }

    internal static void Select(PlanningSnapshot state, PlanningBusinessDecision decision, PlanningBusinessAlternative alternative, string origin)
    {
        if (decision.SelectedChoiceId != alternative.Id && decision.AffectedObligations.Count > 0)
        {
            state.BehaviorAssessment = new(); state.BehaviorPlan = null; state.ApprovedBehaviorHash = null;
            PlanningContext.InvalidateArtifact(state);
        }
        decision.SelectedChoiceId = alternative.Id; decision.Status = "resolved"; decision.ResolutionOrigin = origin;
        PlanningChoiceEvidence.Event(state, decision, "resolved_" + origin);
    }

    private static void Ask(PlanningSnapshot state, PlanningBusinessDecision decision, List<PlanningBusinessAlternative> eligible)
    {
        if (PlanningChoiceEvidence.Origin(state, decision.SubjectReference) is not ("intent" or "answer" or "baseline"))
            Fail(state, decision, "CLARIFICATION_EVIDENCE_UNPROVEN", "Host constraints cannot become business questions.");
        var preferences = decision.Constraints.Where(r => r.Kind == "prefer" && PlanningBusinessGovernance.Applies(decision, r) && eligible.Any(a => a.Id == r.ChoiceId)).ToArray();
        var preferred = preferences.Select(r => r.ChoiceId).Distinct(StringComparer.Ordinal).ToArray();
        var preferredId = preferred.Length == 1 ? preferred[0] : null;
        if (preferredId is not null) PlanningChoiceEvidence.Event(state, decision, "preference_" + preferences.First(r => r.ChoiceId == preferredId).Origin);
        var choices = eligible.OrderBy(a => a.Id == preferredId ? 0 : 1).ThenBy(a => a.Id, StringComparer.Ordinal).Take(4)
            .Select(a => new PlanningClarificationChoice(a.Id, a.Label, a.Id == preferredId,
                a.Id == preferredId ? preferences.First(r => r.ChoiceId == a.Id).Origin switch
                { "policy" => "Matches the declared policy preference.", "baseline" => "Preserves the existing workflow preference.", "answer" => "Matches your previous stated preference.", _ => "Matches your declared preference." } : null,
                new[] { a.EvidenceReference }.Concat(preferences.Where(r => r.ChoiceId == a.Id).Select(r => r.SourceReference)).Distinct(StringComparer.Ordinal).ToList())).ToList();
        var question = "Choose the remaining business behavior: " + PlanningChoiceEvidence.Text(state, decision.SubjectReference).Trim();
        var answerSchema = PlanningHoleRequests.Object((decision.Id, new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }));
        state.Outcome = new PlanningNeedUserClarification(new(decision.Id, decision.EvidenceReferences.ToList(), answerSchema, [decision.ObligationId])
        { Question = question, Choices = choices, DependencyFingerprint = decision.DependencyFingerprint });
        var request = new HumanInputRequest
        {
            RunId = state.Request.SessionId, StepId = decision.Id, Prompt = question, Mode = "form", AllowAbandon = true,
            Fields = [new() { Name = decision.Id, Description = question, Type = "radio", Required = true, AllowCustomAnswer = true,
                Options = choices.Select(c => c.Id).ToList(), OptionDefinitions = choices.Select(c => new HumanInputOptionDef
                { Value = c.Id, Description = c.Label + (c.PreferredReason is null ? "" : " — " + c.PreferredReason), Recommended = c.Preferred }).ToList() }]
        };
        PlanningChoiceEvidence.Event(state, decision, "clarification");
        throw new WorkflowRuntimeException("PLANNING_CLARIFICATION_REQUIRED", "A material business decision remains unresolved.",
            details: new JsonObject { ["question"] = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.HumanInputRequest) });
    }

    private static void Exclude(PlanningBusinessAlternative alternative, string code, IEnumerable<string> references)
    { alternative.ExclusionCode = code; alternative.ExclusionReferences.AddRange(references.Where(r => !alternative.ExclusionReferences.Contains(r))); }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(PlanningSnapshot state, PlanningBusinessDecision? decision, string code, string message)
    {
        PlanningContext.Stop(state, code, message, decision is null ? "/obligations" : "/obligations/@" + decision.ObligationId);
        throw new WorkflowRuntimeException(code, message, details: new JsonObject { ["location"] = decision is null ? "/obligations" : "/obligations/@" + decision.ObligationId });
    }
}
