using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static PlanningRequirements CloneConstructionRequirements(PlanningSession state) => state.Requirements is null
        ? new PlanningRequirements { Summary = state.Request.Prompt, Inputs = state.Plan is null ? [] : PlanningConstruction.Clone(state.Plan).Inputs, Outputs = [] }
        : JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            PlanningJsonContext.Default.PlanningRequirements)!;

    private static async Task ClassifyConstructionIntentAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionSource source, CancellationToken ct)
    {
        var compact = PlanningCompactConstruction.State(state);
        var coalesceInterfaces = state.PendingCall is null ||
            state.PendingCall.Request.StructuredOutputSchema?["description"]?.ToString() == PlanningCompactConstruction.CoalescedInterfaceDescription;
        var preceding = source.ContinuationOf is null ? null :
            compact.Sources.Last(s => s.Id.StartsWith(source.ContinuationOf + ":clause:", StringComparison.Ordinal));
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.IntentPurpose, () =>
            PlanningCompactConstruction.Request(PlanningCompactConstruction.ClassifyInstructions, new JsonObject
            {
                ["sources"] = new JsonArray(new JsonObject { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text,
                    ["continuationOf"] = source.ContinuationOf }),
                ["continuation"] = preceding is null ? null : new JsonObject
                { ["id"] = preceding.Id, ["version"] = preceding.Version, ["text"] = preceding.Text, ["kind"] = preceding.Kind },
                ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics)
            }, PlanningCompactConstruction.ClassificationSchema(source), PlanningCompactConstruction.IntentPurpose), ct);
        var clauses = response["clauses"]!.AsArray();
        if (response["sourceId"]!.ToString() != source.Id || string.Concat(clauses.Select(c => c!["text"]!.ToString())) != source.Text)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/clauses", "Verbatim clauses must cover the entire exact original source page, in order, without omissions or duplication.");
        if (clauses.Any(c => c!["kind"]!.ToString() == "requirement" && string.IsNullOrWhiteSpace(c["text"]!.ToString())))
            PlanningConstruction.Fail("REQUIREMENTS_INVALID", "/clauses", "Whitespace cannot be a business requirement; preserve it in an adjacent clause or global context.");
        if (preceding is not null && clauses[0]!["kind"]!.ToString() != preceding.Kind)
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_KIND", "/clauses/0/kind", "The continued clause must retain its preceding requirement or global classification.");
        state.Requirements ??= CloneConstructionRequirements(state);
        compact.InterfaceExtractions ??= [];
        PlanningConstructionInterfaceExtraction? pageExtraction = null;
        for (var i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i]!;
            var text = clause["text"]!.ToString(); var kind = clause["kind"]!.ToString(); var id = source.Id + ":clause:" + i;
            var direction = clause["interfaces"]!.ToString();
            compact.Sources.Add(new() { Id = id, Text = text, Kind = kind, Indexed = true,
                Version = PlanningGraphCompiler.Fingerprint(text), InterfaceKind = direction });
            if (kind == "global") compact.GlobalSourceIds.Add(id);
            else
            {
                // Continuation merges can remove an earlier identifier; never reuse an extant one.
                var number = compact.RequirementSources.Count;
                while (compact.RequirementSources.ContainsKey("req_" + number)) number++;
                var requirementId = "req_" + number;
                state.Requirements.Outcomes.Add(new(requirementId, text));
                compact.RequirementSources.Add(requirementId, [id]);
                state.Construction!.Requirements.Add(new() { Id = requirementId });
            }
            if (direction != "none")
            {
                // Adjacent declarations and their exact naming/type constraints
                // are inspected together before accepting any interface from this page.
                if (pageExtraction is null || !coalesceInterfaces)
                {
                    pageExtraction = new() { Id = id };
                    compact.InterfaceExtractions.Add(pageExtraction);
                }
                pageExtraction.SourceIds.Add(id);
                pageExtraction.Inputs |= direction is "input" or "both";
                pageExtraction.Outputs |= direction is "output" or "both";
            }
        }
        PlanningCompactConstruction.MergeContinuation(state, source);
        source.Indexed = true;
        state.Diagnostics.Clear();
        // Required interface work remains ahead of generation/review, including an
        // explicit revision's retained candidate. No partial declarations are accepted.
        if (state.Construction!.Candidate is { } candidate && PlanningCompactConstruction.IntentDeclarationsComplete(compact) &&
            compact.Sources.All(s => s.Indexed))
        {
            candidate.LocalReviewed = false; candidate.IntermediateReviewed = false;
            await ValidateConstructionCandidateAsync(state, runtime, ct);
        }
    }

    private static async Task ExtractConstructionInterfacesAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionInterfaceExtraction extraction, CancellationToken ct)
    {
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.IntentPurpose, () =>
            PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceInstructions,
                PlanningCompactConstruction.InterfaceContext(state, extraction), PlanningCompactConstruction.InterfaceSchema(extraction),
                PlanningCompactConstruction.IntentPurpose), ct);
        if (response["sourceId"]!.ToString() != extraction.Id)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/sourceId", "Use the exact issued interface declaration source.");
        var compact = PlanningCompactConstruction.State(state);
        var source = compact.Sources.Single(s => s.Id == extraction.SourceIds[0]);
        var page = compact.Sources.Single(s => source.Id.StartsWith(s.Id + ":clause:", StringComparison.Ordinal));
        var requirements = ReadConstructionInterfaces(state, response, page.Kind);
        state.Requirements = requirements;
        extraction.Complete = true;
        state.Diagnostics.Clear();
        if (state.Construction!.Candidate is { } candidate && PlanningCompactConstruction.IntentDeclarationsComplete(compact))
        {
            candidate.LocalReviewed = false; candidate.IntermediateReviewed = false;
            await ValidateConstructionCandidateAsync(state, runtime, ct);
        }
    }
}
