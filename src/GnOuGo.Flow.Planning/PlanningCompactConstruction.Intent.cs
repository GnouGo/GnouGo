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
            version == 2 && captured is JsonValue value && value.TryGetValue<int>(out var number) && number == 2;
    }

    internal const string ClassifyInstructions = """
        Classify this exact page into consecutive verbatim clauses concatenating to the whole page, including whitespace. Preserve order; never summarize, omit, invent or plan tasks. Actions, results, setup and cleanup are requirements with completion obligations. One requirement may span sentences: keep an action with adjacent constraints on its composition and result. Global means a crosscutting invariant. Mark explicit caller inputs/public results: none, input, output or both; effect payloads, files and resources do not imply public outputs. No types/defaults here. A continuation's first clause inherits its exact preceding text's kind. Other pages and answers remain authoritative.
        """;

    internal const string InterfaceInstructions = """
        Extract only the minimum sufficient caller/public result interfaces from these exact clauses. Combine clauses about the same interface into one declaration. Preserve stated names, meaning, types, enums, nullability and literal defaults; no aliases or invented restrictions. Existing declarations are immutable. No task, tool-argument, file-content, effect or resource design. Use only permitted directions; return [] for a direction only when these clauses declare no interface in it. Examples impose no restrictions. An absent array bound is null; maxItems:0 permits only an empty array. Public outputs have no defaults.
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
        var schema = IntentSchema(new() { Id = extraction.Id });
        schema["properties"]!.AsObject().Remove("clauses");
        schema["required"] = new JsonArray("sourceId", "inputs", "outputs");
        if (!extraction.Inputs) schema["properties"]!["inputs"] = PlanningSchemas.Array(Text(1), 0, 0);
        if (!extraction.Outputs) schema["properties"]!["outputs"] = PlanningSchemas.Array(Text(1), 0, 0);
        if (!extraction.Inputs)
            schema["$defs"]!["type"]!["anyOf"]![3]!["properties"]!["fields"]!["items"] = PlanningSchemas.Ref("output");
        // Bound guidance remains in InterfaceInstructions. Factoring may replace
        // these schemas with references, which cannot carry sibling annotations
        // in the provider's strict structured-output profile.
        return PlanningSchemas.Compact(schema);
    }

    internal static JsonObject InterfaceContext(PlanningSession state, PlanningConstructionInterfaceExtraction extraction)
    {
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
        compact.PresentationVersion is null || compact.PresentationVersion == 2 &&
        compact.InterfaceExtractions is not null && compact.InterfaceExtractions.All(e => e.Complete && e.SourceIds.Count > 0 &&
            e.SourceIds.All(id => compact.Sources.Count(s => s.Id == id && s.Indexed) == 1) &&
            e.Inputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "input" or "both") &&
            e.Outputs == e.SourceIds.Any(id => compact.Sources.Single(s => s.Id == id).InterfaceKind is "output" or "both")) &&
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
