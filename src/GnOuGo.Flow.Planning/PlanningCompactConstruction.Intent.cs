using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static bool CapturedPresentationValid(PlanningSession state)
    {
        var version = state.Construction?.Compact?.PresentationVersion;
        var captured = state.Request.Options["construction_presentation_version"];
        return version is null && captured is null ||
            version is 2 or 3 && captured is JsonValue value && value.TryGetValue<int>(out var number) && number == version;
    }

    internal const string ClassifyInstructions = """
        Classify this exact page into consecutive verbatim clauses concatenating to the whole page, including whitespace. Preserve order; never summarize, omit, invent or plan tasks. Isolate standalone caller/public declarations using exact contiguous spans; retain their names and constraints. Keep an action with adjacent composition/result constraints as one requirement, even across sentences or when declaring a public result. Interface marking does not require splitting that obligation. Actions, results, setup and cleanup have completion obligations. Global means a crosscutting invariant. Mark explicit caller inputs/public results: none, input, output or both; effect payloads, files and resources do not imply public outputs. No types/defaults here. A continuation's first clause inherits its exact preceding text's kind. Other pages and answers remain authoritative.
        """;

    internal const string InterfaceInstructions = """
        Extract only the minimum sufficient caller/public result interfaces from these exact clauses. Combine clauses about the same interface into one declaration. Preserve stated names, meaning, types, enums, nullability and literal defaults; no aliases or invented restrictions. Existing declarations are immutable. No task, tool-argument, file-content, effect or resource design. Use only permitted directions; return [] for a direction only when these clauses declare no interface in it. Examples impose no restrictions. An absent array bound is null; maxItems:0 permits only an empty array. Public outputs have no defaults.
        """;

    internal static string InterfaceGuidance(PlanningSession state) => State(state).PresentationVersion == 3 ? """
        Resolve each candidate source's provisional interface classification using its exact original context. Return one action: interfaces, original evidence requests, or clarification. Extract every explicit caller input/public result in the candidate sources; combine only declarations of the same interface. Preserve names, types, constraints and literal defaults. Existing declarations are immutable. Files, effect payloads and resources remain business obligations; use no_public_interface only when their sources declare no caller input or public result. Every disposition must cite exact displayed original source identities/versions and explain its declaration or non-interface decision. Missing type or naming context requires original evidence or clarification, never a fallback to any; any remains valid only for explicitly unconstrained data. Use existing evidence instead of rereading it. Examples impose no restrictions; an absent array bound is null, and maxItems:0 permits only an empty array. For strings, enum:null means unrestricted values; a list is the exact declared exhaustive set. default:null means no default; {"kind":"null"} is an explicit null literal. Public outputs have no defaults.
        """ : PlanningConstructionPhasePolicies.IsFresh(state.Request) ? """
        Extract every explicitly declared caller input and public result from these exact clauses. Combine clauses only when they describe the same interface; preserve distinct named declarations. Preserve stated meaning, types, exhaustive enums, nullability and literal defaults; no aliases or invented restrictions. Existing declarations are immutable. No task, tool-argument, file-content, effect or resource design. Use only permitted directions; return [] only when no interface is declared in that direction. Examples impose no restrictions. An absent array bound is null; maxItems:0 permits only an empty array. Public outputs have no defaults.
        """ : InterfaceInstructions;

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

    internal static JsonObject InterfaceSchema(PlanningConstructionInterfaceExtraction extraction, bool requireDeclared = false, bool provisional = false)
    {
        var schema = IntentSchema(new() { Id = extraction.Id });
        schema["properties"]!.AsObject().Remove("clauses");
        schema["required"] = new JsonArray("sourceId", "inputs", "outputs");
        if (!extraction.Inputs) schema["properties"]!["inputs"] = PlanningSchemas.Array(Text(1), 0, 0);
        else if (requireDeclared && !provisional) schema["properties"]!["inputs"]!["minItems"] = 1;
        if (!extraction.Outputs) schema["properties"]!["outputs"] = PlanningSchemas.Array(Text(1), 0, 0);
        else if (requireDeclared && !provisional) schema["properties"]!["outputs"]!["minItems"] = 1;
        if (!extraction.Inputs)
            schema["$defs"]!["type"]!["anyOf"]![3]!["properties"]!["fields"]!["items"] = PlanningSchemas.Ref("output");
        else if (requireDeclared)
        {
            // The existing DTO distinguishes an absent default from a literal
            // null. Keep one input shape instead of duplicating the declaration.
            var input = schema["$defs"]!["output"]!.DeepClone().AsObject();
            input["properties"]!["default"] = PlanningSchemas.Nullable(PlanningSchemas.Ref("literal"));
            input["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("default"));
            schema["$defs"]!["input"] = input;
        }
        if (requireDeclared)
        {
            var types = schema["$defs"]!["type"]!["anyOf"]!.AsArray();
            types[0]!["properties"]!["kind"] = PlanningSchemas.Enum("number", "integer", "boolean", "any");
            types[1]!["properties"]!["enum"] = PlanningSchemas.Nullable(types[1]!["properties"]!["enum"]!.DeepClone().AsObject());
        }
        // Bound guidance remains in InterfaceInstructions. Factoring may replace
        // these schemas with references, which cannot carry sibling annotations
        // in the provider's strict structured-output profile.
        var compact = PlanningSchemas.Compact(schema);
        if (requireDeclared)
            compact["description"] = "For strings, enum:null means unrestricted values. An enum list is the exact exhaustive set declared by the source, never a placeholder or default." +
                (extraction.Inputs ? " default:null means no default; {\"kind\":\"null\"} is an explicit null literal." : "");
        return compact;
    }

    internal static JsonObject ResolvableInterfaceSchema(PlanningSession state, PlanningConstructionInterfaceExtraction extraction)
    {
        // Classification is provisional in both directions. A file obligation
        // can be declined, and a mistaken direction can be corrected here without
        // another phase. Both declarations still use the existing shared types.
        var shared = InterfaceSchema(new() { Id = extraction.Id, Inputs = true, Outputs = true },
            requireDeclared: true, provisional: true);
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
        if (State(state).PresentationVersion == 3) return ResolvableInterfaceContext(state, extraction);
        var sources = State(state).Sources;
        var text = string.Concat(extraction.SourceIds.Select(id => sources.Single(s => s.Id == id).Text));
        return new JsonObject
        {
            ["sources"] = new JsonArray(extraction.SourceIds.Select(id =>
            {
                var source = sources.Single(s => s.Id == id);
                return (JsonNode)new JsonObject { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text };
            }).ToArray()),
            ["existingInputs"] = new JsonArray((state.Requirements?.Inputs ?? []).Where(i => Mentions(i.Name))
                .Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
            ["existingOutputs"] = new JsonArray((state.Requirements?.Outputs ?? []).Where(i => Mentions(i.Name))
                .Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics)
        };

        bool Mentions(string name)
        {
            for (var start = 0; name.Length > 0 && start <= text.Length - name.Length;)
            {
                var position = text.IndexOf(name, start, StringComparison.Ordinal);
                if (position < 0) return false;
                var end = position + name.Length;
                if ((position == 0 || !Identifier(text[position - 1])) && (end == text.Length || !Identifier(text[end]))) return true;
                start = position + 1;
            }
            return false;
        }
        static bool Identifier(char value) => char.IsLetterOrDigit(value) || value == '_';
    }

    internal static bool IntentDeclarationsComplete(PlanningCompactConstructionState compact) =>
        compact.PresentationVersion is null || compact.PresentationVersion is 2 or 3 &&
        compact.InterfaceExtractions is not null && compact.InterfaceExtractions.All(e => e.Complete && e.SourceIds.Count > 0 &&
            e.SourceIds.All(id => compact.Sources.Count(s => s.Id == id && s.Indexed) == 1) &&
            e.Inputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "input" or "both") &&
            e.Outputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "output" or "both") &&
            (compact.PresentationVersion != 3 || DispositionsComplete(compact, e))) &&
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
