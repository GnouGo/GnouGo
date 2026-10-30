using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string BusinessSelectionContract = "business_selection_contract";
    internal const string SelectGuidance = """
        Select the next coherent business responsibility from the original request, available contributions and remaining obligations. Describe its objective, information needs, expected results, business dependencies and observable success criteria. Preserve user-required ordering, privacy, reuse and deliverable constraints. Choose neither technical tasks nor implementation structure. Do not turn a helper computation or a generation fragment into a business responsibility. Do not force a fixed number of blocks or collapse unrelated responsibilities into one block. Available contributions are planned behavior, not observed execution results; their review status remains explicit. Depend only on offered results. Missing implementation details belong to implementation; ask a clarification only for a missing business fact. Return exactly one action: block, original-evidence requests, or clarifications.
        """;

    internal static PlanningConstructionBlock? CurrentBlock(PlanningSession state) => State(state).Blocks?
        .SingleOrDefault(block => block.Id == State(state).CurrentBlockId);

    internal static IEnumerable<PlanningConstructionCheckpoint> BusinessBlockCheckpoints(PlanningSession state) =>
        state.Construction!.Checkpoints.Where(checkpoint => checkpoint.Step?.BlockId is { } block && block == State(state).CurrentBlockId &&
            checkpoint.InvalidatedTaskIds.Count == 0);

    internal static bool CandidateCompletesBlock(PlanningSession state) => state.Construction?.Candidate is { } candidate && CandidateCompletesBlock(candidate);

    internal static bool CandidateCompletesBlock(PlanningConstructionCandidate candidate) => candidate.Extension.CompleteBlock == true || candidate.Extension.Complete;

    internal static void ValidateBusinessContract(PlanningSession state)
    {
        if (state.Request.Options[BusinessSelectionContract]?.ToString() != "1" || State(state).Blocks is null)
            throw new PlanningConflictException("This retained session has a different construction selection contract. Preserve its issued work and use its captured source; do not relabel technical steps as business blocks.");
        if (State(state).CurrentBlockId is not null && CurrentBlock(state) is null)
            throw new PlanningConflictException("The active business block is missing from its retained construction metadata.");
        if (State(state).CurrentStep is { Kind: not "revision" } step && step.BlockId != State(state).CurrentBlockId &&
            state.Construction?.Candidate?.Reviews.Any(row => row["purpose"]?.ToString() == "construction_transition") != true)
            throw new PlanningConflictException("The implementation cursor belongs to a different business block.");
    }

    internal static JsonObject BusinessBlockContext(PlanningConstructionBlock block) => new()
    {
        ["objective"] = block.Objective, ["requirementIds"] = List(block.RequirementIds),
        ["informationNeeds"] = List(block.InformationNeeds), ["expectedResults"] = List(block.ExpectedResults),
        ["dependencies"] = new JsonArray(block.Dependencies.Select(dependency => (JsonNode)new JsonObject
            { ["resultId"] = dependency.ResultId, ["reason"] = dependency.Reason }).ToArray()),
        ["successCriteria"] = List(block.SuccessCriteria)
    };

    private static string[] BusinessRequirementIds(PlanningSession state) => state.Construction!.Requirements
        .Where(requirement => requirement.Status != "closed").Select(requirement => requirement.Id).ToArray();

    internal static JsonObject SelectionContext(PlanningSession state)
    {
        var ids = BusinessRequirementIds(state);
        var originals = OriginalRequirementRows(state, ids).OfType<JsonObject>();
        var context = new JsonObject
        {
            ["requirements"] = new JsonArray(originals.Select(row => (JsonNode)new JsonObject
                { ["id"] = row["id"]!.DeepClone(), ["original"] = row["original"]!.DeepClone() }).ToArray()),
            ["globalConstraints"] = new JsonArray(OriginalGlobalRows(state).OfType<JsonObject>().Select(row => (JsonNode)new JsonObject
                { ["id"] = row["id"]!.DeepClone(), ["text"] = row["text"]!.DeepClone() }).ToArray()),
            ["remainingObligations"] = List(state.Construction!.Requirements.Where(requirement => requirement.Status != "closed").Select(requirement => requirement.Id)),
            ["availableResults"] = BusinessResults(state),
            ["evidence"] = new JsonObject((state.Construction.Evidence ?? []).Where(pair => pair.Key.StartsWith("source:", StringComparison.Ordinal) && pair.Value?["text"] is not null)
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, new JsonObject { ["text"] = pair.Value!["text"]!.DeepClone() })))
        };
        var feasibility = BusinessFeasibilityObservations(state);
        if (feasibility.Count > 0) context["feasibilityObservations"] = feasibility;
        if (!string.IsNullOrWhiteSpace(state.Request.Policy.Instructions)) context["instructions"] = state.Request.Policy.Instructions;
        return context;
    }

    private static JsonArray BusinessFeasibilityObservations(PlanningSession state)
    {
        // Retain only recorded lookup status, without interpreting provider prose,
        // model navigation claims or empty search results as business infeasibility.
        var pages = state.Discovery.Pages.AsEnumerable().Reverse()
            .DistinctBy(page => (page.SourceId, page.Cursor, page.Query, page.ProducedArtifactKind)).ToArray();
        var observations = new JsonArray();
        if (pages.Any(page => page.UnavailableReason is not null))
            observations.Add((JsonNode?)JsonValue.Create("Some capability checks were unavailable when attempted. Their results do not establish whether the required capabilities exist."));
        if (pages.Any(page => page.NextCursor is { } cursor && !pages.Any(next =>
                next.SourceId == page.SourceId && next.Cursor == cursor && next.Query == page.Query && next.ProducedArtifactKind == page.ProducedArtifactKind)))
            observations.Add((JsonNode?)JsonValue.Create("Some capability checks have uninspected continuation results. Their results do not establish whether the required capabilities exist."));
        return observations;
    }

    private static JsonArray BusinessResults(PlanningSession state) => new((State(state).Blocks ?? [])
        .Where(block => state.Construction!.Checkpoints.Any(checkpoint => checkpoint.Step?.BlockId == block.Id &&
            checkpoint.InvalidatedTaskIds.Count == 0 && checkpoint.TaskIds.Count > 0))
        .Select(block => (JsonNode)new JsonObject
        {
            ["id"] = "result:" + block.Id, ["objective"] = block.Objective, ["plannedResults"] = List(block.ExpectedResults),
            ["availability"] = "Accepted planned contribution only; planned results are not all established by a partial contribution.",
            ["reviewStatus"] = BusinessBlockReviewCurrent(state, block)
                ? "business_completion_reviewed" : "accepted_contribution_pending_business_completion",
            ["executionObserved"] = false
        }).ToArray());

    private static bool BusinessBlockReviewCurrent(PlanningSession state, PlanningConstructionBlock block) =>
        block.CompletionCheckpoint is { } sequence && state.Construction!.Checkpoints.SingleOrDefault(checkpoint => checkpoint.Sequence == sequence) is { } completion &&
        HybridWorkflowPlanner.ReviewedCheckpoint(state, completion) is not null && state.Construction.Checkpoints
            .Where(checkpoint => checkpoint.Step?.BlockId == block.Id).All(checkpoint => checkpoint.InvalidatedTaskIds.Count == 0);

    internal static JsonObject SelectionSchema(PlanningSession state)
    {
        var requirements = BusinessRequirementIds(state);
        var results = BusinessResults(state).Select(result => result!["id"]!.ToString()).ToArray();
        var dependency = PlanningSchemas.Object(("resultId", results.Length == 0 ? BusinessText(state, 256) : PlanningSchemas.Enum(results)), ("reason", BusinessText(state, 512)));
        var block = PlanningSchemas.Object(("objective", BusinessText(state, 1024)),
            ("requirementIds", PlanningSchemas.Array(PlanningSchemas.Enum(requirements), 1)),
            ("informationNeeds", PlanningSchemas.Array(BusinessText(state, 1024))), ("expectedResults", PlanningSchemas.Array(BusinessText(state, 1024), 1)),
            ("dependencies", results.Length == 0 ? EmptyArray() : PlanningSchemas.Array(dependency)),
            ("successCriteria", PlanningSchemas.Array(BusinessText(state, 1024), 1)));
        var addresses = State(state).Sources.Where(source => source.Indexed).Select(source => "source:" + source.Id).ToArray();
        return PlanningSchemas.Object(("block", requirements.Length == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(block)),
            ("evidenceRequests", addresses.Length == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications(PlanningTokenThresholds.OptimizationAdvisory(state.Request)))));
    }

    internal static void SelectBlock(PlanningSession state, JsonNode proposed)
    {
        var compact = State(state);
        if (compact.CurrentBlockId is not null || compact.CurrentStep is not null || state.Construction!.Candidate is not null)
            throw new PlanningConflictException("Finish or explicitly suspend the retained business block before selecting another responsibility.");
        var block = proposed.Deserialize(PlanningJsonContext.Default.PlanningConstructionBlock)!;
        var available = BusinessResults(state).Select(result => result!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(block.Objective) || block.ExpectedResults.Count == 0 || block.SuccessCriteria.Count == 0 ||
            block.ExpectedResults.Concat(block.SuccessCriteria).Concat(block.InformationNeeds).Any(string.IsNullOrWhiteSpace) ||
            block.RequirementIds.Count == 0 || block.RequirementIds.Distinct(StringComparer.Ordinal).Count() != block.RequirementIds.Count ||
            block.RequirementIds.Except(BusinessRequirementIds(state), StringComparer.Ordinal).Any() ||
            block.Dependencies.Any(dependency => !available.Contains(dependency.ResultId) || string.IsNullOrWhiteSpace(dependency.Reason)))
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_INVALID", "/block", "Select an observable business responsibility tied to original obligations and existing results.");
        block.Id = "block_" + (compact.Blocks!.Count + 1);
        block.CompletionCheckpoint = null;
        compact.Blocks.Add(block); compact.CurrentBlockId = block.Id;
        state.Diagnostics.Clear();
    }

    private static JsonObject BusinessText(PlanningSession state, int maximum) =>
        PlanningTokenThresholds.OptimizationAdvisory(state.Request) ? PlanningSchemas.Type("string") : Text(maximum);

    private static JsonArray List(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
