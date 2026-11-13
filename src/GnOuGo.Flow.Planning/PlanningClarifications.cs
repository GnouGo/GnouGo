using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Deterministic interaction validation. Answers are intent, never executable bindings.</summary>
internal static class PlanningClarifications
{
    private const string ContextKey = "clarification_context";
    internal static void Suspend(PlanningSession state, List<PlanningQuestion> questions)
    {
        var diagnostic = PlanningTokenThresholds.OptimizationAdvisory(state.Request);
        ValidateQuestions(questions, diagnostic);
        if (state.AnswerHistory?.Any(batch => batch.Questions.Any(old => questions.Any(next =>
            old.Question == next.Question && old.Recommended == next.Recommended && old.Alternatives.SequenceEqual(next.Alternatives)))) == true)
            throw new WorkflowRuntimeException("CLARIFICATION_NO_PROGRESS", "An answered question was repeated. Retain the recorded decision; no additional question or allowance was created.");
        if (!PlanningModelCalls.HasCallCapacity(state) || PlanningModelCalls.IsRepair(state) && !PlanningModelCalls.HasRepairCapacity(state))
            throw new WorkflowRuntimeException("CLARIFICATION_CONTINUATION_UNAVAILABLE", "The remaining planning allowance cannot use another answer. Review the retained diagnostics; answering cannot reset calls or correction attempts.");
        if (diagnostic)
        {
            if (questions.Count > 3)
                PlanningOptimizationThresholds.Record(state, "clarification", "clarification_questions", questions.Count, 3);
            foreach (var (question, index) in questions.Select((question, index) => (question, index)))
                if (question.Alternatives.Count > 3)
                    PlanningOptimizationThresholds.Record(state, "clarification", "clarification_alternatives", question.Alternatives.Count, 3,
                        new JsonObject { ["questionIndex"] = index });
        }
        state.Request.Options[ContextKey] = new JsonObject
        {
            ["version"] = 1, ["baseline"] = Baseline(state), ["requirements"] = Requirements(state),
            ["scope"] = Scope(state)
        };
        Activity.Current?.AddEvent(new("planning.clarification.requested", tags: new ActivityTagsCollection
        { ["question.ids"] = string.Join(',', questions.Select(q => q.Id)), ["question.count"] = questions.Count,
            ["diagnostic.locations"] = string.Join(',', state.Diagnostics.Where(d => d.Required).Take(8)
                .Select(d => d.Location[..Math.Min(d.Location.Length, 192)])) }));
    }

