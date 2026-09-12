using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningIntentAssessment(TimeProvider time)
{
    internal sealed record IntentSource(string Id, string Kind, string Text, string? QuestionContext = null);
    internal async Task AssessAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningSourceDecisions.InterpretAsync(state, runtime, ct);
        state.Events.Add(new("intent_interpreted", PlanningPhase.Intent, time.GetUtcNow(), state.Obligations.Count));
    }

    internal static List<IntentSource> IntentSources(PlanningSnapshot state)
    {
        var sources = new List<IntentSource> { new("request", "user_request", state.Request.Prompt) };
        if (state.Request.Options["policy"]?["instructions"]?.GetValue<string>() is { Length: > 0 } context)
            sources.Add(new("host", "host_constraint", context));
        if (PlanningContext.BaselineText(state) is { } existing)
            sources.Add(new("existing", "existing_workflow", existing));
        for (var index = 0; index < state.Intent.Answers.Count; index++)
        {
            var answer = state.Intent.Answers[index];
            var field = 0;
            foreach (var entry in answer.Answers.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (state.BusinessDecisions.Any(d => d.Id == entry.Key && d.SelectedChoiceId == entry.Value?.ToString())) continue;
                var text = ReadText(entry.Value) ?? entry.Value?.ToJsonString() ?? "";
                sources.Add(new($"answer_{index}_{field++}", "user_answer", text, answer.Question + "\nField: " + entry.Key));
            }
        }
        foreach (var decision in state.BusinessDecisions.Where(d => d.CustomAnswer is not null && d.Status != "superseded"))
            sources.Add(new("business_answer_" + decision.Id, "user_answer", decision.CustomAnswer!));
        return sources;
    }

    private static string? ReadText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static void RecordClarification(PlanningSnapshot state, int questions)
    {
        state.Intent.Forms++;
        state.Intent.Questions += questions;
    }

    internal static void ArchiveIntent(PlanningSnapshot state)
        => state.Intent.History.Add(new(state.Revision, state.Request.Prompt, state.Intent.Answers.ToList(), state.Diagnostics.ToList()));
}
