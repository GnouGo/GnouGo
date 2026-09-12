using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningBusinessAnswers
{
    internal static async Task AcceptAsync(PlanningSnapshot state, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.Status != PlanningStatus.Clarification || state.Intent.Question is null || command.Answers is null ||
            state.Outcome is not PlanningNeedUserClarification question ||
            PlanningContractValidation.ValidateInstance(command.Answers, question.Decision.AnswerSchema).Count != 0)
            throw new PlanningConflictException("No matching typed clarification answer is pending.");
        var decision = state.BusinessDecisions.SingleOrDefault(d => d.Id == question.Decision.DecisionId);
        if (decision is null || question.Decision.DependencyFingerprint is null)
        {
            // An old question has no eligibility proof. Explicit interaction re-evaluates it;
            // its old recommendation and proposed answer grant no authority.
            state.Intent.Question = null; state.Outcome = null; state.Status = PlanningStatus.Created;
            state.CurrentPhase = state.Preparation is null ? PlanningPhase.Capabilities : PlanningPhase.Behavior;
            return;
        }
        if (decision.DependencyFingerprint != question.Decision.DependencyFingerprint || decision.DependencyFingerprint != PlanningChoiceEvidence.Fingerprint(state) || !decision.EvidenceReferences.All(r => PlanningChoiceEvidence.Current(state, r)))
            throw new PlanningConflictException("The decision evidence changed. Reload the current question.");
        if (command.Answers.Count != 1 || command.Answers[decision.Id] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new PlanningConflictException("Submit a choice or a nonempty custom answer for this decision.");
        var eligible = PlanningClarifications.Eligible(state, decision);
        var selected = eligible.SingleOrDefault(a => a.Id == text);
        if (selected is not null && !question.Decision.Choices.Any(c => c.Id == selected.Id) || selected is null && text.StartsWith("choice_", StringComparison.Ordinal))
            throw new PlanningConflictException("The choice is outside the current displayed scope.");
        if (selected is null)
        {
            decision.CustomAnswer = text.Trim();
            var schema = PlanningHoleRequests.Enum(["unresolved", .. eligible.Select(a => a.Id)]);
            decision.CustomAnswerFingerprint = PlanningGraphCompiler.Fingerprint(decision.DependencyFingerprint + ":" + text);
            var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "business_answer", "$plan",
                [new(decision.Id, schema, new JsonObject
                {
                    ["answer"] = text, ["subject"] = PlanningChoiceEvidence.Text(state, decision.SubjectReference),
                    ["choices"] = new JsonObject(eligible.Select(a => new KeyValuePair<string, JsonNode?>(a.Id,
                        JsonValue.Create(PlanningChoiceEvidence.Text(state, a.EvidenceReference))))),
                    ["task"] = "Select the existing business outcome explicitly expressed by the answer. If ambiguous, conflicting or requesting different behavior, select unresolved. Do not infer consent or authorize a new operation."
                }, decision.CustomAnswerFingerprint)], ct);
            selected = eligible.SingleOrDefault(a => a.Id == response[decision.Id]!.ToString());
            if (selected is null)
            {
                state.Diagnostics = [new("BUSINESS_ANSWER_UNRESOLVED", "/decisions/" + decision.Id,
                    "The answer does not identify an admissible outcome. Select a choice, clarify your answer, or explicitly revise the intent for different behavior.")];
                return;
            }
        }
        PlanningClarifications.Select(state, decision, selected, "answer");
        decision.ValuePresence = "explicit_value";
        state.Intent.Answers.Add(new(question.Decision.Question, new JsonObject { [decision.Id] = selected.Id }));
        state.Intent.Question = null; state.Outcome = null; state.TechnicalStop = null; state.Diagnostics.Clear();
        state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Behavior;
        // Intent pages, unrelated decisions, discovery, receipts and allowances remain retained.
        // Locked capabilities remain available for proof; only their selected behavior is rebuilt.
        state.BehaviorAssessment = new(); state.BehaviorPlan = null; state.ApprovedBehaviorHash = null;
        PlanningContext.InvalidateArtifact(state);
    }

    internal static bool Included(PlanningSnapshot state, string operation) => state.BusinessDecisions
        .Where(d => d.Status == "resolved" && d.AffectedObligations.Contains(operation))
        .All(d => d.Alternatives.Single(a => a.Id == d.SelectedChoiceId).OperationIds.Contains(operation));

    internal static IEnumerable<PlanningDiagnostic> ValidateBehavior(PlanningSnapshot state, PlanningBehaviorPlan plan)
    {
        var operations = plan.Workflows.SelectMany(w => PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally)))
            .SelectMany(n => n.OperationIds.Concat(state.Preparation!.Capabilities.Where(c => c.Id == n.CapabilityId).SelectMany(c => c.OperationIds))).ToHashSet(StringComparer.Ordinal);
        foreach (var decision in state.BusinessDecisions.Where(d => d.Status != "superseded"))
        {
            if (!decision.EvidenceReferences.All(id => PlanningChoiceEvidence.Current(state, id)))
                yield return new("BUSINESS_CHOICE_EVIDENCE_STALE", "/decisions/" + decision.Id, "Business decision evidence changed after resolution.");
            if (decision.Status is not ("runtime" or "resolved") || decision.SelectedChoiceId is null)
            { yield return new("BUSINESS_CHOICE_UNRESOLVED", "/decisions/" + decision.Id, "A business decision has not been resolved."); continue; }
            var selected = decision.Alternatives.Single(a => a.Id == decision.SelectedChoiceId);
            if (decision.AffectedObligations.Any(id => operations.Contains(id) != selected.OperationIds.Contains(id)))
                yield return new("BUSINESS_CHOICE_BEHAVIOR_CONFLICT", "/decisions/" + decision.Id, "The behavior differs from the selected business outcome. Renew behavior review.");
        }
    }

    internal static string ContractFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(new JsonObject(
        state.BusinessDecisions.Where(d => d.Status is "runtime" or "resolved").OrderBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => new KeyValuePair<string, JsonNode?>(d.Id, new JsonObject
            {
                ["choice"] = d.SelectedChoiceId,
                ["sources"] = new JsonArray(d.EvidenceReferences.Order(StringComparer.Ordinal).Select(id =>
                    (JsonNode?)JsonValue.Create(id + ":" + state.References.Single(r => r.Id == id).SourceFingerprint)).ToArray())
            }))).ToJsonString());

    internal static string Describe(PlanningSnapshot state, string id, JsonNode? value)
    {
        var decision = state.BusinessDecisions.SingleOrDefault(d => d.Id == id && d.SelectedChoiceId == value?.ToString());
        return decision is null ? value?.ToJsonString() ?? "" : PlanningChoiceEvidence.Text(state, decision.Alternatives.Single(a => a.Id == decision.SelectedChoiceId).EvidenceReference);
    }
}
