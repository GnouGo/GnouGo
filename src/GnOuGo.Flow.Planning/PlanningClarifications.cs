using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static class PlanningClarifications
{
    internal static async Task AskAfterDiscoveryAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        foreach (var obligation in state.Obligations.Where(o => o.Kind == "business_choice" && o.Owner == "business_decision"))
        {
            var id = "question_" + obligation.Id;
            if (state.Intent.Answers.Any(a => a.Answers.ContainsKey(id)) || state.Intent.History.Any(h => h.Answers.Any(a => a.Answers.ContainsKey(id)))) continue;
            var sources = PlanningIntentAssessment.IntentSources(state).ToDictionary(s => s.Id, StringComparer.Ordinal);
            if (obligation.EvidenceReferences.Count == 0 || obligation.EvidenceReferences.Any(r =>
                state.References.SingleOrDefault(e => e.Id == r) is not { } reference || !sources.TryGetValue(reference.SourceId, out var source) ||
                source.Kind is not ("user_request" or "user_answer" or "existing_workflow")))
                throw new WorkflowRuntimeException("CLARIFICATION_EVIDENCE_UNPROVEN", "A business question requires current user-owned intent evidence.");
            var text = PlanningSourceDecisions.Text(state, obligation);
            var schema = PlanningHoleRequests.Object(("question", Text(256)), ("first", Text(128)), ("second", Text(128)));
            var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "clarification", "$plan",
                [new(id, schema, new JsonObject { ["businessDecision"] = text,
                    ["task"] = "Describe the missing business choice with two distinct alternatives. Discovery has completed. Never ask about tool availability, working directories, file contents, schemas, available checks or any other runtime-observable fact." }, PlanningGraphCompiler.Fingerprint(text))], ct);
            var question = response[id]!;
            if (question["first"]!.ToString() == question["second"]!.ToString())
                throw new WorkflowRuntimeException("CLARIFICATION_OPTIONS_INVALID", "The business alternatives must be distinct.");
            var answerSchema = PlanningHoleRequests.Object((id, Text(512)));
            state.Outcome = new PlanningNeedUserClarification(new(id, obligation.EvidenceReferences.ToList(), answerSchema, [obligation.Id]));
            var request = new HumanInputRequest
            {
                RunId = state.Request.SessionId, StepId = id, Prompt = question["question"]!.ToString(), Mode = "form", AllowAbandon = true,
                Fields = [new() { Name = id, Description = question["question"]!.ToString(), Type = "radio", Required = true, AllowCustomAnswer = true,
                    Options = ["choice_0", "choice_1"], OptionDefinitions = [
                        new() { Value = "choice_0", Description = question["first"]!.ToString(), Recommended = true },
                        new() { Value = "choice_1", Description = question["second"]!.ToString(), Recommended = false }] }]
            };
            throw new WorkflowRuntimeException("PLANNING_CLARIFICATION_REQUIRED", "A business decision needs user clarification.",
                details: new JsonObject { ["question"] = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.HumanInputRequest) });
        }
        static JsonObject Text(int length) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = length };
    }
}