    private static string Baseline(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(state.Plan ?? state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
    private static string Requirements(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
    private static string Scope(PlanningSession state) => PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["editablePaths"] = new JsonArray(state.EditablePaths?.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray() ?? []),
        ["revisionScope"] = new JsonArray(state.RevisionScope.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)
    }.ToJsonString());

    internal static void ValidateQuestions(List<PlanningQuestion> questions, bool diagnostic = false)
    {
        if (questions.Count < 1 || !diagnostic && questions.Count > 3 || questions.Any(q => q is null) ||
            questions.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count ||
            questions.Any(q => string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Question) || q.Alternatives is null ||
                q.Alternatives.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Description)) ||
                q.Alternatives.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != q.Alternatives.Count ||
                (q.Alternatives.Count == 0 ? q.Recommended is not null :
                    q.Alternatives.Count < 2 || !diagnostic && q.Alternatives.Count > 3 || !q.Alternatives.Any(a => a.Id == q.Recommended))))
            throw new PlanningResponseException([new("CLARIFICATION_INVALID", "/clarifications", diagnostic
                ? "Ask distinct nonblank questions; offer at least two distinct alternatives with one recommendation, or no alternatives for missing facts."
                : "Ask one to three distinct questions; offer two or three distinct alternatives with one recommendation, or no alternatives for missing facts.")]);
    }

    internal static void Answer(PlanningSession state, PlanningCommand command)
    {
        if (state.Status != PlanningStatus.Clarification || state.PendingCall is not null)
            throw new PlanningConflictException("No clarification is awaiting an answer.");
        var diagnostic = PlanningTokenThresholds.OptimizationAdvisory(state.Request);
        var questions = state.GetQuestions().ToList();
        var answers = command.Answers;
        if (questions.Count == 0 || answers is null || answers.Any(a => a is null) ||
            !answers.Select(a => a.QuestionId).Order(StringComparer.Ordinal).SequenceEqual(questions.Select(q => q.Id).Order(StringComparer.Ordinal)))
            throw new ArgumentException("Answer exactly the pending questions.");
        foreach (var answer in answers)
        {
            var question = questions.Single(q => q.Id == answer.QuestionId);
            if ((answer.AlternativeId is null) == (answer.Text is null) ||
                answer.AlternativeId is not null && !question.Alternatives.Any(a => a.Id == answer.AlternativeId) ||
                answer.Text is not null && (string.IsNullOrWhiteSpace(answer.Text) || !diagnostic && answer.Text.Length > 8192))
                throw new ArgumentException(diagnostic ? "Select a declared alternative or supply a nonblank custom answer."
                    : "Select a declared alternative or supply a nonblank custom answer of at most 8192 characters.");
        }
        // The existing choose command is the model-free path for finite literal selections.
        if (state.PendingQuestions is null && answers.All(a => a.Text is null))
            throw new ArgumentException("Use choose for declared literal alternatives.");
        var context = state.Request.Options[ContextKey] as JsonObject;
        if (context is null)
            throw new PlanningConflictException("The clarification has no retained intent-preserving scope. Use an explicit revision; accepted requirements remain unchanged.");
        if (context["version"]?.ToString() != "1" || context["baseline"]?.ToString() != Baseline(state) || context["requirements"]?.ToString() != Requirements(state) ||
            context["scope"]?.ToString() != Scope(state))
            throw new PlanningConflictException("The clarification baseline or correction scope changed. Reload the current decision.");
        // A custom answer to a compiled literal choice can change its domain. Keep
        // that change on the explicit revision path instead of granting edit authority.
        if (state.PendingQuestions is null)
            throw new PlanningConflictException("A different business alternative requires an explicit revision. The accepted requirements and choices remain unchanged.");
        if (diagnostic)
            foreach (var (answer, index) in answers.Select((answer, index) => (answer, index)))
                if (answer.Text is { Length: > 8192 } text)
                    PlanningOptimizationThresholds.Record(state, "clarification_answer", "clarification_answer_characters", text.Length, 8192,
                        new JsonObject { ["answerIndex"] = index });
        state.AnswerHistory ??= [];
        state.AnswerHistory.Add(new(state.Revision, questions, [.. answers]));
        state.Request.Options.Remove(ContextKey);
        state.PendingQuestions = null; state.Graph = null; state.Yaml = null; state.ApprovedHash = null;
        state.Status = PlanningStatus.Generating;
        Activity.Current?.AddEvent(new("planning.clarification.answered", tags: new ActivityTagsCollection
        { ["question.ids"] = string.Join(',', questions.Select(q => q.Id)), ["question.count"] = questions.Count }));
    }

    internal static void Revise(PlanningSession state, bool preserveRequirements = false, List<string>? editablePaths = null)
    {
        state.Request.Options.Remove(ContextKey);
        state.Request.Baseline = state.Plan ?? state.Request.Baseline;
        if (state.Requirements is not null)
            state.Request.RevisionContext = "Preserve unrelated accepted outcomes and inputs when applying the explicit user revision. Previous requirements:\n" +
                PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements))!.ToJsonString();
        if (!preserveRequirements) state.Requirements = null;
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        state.Request.Options["mapping_profile"] = TaskPlanCompiler.AdaptiveMappingProfile;
        state.EditablePaths = editablePaths?.Order(StringComparer.Ordinal).ToList();
        if (editablePaths is null) state.Plan = null;
        state.Graph = null; state.PendingQuestions = null;
        state.Diagnostics.Clear(); state.ValidationResults.Clear(); state.RevisionScope.Clear();
        if (state.EditablePaths is not null) state.RevisionScope.AddRange(state.EditablePaths);
        state.Yaml = null; state.ApprovedHash = null; state.Status = PlanningStatus.Generating;
    }

    internal static bool SameInputs(List<TaskInput>? left, List<TaskInput>? right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left?.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(), PlanningJsonContext.Default.ListTaskInput),
        JsonSerializer.SerializeToNode(right?.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(), PlanningJsonContext.Default.ListTaskInput));

    internal static List<PlanningDiagnostic> InputFindings(PlanningSession state) => state.Requirements?.Inputs is { } inputs &&
        state.Plan is { } plan && !SameInputs(inputs, plan.Inputs)
        ? [new("REQUIREMENTS_INPUTS_CHANGED", "/inputs", "Preserve the accepted caller inputs, types, requiredness and defaults. Additional or changed inputs require an explicit user revision.")]
        : [];

    internal static List<PlanningDiagnostic> OutputFindings(PlanningSession state, PlanningGraph graph)
    {
        if (state.Requirements?.Outputs is not { } accepted) return [];
        var actual = graph.Workflows.Single(w => w.Key == graph.Entrypoint).Outputs;
        var findings = new List<PlanningDiagnostic>();
        foreach (var output in actual.Where(o => accepted.All(a => a.Name != o.Name)))
            findings.Add(new("REQUIREMENTS_OUTPUTS_CHANGED", "/outputs/" + output.Name, "Additional public outputs require an explicit revision of the accepted business interface."));
        foreach (var output in accepted)
        {
            var produced = actual.SingleOrDefault(o => o.Name == output.Name);
            if (produced is null && !output.Required) continue;
            if (produced is null || !PlanningContractCompatibility.Fits(PlanningGraphCompiler.ToJsonSchema(produced.Schema, state.Catalog!), TaskPlanCompiler.TypeSchema(output.Type, state.Request)))
                findings.Add(new("REQUIREMENTS_OUTPUTS_CHANGED", "/outputs/" + output.Name, "Preserve the accepted output type, requiredness and nullability. A missing or incompatible result requires a corrected plan or explicit intent revision."));
        }
        return findings;
    }
}
