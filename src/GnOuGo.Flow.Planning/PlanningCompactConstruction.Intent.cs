using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string IntentInstructions = """
        Understand the exact original source in one pass. Return consecutive verbatim clauses whose text concatenates to the entire source, including whitespace, without omissions or duplication. Keep related actions and restrictions together; clauses are acceptance criteria, not tasks or scopes. Classify each as requirement or global (a crosscutting invariant).
        Each clause declares its typed caller inputs and public returns, or empty inputs/outputs when none. Files, artifact contents, internal resources and effects alone are not public returns. Preserve names, types, requiredness, nullability, enums and literal defaults; do not invent types or defaults. Use any only for explicitly unconstrained data. Combine evidence about the same declaration. Existing interfaces are immutable. Absent array bounds are null; maxItems:0 permits only []. String enum:null is unrestricted. default:null is absent; {"kind":"null"} is an explicit null default. Public outputs have no defaults.
        Use originalContext and retrieved sources for linked naming/type constraints before clarification. A continuation's first clause keeps the preceding kind. Return exactly one action: clauses, evidenceRequests for missing displayed original addresses, or clarifications for genuinely missing business facts. Emit compact JSON, preserving original strings exactly. No tools, tasks or workflow outline.
        """;

    internal static JsonObject IntentSchema(PlanningConstructionSource source, IEnumerable<string>? addresses = null)
    {
        var evidence = (addresses ?? []).ToArray();
        var schema = PlanningSchemas.Object(
            ("clauses", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(
                ("text", Text(source.Text.Length)), ("kind", PlanningSchemas.Enum("requirement", "global")),
                ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("input"), 0, 16)),
                ("outputs", PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, 16))), 1, 128))),
            ("evidenceRequests", evidence.Length == 0 ? PlanningSchemas.Type("null") :
                PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(evidence), 1, 8))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        schema["$defs"] = InterfaceTypeDefinitions();
        return PlanningSchemas.Compact(schema);
    }

    internal static LLMRequest IntentRequest(PlanningSession state, PlanningConstructionSource source, IEnumerable<PlanningConstructionSource>? prospective = null)
    {
        var compact = State(state);
        var pages = compact.Sources.Where(s => s.Kind is "page" or "answer_page" or "revision_page").Concat(prospective ?? []).DistinctBy(s => s.Id).ToList();
        var preceding = source.ContinuationOf is null ? null : compact.Sources.LastOrDefault(s =>
            s.Id.StartsWith(source.ContinuationOf + ":clause:", StringComparison.Ordinal));
        JsonObject Original(PlanningConstructionSource item) => new()
        { ["id"] = item.Id, ["version"] = item.Version, ["text"] = item.Text, ["continuationOf"] = item.ContinuationOf };
        var available = pages.Where(s => s.Id != source.Id && state.Construction!.Evidence?["source:" + s.Id] is not null).ToArray();
        var missing = pages.Where(s => s.Id != source.Id && !available.Contains(s)).Select(s => "source:" + s.Id).ToArray();
        var addresses = missing.Take(9).ToArray();
        var context = new JsonObject
        {
            ["sources"] = new JsonArray(Original(source)),
            ["continuation"] = source.ContinuationOf is null ? null : new JsonObject { ["kind"] = preceding?.Kind ?? "requirement" },
            ["originalContext"] = new JsonArray(available.Select(item => (JsonNode)Original(item)).ToArray()),
            ["originalDirectory"] = new JsonArray(addresses.Take(8).Select(address => (JsonNode)new JsonObject { ["evidence"] = address }).ToArray()),
            ["nextOriginal"] = addresses.ElementAtOrDefault(8),
            ["existingInputs"] = System.Text.Json.JsonSerializer.SerializeToNode(state.Requirements?.Inputs ?? state.Plan?.Inputs ?? [], PlanningJsonContext.Default.ListTaskInput),
            ["existingOutputs"] = System.Text.Json.JsonSerializer.SerializeToNode(state.Requirements?.Outputs ?? [], PlanningJsonContext.Default.ListTaskInput),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics)
        };
        return Request(IntentInstructions, context, IntentSchema(source, addresses), IntentPurpose);
    }

    private static (int Count, bool Continues) CoherentSourceLength(string text, int maximum)
    {
        if (maximum == text.Length) return (maximum, false);
        var boundary = 0;
        for (var index = 0; index < maximum; index++)
            if (text[index] is '.' or '!' or '?' && index + 1 < text.Length && char.IsWhiteSpace(text[index + 1]) ||
                text[index] == '\n' && index + 1 < maximum && text[index + 1] == '\n')
            {
                var end = index + 1;
                while (end < maximum && char.IsWhiteSpace(text[end])) end++;
                boundary = end;
            }
        if (boundary > 0) return (boundary, false);
        for (var index = maximum - 1; index > 0; index--)
            if (char.IsWhiteSpace(text[index])) return (index + 1, true);
        return (maximum, true);
    }

    internal static bool IntentDeclarationsComplete(PlanningCompactConstructionState compact) =>
        compact.Sources.Where(s => s.Kind is "requirement" or "global").All(s => s.Indexed && s.Inputs is not null && s.Outputs is not null &&
            s.Version == PlanningGraphCompiler.Fingerprint(s.Text));

    internal static void MergeContinuation(PlanningSession state, PlanningConstructionSource page)
    {
        if (page.ContinuationOf is null) return;
        var compact = State(state);
        var previous = compact.Sources.Last(s => s.Id.StartsWith(page.ContinuationOf + ":clause:", StringComparison.Ordinal));
        var current = compact.Sources.First(s => s.Id.StartsWith(page.Id + ":clause:", StringComparison.Ordinal));
        current.ContinuationOf = previous.Id;
        var previousRequirement = compact.RequirementSources.SingleOrDefault(p => p.Value.Contains(previous.Id, StringComparer.Ordinal));
        var currentRequirement = compact.RequirementSources.SingleOrDefault(p => p.Value.Contains(current.Id, StringComparer.Ordinal));
        var preceding = new List<string> { previous.Id };
        for (var ancestor = previous; ancestor.ContinuationOf is not null;)
        {
            ancestor = compact.Sources.Single(s => s.Id == ancestor.ContinuationOf);
            preceding.Insert(0, ancestor.Id);
        }
        var combined = (previousRequirement.Value ?? preceding).Concat(currentRequirement.Value ?? [current.Id]).ToList();
        if (previous.Kind == "requirement" || current.Kind == "requirement")
        {
            var id = previousRequirement.Key ?? currentRequirement.Key!;
            if (previousRequirement.Key is not null && currentRequirement.Key is not null && previousRequirement.Key != currentRequirement.Key)
            {
                compact.RequirementSources.Remove(currentRequirement.Key);
                state.Requirements!.Outcomes.RemoveAll(r => r.Id == currentRequirement.Key);
                state.Construction!.Requirements.RemoveAll(r => r.Id == currentRequirement.Key);
            }
            foreach (var sourceId in combined)
            {
                compact.Sources.Single(s => s.Id == sourceId).Kind = "requirement";
                compact.GlobalSourceIds.Remove(sourceId);
            }
            compact.RequirementSources[id] = combined;
            var index = state.Requirements!.Outcomes.FindIndex(r => r.Id == id);
            state.Requirements.Outcomes[index] = state.Requirements.Outcomes[index] with
            { Description = string.Concat(combined.Select(sourceId => compact.Sources.Single(s => s.Id == sourceId).Text)) };
        }
    }
}
