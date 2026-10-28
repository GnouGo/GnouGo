using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string ClassifyInstructions = """
        Classify this exact page into consecutive verbatim clauses concatenating to the whole page, including whitespace. Preserve order; never summarize, omit, invent or plan tasks. Isolate standalone caller/public declarations using exact contiguous spans; retain their names and constraints. Keep an action with adjacent composition/result constraints as one requirement, even across sentences or when declaring a public result. Interface marking does not require splitting that obligation. Actions, results, setup and cleanup have completion obligations. Global means a crosscutting invariant. Mark explicit caller inputs/public results: none, input, output or both; effect payloads, files and resources do not imply public outputs. No types/defaults here. A continuation's first clause inherits its exact preceding text's kind. Other pages and answers remain authoritative.
        """;

    internal const string InterfaceInstructions = """
        Extract only the minimum sufficient caller/public result interfaces from these exact clauses. Combine clauses about the same interface into one declaration. Preserve stated names, meaning, types, enums, nullability and literal defaults; no aliases or invented restrictions. Existing declarations are immutable. No task, tool-argument, file-content, effect or resource design. Use only permitted directions; return [] for a direction only when these clauses declare no interface in it. Examples impose no restrictions. An absent array bound is null; maxItems:0 permits only an empty array. Public outputs have no defaults.
        """;

    internal static string InterfaceGuidance(PlanningSession state) => """
        Resolve whether each current candidate source declares caller inputs/public results; classifications are provisional. originalContext and existing names supply context, not additional candidates. Return exactly one of interfaces, evidenceRequests or clarifications. Reuse supplied evidence; retrieve missing originals or clarify missing names/types for a declared interface. Use any only for explicitly unconstrained data.
        For interfaces, give each candidate source one disposition with its exact ID/version, displayed evidence citations, declared names and reason. Extract all interfaces declared by these sources, matching typed declarations exactly to their disposition names and merging only the same interface. A current source may explicitly link an unchanged existing interface. Otherwise use no_public_interface, retaining business obligations. Artifact contents, effects and resources alone are not public results; explicitly returned paths/contents are.
        Preserve names, types, constraints, enums, nullability and literal defaults; existing declarations are immutable. Examples add no restrictions. Absent array bounds are null; maxItems:0 permits only []. String enum:null means unrestricted values; a list is the declared exhaustive set. default:null means no default; {"kind":"null"} is an explicit null literal. Outputs have no defaults. Emit compact JSON without whitespace outside strings; preserve string contents and verbatim originals exactly.
        """;

    internal static (int Count, bool Continues) CoherentOriginalPageLength(string text, int start)
    {
        var maximum = OriginalPageLength(text, start);
        if (start + maximum == text.Length) return (maximum, false);
        var boundary = 0;
        for (var i = start; i < start + maximum; i++)
        {
            if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                var end = i + 1;
                while (end < start + maximum && char.IsWhiteSpace(text[end])) end++;
                boundary = end - start;
            }
            else if (text[i] is '\r' or '\n')
            {
                var end = i;
                var breaks = 0;
                while (end < start + maximum && char.IsWhiteSpace(text[end]))
                {
                    if (text[end] == '\r')
                    {
                        breaks++;
                        if (end + 1 < start + maximum && text[end + 1] == '\n') end++;
                    }
                    else if (text[end] == '\n') breaks++;
                    end++;
                }
                // A single newline may be a soft wrap inside one obligation.
                if (breaks > 1) boundary = end - start;
            }
        }
        if (boundary > 0) return (boundary, false);
        // A long indivisible sentence may cross bounded pages. Keep its exact
        // neighboring fragments linked instead of inventing independent obligations.
        for (var i = start + maximum - 1; i > start; i--)
            if (char.IsWhiteSpace(text[i])) return (i + 1 - start, true);
        return (maximum, true);
    }

    internal const string CoalescedInterfaceDescription =
        "Classify source clauses; interface declarations from this page are extracted together.";

    internal static JsonObject ClassificationSchema(PlanningConstructionSource source)
    {
        var schema = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(source.Id)),
            ("clauses", PlanningSchemas.Array(PlanningSchemas.Object(("text", Text(1200)),
                ("kind", PlanningSchemas.Enum("requirement", "global")),
                ("interfaces", PlanningSchemas.Enum("none", "input", "output", "both"))), 1, 16)));
        schema["description"] = CoalescedInterfaceDescription;
        return schema;
    }

    internal static JsonObject InterfaceSchema(PlanningConstructionInterfaceExtraction extraction)
    {
        var schema = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(extraction.Id)),
            ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("input"), 0, 16)),
            ("outputs", PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, 16)));
        schema["$defs"] = InterfaceTypeDefinitions();
        // Bound guidance remains in InterfaceInstructions. Factoring may replace
        // these schemas with references, which cannot carry sibling annotations
        // in the provider's strict structured-output profile.
        var compact = PlanningSchemas.Compact(schema);
        compact["description"] = "For strings, enum:null means unrestricted values. An enum list is the exact exhaustive set declared by the source, never a placeholder or default. default:null means no default; {\"kind\":\"null\"} is an explicit null literal.";
        return compact;
    }

    internal static JsonObject ResolvableInterfaceSchema(PlanningSession state, PlanningConstructionInterfaceExtraction extraction)
    {
        // Classification is provisional in both directions. A file obligation
        // can be declined, and a mistaken direction can be corrected here without
        // another phase. Both declarations still use the existing shared types.
        var shared = InterfaceSchema(extraction);
        var originals = InterfaceEvidenceSources(state, extraction).ToList();
        var disposition = PlanningSchemas.Object(
            ("sourceId", PlanningSchemas.Enum(extraction.SourceIds.ToArray())),
            ("version", PlanningSchemas.Enum(extraction.SourceIds.Select(id => State(state).Sources.Single(s => s.Id == id).Version).Distinct(StringComparer.Ordinal).ToArray())),
            ("kind", PlanningSchemas.Enum("interfaces", "no_public_interface")),
            ("inputs", Strings(0, 16)), ("outputs", Strings(0, 16)), ("reason", Text(600)),
            ("evidence", PlanningSchemas.Array(PlanningSchemas.Object(
                ("sourceId", PlanningSchemas.Enum(originals.Select(s => s.Id).ToArray())),
                ("version", PlanningSchemas.Enum(originals.Select(s => s.Version).Distinct(StringComparer.Ordinal).ToArray()))), 1, 8)));
        var declarations = PlanningSchemas.Object(("inputs", shared["properties"]!["inputs"]!.DeepClone().AsObject()),
            ("outputs", shared["properties"]!["outputs"]!.DeepClone().AsObject()),
            ("dispositions", PlanningSchemas.Array(disposition, extraction.SourceIds.Count, extraction.SourceIds.Count)));
        var addresses = InterfaceEvidenceDirectory(state, extraction).Where(s => originals.All(o => o.Id != s.Id))
            .Select(s => "source:" + s.Id).ToArray();
        var schema = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(extraction.Id)),
            ("interfaces", PlanningSchemas.Nullable(declarations)),
            ("evidenceRequests", addresses.Length == 0 ? PlanningSchemas.Type("null") :
                PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, 8))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        schema["$defs"] = shared["$defs"]?.DeepClone();
        return PlanningSchemas.Compact(schema);
    }

    private static IEnumerable<PlanningConstructionSource> InterfaceOriginalPages(PlanningSession state,
        PlanningConstructionInterfaceExtraction extraction)
    {
        var sources = State(state).Sources;
        var pages = sources.Where(s => s.Kind is "page" or "answer_page" or "revision_page").ToList();
        var containing = pages.Where(s => extraction.SourceIds.Any(id => id.StartsWith(s.Id + ":clause:", StringComparison.Ordinal))).ToList();
        // Retain the opening declaration and bounded containing pages. Other
        // originals are exact navigable evidence, not a workflow-wide summary.
        return pages.Take(1).Concat(containing.Take(1))
            .Concat(pages.Where(s => s.Kind == "answer_page").TakeLast(1)).Concat(containing.Skip(1))
            .Concat(containing.SelectMany(page =>
            {
                var index = pages.IndexOf(page);
                return pages.Skip(Math.Max(0, index - 1)).Take(index == 0 ? 2 : 3);
            }))
            .DistinctBy(s => s.Id, StringComparer.Ordinal).Take(3);
    }

    internal static IEnumerable<PlanningConstructionSource> InterfaceEvidenceSources(PlanningSession state,
        PlanningConstructionInterfaceExtraction extraction) => State(state).Sources.Where(s =>
            extraction.SourceIds.Contains(s.Id, StringComparer.Ordinal) ||
            InterfaceOriginalPages(state, extraction).Any(p => p.Id == s.Id) ||
            extraction.EvidenceSourceIds?.Contains(s.Id, StringComparer.Ordinal) == true &&
            state.Construction!.Evidence?["source:" + s.Id] is JsonObject evidence &&
                evidence["id"]?.ToString() == s.Id && evidence["version"]?.ToString() == s.Version);

    internal static IEnumerable<PlanningConstructionSource> InterfaceEvidenceDirectory(PlanningSession state,
        PlanningConstructionInterfaceExtraction extraction)
    {
        var pages = State(state).Sources.Where(s => s.Kind is "page" or "answer_page" or "revision_page").ToList();
        var retained = extraction.EvidenceSourceIds?.AsEnumerable().Reverse() ?? [];
        var anchors = retained.Concat(InterfaceOriginalPages(state, extraction).Select(s => s.Id)).Distinct(StringComparer.Ordinal);
        return anchors.SelectMany(id =>
        {
            var index = pages.FindIndex(s => s.Id == id);
            return index < 0 ? [] : pages.Skip(Math.Max(0, index - 1)).Take(index == 0 ? 2 : 3);
        }).DistinctBy(s => s.Id, StringComparer.Ordinal).Take(8);
    }

    private static JsonObject ResolvableInterfaceContext(PlanningSession state, PlanningConstructionInterfaceExtraction extraction)
    {
        var compact = State(state);
        JsonObject Original(PlanningConstructionSource source) => new()
        { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text, ["continuationOf"] = source.ContinuationOf };
        var available = InterfaceEvidenceSources(state, extraction).ToList();
        var originalText = string.Concat(available.Select(s => s.Text));
        return new JsonObject
        {
            ["sources"] = new JsonArray(extraction.SourceIds.Select(id => (JsonNode)Original(compact.Sources.Single(s => s.Id == id))).ToArray()),
            ["originalContext"] = new JsonArray(InterfaceOriginalPages(state, extraction).Select(s => (JsonNode)Original(s)).ToArray()),
            ["originalDirectory"] = new JsonArray(InterfaceEvidenceDirectory(state, extraction).Select(s => (JsonNode)new JsonObject
            { ["id"] = s.Id, ["version"] = s.Version, ["evidence"] = "source:" + s.Id }).ToArray()),
            ["evidence"] = new JsonObject(available.Where(s => state.Construction!.Evidence?["source:" + s.Id] is not null)
                .Select(s => new KeyValuePair<string, JsonNode?>("source:" + s.Id, Original(s)))),
            ["existingInputs"] = new JsonArray((state.Requirements?.Inputs ?? []).Where(i => originalText.Contains(i.Name, StringComparison.Ordinal))
                .Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
            ["existingOutputs"] = new JsonArray((state.Requirements?.Outputs ?? []).Where(i => originalText.Contains(i.Name, StringComparison.Ordinal))
                .Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics)
        };
    }

    private static bool DispositionsComplete(PlanningCompactConstructionState compact, PlanningConstructionInterfaceExtraction extraction) =>
        extraction.Dispositions is { } dispositions && dispositions.Count == extraction.SourceIds.Count &&
        extraction.SourceIds.All(id => dispositions.Count(d => d.SourceId == id) == 1) && dispositions.All(d =>
            compact.Sources.Any(s => s.Id == d.SourceId && s.Version == d.Version && s.Version == PlanningGraphCompiler.Fingerprint(s.Text)) &&
            !string.IsNullOrWhiteSpace(d.Reason) &&
            (d.Kind == "interfaces" && d.Inputs.Count + d.Outputs.Count > 0 ||
                d.Kind == "no_public_interface" && d.Inputs.Count + d.Outputs.Count == 0) && d.Evidence.Count > 0 &&
            d.Evidence.All(e => compact.Sources.Any(s => s.Id == e.Key && s.Version == e.Value && s.Version == PlanningGraphCompiler.Fingerprint(s.Text))));

    internal static JsonObject InterfaceContext(PlanningSession state, PlanningConstructionInterfaceExtraction extraction)
    {
        return ResolvableInterfaceContext(state, extraction);
    }

    internal static bool IntentDeclarationsComplete(PlanningCompactConstructionState compact) =>

        compact.InterfaceExtractions is not null && compact.InterfaceExtractions.All(e => e.Complete && e.SourceIds.Count > 0 &&
            e.SourceIds.All(id => compact.Sources.Count(s => s.Id == id && s.Indexed) == 1) &&
            e.Inputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "input" or "both") &&
            e.Outputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "output" or "both") &&
            (DispositionsComplete(compact, e))) &&
        compact.Sources.Where(s => s.Kind is "requirement" or "global").All(s =>
            s.InterfaceKind == "none" || s.InterfaceKind is "input" or "output" or "both" &&
            compact.InterfaceExtractions.Count(e => e.SourceIds.Contains(s.Id, StringComparer.Ordinal)) == 1);

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
        // Interface-bearing continuations are extracted together after all pages
        // have been classified; no partial declaration is frozen at the page edge.
        var extractions = compact.InterfaceExtractions!;
        var linked = extractions.Where(e => e.SourceIds.Contains(previous.Id, StringComparer.Ordinal) ||
            e.SourceIds.Contains(current.Id, StringComparer.Ordinal)).ToList();
        if (linked.Count == 0) return;
        var target = linked[0];
        target.SourceIds = compact.Sources.Where(s => combined.Contains(s.Id, StringComparer.Ordinal) ||
            linked.Any(e => e.SourceIds.Contains(s.Id, StringComparer.Ordinal))).Select(s => s.Id).ToList();
        target.Inputs = linked.Any(e => e.Inputs);
        target.Outputs = linked.Any(e => e.Outputs);
        foreach (var obsolete in linked.Skip(1)) extractions.Remove(obsolete);
    }
}
