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
            candidate.BusinessReviewed = false;
            await ValidateConstructionCandidateAsync(state, runtime, ct);
        }
    }

    private static async Task ExtractConstructionInterfacesAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionInterfaceExtraction extraction, CancellationToken ct)
    {
        { await ResolveConstructionInterfacesAsync(state, runtime, extraction, ct); return; }
    }

    private static LLMRequest ResolvableInterfaceRequest(PlanningSession state, string extractionId)
    {
        var extraction = PlanningCompactConstruction.State(state).InterfaceExtractions!.Single(e => e.Id == extractionId);
        return PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceGuidance(state),
            PlanningCompactConstruction.InterfaceContext(state, extraction),
            PlanningCompactConstruction.ResolvableInterfaceSchema(state, extraction), PlanningCompactConstruction.IntentPurpose);
    }

    private static async Task ResolveConstructionInterfacesAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionInterfaceExtraction extraction, CancellationToken ct)
    {
        using var correction = new PlanningCorrectionAttempt(state, state.Diagnostics.Any(d => d.Required));
        var issued = state.PendingCall?.Request;
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.IntentPurpose,
            () => issued = ResolvableInterfaceRequest(state, extraction.Id), ct, correction, request => issued = request);
        if (response["sourceId"]?.ToString() != extraction.Id)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/sourceId", "Use the exact issued interface declaration source.");
        if (new[] { "interfaces", "evidenceRequests", "clarifications" }.Count(name => response[name] is not null) != 1)
            PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return exactly one interface resolution, original evidence request or clarification.");
        var context = JsonNode.Parse(issued!.Prompt[(issued.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        if (response["evidenceRequests"] is JsonArray evidence)
        {
            var available = IssuedInterfaceSources(context);
            var addresses = context["originalDirectory"]!.AsArray().Select(s => s!["evidence"]!.ToString()).ToHashSet(StringComparer.Ordinal);
            var requests = evidence.Select(e => e!.ToString()).ToList();
            if (requests.Any(address => !addresses.Contains(address) || available.ContainsKey(address[7..])))
                PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Retrieve an issued original source address that is not already supplied.");
            PlanningConstruction.LoadEvidence(state, requests, followup: next =>
            {
                var pending = PlanningCompactConstruction.State(next).InterfaceExtractions!.Single(e => e.Id == extraction.Id);
                pending.EvidenceSourceIds = (pending.EvidenceSourceIds ?? []).Concat(requests.Select(address => address[7..]))
                    .Distinct(StringComparer.Ordinal).ToList();
                var request = ResolvableInterfaceRequest(next, extraction.Id);
                return PlanningModelCalls.PrepareRequest(next, PlanningCompactConstruction.IntentPurpose,
                    request.Prompt, request.StructuredOutputSchema!.AsObject(), next.Construction!.Limits.DraftOutputTokens);
            }, purpose: PlanningCompactConstruction.IntentPurpose);
            state.Diagnostics.RemoveAll(d => d.Location == "/evidenceRequests" &&
                d.Code is "CONSTRUCTION_EVIDENCE_UNKNOWN" or "CONSTRUCTION_EVIDENCE_INVALID" or "CONSTRUCTION_EVIDENCE_LIMIT");
            correction.EvidenceAccepted();
            return;
        }
        if (response["clarifications"] is JsonArray questions)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            return;
        }
        var declarations = response["interfaces"]!;
        var dispositions = ReadInterfaceDispositions(state, extraction, declarations, context);
        var compact = PlanningCompactConstruction.State(state);
        var source = compact.Sources.Single(s => s.Id == extraction.SourceIds[0]);
        var page = compact.Sources.Single(s => source.Id.StartsWith(s.Id + ":clause:", StringComparison.Ordinal));
        // No declaration or disposition becomes authoritative before every source
        // reference and the complete existing semantic interface preflight pass.
        var requirements = ReadConstructionInterfaces(state, declarations, page.Kind);
        state.Requirements = requirements;
        extraction.Dispositions = dispositions;
        extraction.Complete = true;
        state.Diagnostics.Clear();
        if (state.Construction!.Candidate is { } candidate && PlanningCompactConstruction.IntentDeclarationsComplete(compact))
        {
            candidate.BusinessReviewed = false;
            await ValidateConstructionCandidateAsync(state, runtime, ct);
        }
    }

    private static Dictionary<string, string> IssuedInterfaceSources(JsonObject context)
    {
        var sources = context["sources"]!.AsArray().Concat(context["originalContext"]!.AsArray())
            .Concat(context["evidence"]!.AsObject().Select(e => e.Value));
        return sources.GroupBy(s => s!["id"]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!["version"]!.ToString(), StringComparer.Ordinal);
    }

    private static List<PlanningConstructionInterfaceDisposition> ReadInterfaceDispositions(PlanningSession state,
        PlanningConstructionInterfaceExtraction extraction, JsonNode declarations, JsonObject context)
    {
        var issued = IssuedInterfaceSources(context);
        var sources = PlanningCompactConstruction.State(state).Sources;
        var dispositions = new List<PlanningConstructionInterfaceDisposition>();
        foreach (var item in declarations["dispositions"]!.AsArray())
        {
            var decision = new PlanningConstructionInterfaceDisposition
            {
                SourceId = item!["sourceId"]!.ToString(), Version = item["version"]!.ToString(), Kind = item["kind"]!.ToString(),
                Inputs = item["inputs"]!.AsArray().Select(n => n!.ToString()).ToList(),
                Outputs = item["outputs"]!.AsArray().Select(n => n!.ToString()).ToList(), Reason = item["reason"]!.ToString()
            };
            if (!extraction.SourceIds.Contains(decision.SourceId, StringComparer.Ordinal) ||
                !ValidOriginal(decision.SourceId, decision.Version) || string.IsNullOrWhiteSpace(decision.Reason))
                PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/dispositions", "Resolve the exact issued candidate source and version with an evidence-based reason.");
            foreach (var reference in item["evidence"]!.AsArray())
            {
                var id = reference!["sourceId"]!.ToString(); var version = reference["version"]!.ToString();
                if (!ValidOriginal(id, version) || !decision.Evidence.TryAdd(id, version))
                    PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/dispositions/evidence", "Cite distinct exact original sources supplied in this issued request; retrieve missing context first.");
            }
            if (decision.Evidence.Count == 0 || decision.Kind == "interfaces" && decision.Inputs.Count + decision.Outputs.Count == 0 ||
                decision.Kind == "no_public_interface" && decision.Inputs.Count + decision.Outputs.Count != 0 ||
                decision.Kind is not ("interfaces" or "no_public_interface"))
                PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/dispositions", "Resolve each source explicitly to its named interfaces or to no public interface, preserving its business obligation.");
            dispositions.Add(decision);
        }
        if (dispositions.Count != extraction.SourceIds.Count || extraction.SourceIds.Any(id => dispositions.Count(d => d.SourceId == id) != 1))
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/dispositions", "Resolve every candidate source exactly once before accepting interfaces.");
        CheckNames("inputs", dispositions.SelectMany(d => d.Inputs));
        CheckNames("outputs", dispositions.SelectMany(d => d.Outputs));
        return dispositions;

        bool ValidOriginal(string id, string version) => issued.TryGetValue(id, out var expected) && expected == version &&
            sources.Any(s => s.Id == id && s.Version == version && s.Version == PlanningGraphCompiler.Fingerprint(s.Text));
        void CheckNames(string direction, IEnumerable<string> referenced)
        {
            var names = declarations[direction]!.AsArray().Select(i => i!["name"]!.ToString()).ToList();
            var typed = names.ToHashSet(StringComparer.Ordinal);
            var required = referenced.ToHashSet(StringComparer.Ordinal);
            if (names.Count == typed.Count && typed.SetEquals(required)) return;
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/" + direction,
                    "Typed " + direction + " must match current-source dispositions." +
                    Describe("duplicate typed names", names.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key)) +
                    Describe("missing typed declarations", required.Except(typed, StringComparer.Ordinal)) +
                    Describe("typed names without a current-source disposition", typed.Except(required, StringComparer.Ordinal)));
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/interfaces/" + direction, "Each typed interface must be named by an exact source disposition.");
        }
        static string Describe(string label, IEnumerable<string> values)
        {
            var names = values.Order(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return "";
            var shown = new JsonArray();
            foreach (var name in names.Take(4))
            {
                shown.Add((JsonNode?)JsonValue.Create(name));
                if (System.Text.Encoding.UTF8.GetByteCount(shown.ToJsonString()) <= 384) continue;
                shown.RemoveAt(shown.Count - 1); break;
            }
            return " " + label + " (" + names.Length + "): " + shown.ToJsonString() + "; omitted=" + (names.Length - shown.Count) + ".";
        }
    }
}
