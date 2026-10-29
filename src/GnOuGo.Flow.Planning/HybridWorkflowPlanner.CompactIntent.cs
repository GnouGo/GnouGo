using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static PlanningRequirements CloneConstructionRequirements(PlanningSession state) => state.Requirements is null
        ? new PlanningRequirements { Summary = state.Request.Prompt, Inputs = state.Plan is null ? [] : PlanningConstruction.Clone(state.Plan).Inputs, Outputs = [] }
        : JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            PlanningJsonContext.Default.PlanningRequirements)!;

    private static async Task IndexConstructionIntentAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionSource source, CancellationToken ct)
    {
        using var correction = new PlanningCorrectionAttempt(state, state.Diagnostics.Any(d => d.Required));
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.IntentPurpose,
            () => PlanningCompactConstruction.IntentRequest(state, source), ct, correction);
        if (new[] { "clauses", "evidenceRequests", "clarifications" }.Count(name => response[name] is not null) != 1)
            PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return one complete intent index, missing original evidence request or clarification.");
        if (response["evidenceRequests"] is JsonArray evidence)
        {
            PlanningConstruction.LoadEvidence(state, evidence.Select(item => item!.ToString()).ToList(), followup: next =>
            {
                var request = PlanningCompactConstruction.IntentRequest(next, PlanningCompactConstruction.State(next).Sources.Single(s => s.Id == source.Id));
                return PlanningModelCalls.PrepareRequest(next, PlanningCompactConstruction.IntentPurpose,
                    request.Prompt, request.StructuredOutputSchema!.AsObject());
            }, purpose: PlanningCompactConstruction.IntentPurpose);
            correction.EvidenceAccepted();
            return;
        }
        if (response["clarifications"] is JsonArray questions)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            correction.EvidenceAccepted();
            return;
        }
        var clauses = response["clauses"]!.AsArray();
        if (string.Concat(clauses.Select(clause => clause!["text"]!.ToString())) != source.Text)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/clauses", "Verbatim clauses must cover the entire exact original source, in order, without omissions or duplication.");
        if (clauses.Any(clause => clause!["kind"]!.ToString() == "requirement" && string.IsNullOrWhiteSpace(clause["text"]!.ToString())))
            PlanningConstruction.Fail("REQUIREMENTS_INVALID", "/clauses", "Whitespace cannot be a business requirement.");
        var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var compact = PlanningCompactConstruction.State(next);
        var original = compact.Sources.Single(s => s.Id == source.Id && s.Version == source.Version);
        if (source.ContinuationOf is not null && compact.Sources.Last(s => s.Id.StartsWith(source.ContinuationOf + ":clause:", StringComparison.Ordinal)).Kind != clauses[0]!["kind"]!.ToString())
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_KIND", "/clauses/0/kind", "The continued clause must retain its preceding requirement or global classification.");
        next.Requirements ??= CloneConstructionRequirements(next);
        for (var index = 0; index < clauses.Count; index++)
        {
            var clause = clauses[index]!;
            // Reuse the existing interface compiler preflight before committing any source progress.
            next.Requirements = ReadConstructionInterfaces(next, clause, source.Kind);
            var text = clause["text"]!.ToString(); var kind = clause["kind"]!.ToString(); var id = source.Id + ":clause:" + index;
            compact.Sources.Add(new() { Id = id, Version = PlanningGraphCompiler.Fingerprint(text), Text = text, Kind = kind, Indexed = true,
                Inputs = clause["inputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)!,
                Outputs = clause["outputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)! });
            if (kind == "global") compact.GlobalSourceIds.Add(id);
            else
            {
                var number = compact.RequirementSources.Count;
                while (compact.RequirementSources.ContainsKey("req_" + number)) number++;
                var requirement = "req_" + number;
                next.Requirements.Outcomes.Add(new(requirement, text));
                compact.RequirementSources.Add(requirement, [id]);
                next.Construction!.Requirements.Add(new() { Id = requirement });
            }
        }
        PlanningCompactConstruction.MergeContinuation(next, original);
        original.Indexed = true;
        next.Diagnostics.Clear();
        if (next.Construction!.Candidate is { } candidate && compact.Sources.All(s => s.Indexed))
        {
            candidate.BusinessReviewed = false;
            await ValidateConstructionCandidateAsync(next, runtime, ct);
        }
        state.Requirements = next.Requirements;
        state.Construction = next.Construction;
        state.Diagnostics = next.Diagnostics;
        state.Phase = next.Phase;
    }
}
