using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Bounded, non-recursive model transport for a synthesized schema tree.</summary>
internal sealed class PlanningFlatSchemas
{
    internal const string Instructions = "At each schema slot return fields, a flat list of typed declarations. " +
        "Declare the root at path \"\". Declare object members at /properties/NAME, array items at /items, and typed map values at /additionalProperties; compose these paths for nesting. " +
        "Escape names with JSON Pointer ~0 and ~1. Every parent must be declared. A reference is an indivisible authoritative schema. " +
        "Each declaration contains path, required, and a shallow schema; do not repeat child schemas inside their parents. required applies to object members. " +
        "The host assembles the tree and validates it against the original contracts. Preserve all existing valid declarations during repair.\n";

    private readonly Dictionary<string, bool> _slots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _additions = new(StringComparer.Ordinal);
    internal JsonObject Schema { get; }
    internal string Guidance => Instructions + (_additions.Count == 0 ? "" :
        "For this additive repair, each root response key is an exact schema coordinate. Declare an object root and only the new properties beneath it, at most four. Existing properties and map contracts are retained by the host; do not repeat them.\n");

    internal PlanningFlatSchemas(JsonObject original)
    {
        Schema = original.DeepClone().AsObject();
        // Additive repairs use the exact coordinate as a root response key. Repeating
        // changes/addProperties/port/schema wrappers around flat rows exceeds strict
        // transport depth limits and adds no executable information.
        if (Schema["properties"]?["changes"]?["properties"] is JsonObject changes && changes.Count > 0 &&
            changes.All(p => p.Value?["properties"]?["addProperties"]?["items"]?["$ref"]?.ToString() is "#/$defs/port" or "#/$defs/strictPort"))
        {
            var slots = new JsonObject();
            foreach (var (coordinate, value) in changes)
            {
                _additions.Add(coordinate);
                slots[coordinate] = new JsonObject { ["$ref"] = value!["properties"]!["addProperties"]!["items"]!["$ref"]!.ToString() == "#/$defs/strictPort" ? "#/$defs/strictSchema" : "#/$defs/schema" };
            }
            Schema["properties"] = slots;
            Schema["required"] = new JsonArray(slots.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        }
        var definitions = Schema["$defs"]!.AsObject();
        foreach (var name in new[] { "schema", "strictSchema" })
        {
            if (definitions[name]?["anyOf"] is not JsonArray variants) continue;
            var shallow = new JsonArray(variants.Select(variant =>
            {
                var copy = variant!.DeepClone().AsObject(); var properties = copy["properties"]!.AsObject();
                foreach (var child in new[] { "items", "properties", "additionalProperties" }) properties.Remove(child);
                copy["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                return (JsonNode)copy;
            }).ToArray());
            definitions["flat_" + name] = Object(new()
            {
                ["fields"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Object(new()
                {
                    ["path"] = new JsonObject { ["type"] = "string" }, ["required"] = new JsonObject { ["type"] = "boolean" },
                    ["schema"] = new JsonObject { ["anyOf"] = shallow }
                }) }
            });
        }
        Rewrite(Schema, ""); PlanningConstruction.PruneDefinitions(Schema);
        void Rewrite(JsonObject contract, string path)
        {
            if (contract["$ref"]?.ToString() is "#/$defs/schema" or "#/$defs/strictSchema")
            {
                var name = contract["$ref"]!.ToString()[8..]; _slots[path] = name == "strictSchema";
                contract["$ref"] = "#/$defs/flat_" + name; return;
            }
            foreach (var (key, child) in contract["properties"] as JsonObject ?? [])
                if (child is JsonObject field) Rewrite(field, path + "/" + PlanningSchemaReferences.Escape(key));
            foreach (var child in (contract["anyOf"] as JsonArray ?? []).OfType<JsonObject>()) Rewrite(child, path);
        }
    }

    internal JsonObject? Expand(JsonObject? candidate, out List<PlanningDiagnostic> diagnostics)
    {
        diagnostics = PlanningContractValidation.ValidateInstance(candidate, Schema)
            .Select(e => new PlanningDiagnostic("SCHEMA_DECLARATION_SHAPE_INVALID", "/declarations", e, ValidationStage: "conversion")).ToList();
        _invalidRows.Clear();
        if (diagnostics.Count != 0) return null;
        var result = candidate!.DeepClone().AsObject(); var findings = diagnostics;
        foreach (var (slot, strict) in _slots)
        {
            if (PlanningSchemaReferences.Read(result, slot) is not JsonObject declaration) continue;
            var fields = declaration["fields"]!.AsArray();
            var entries = new Dictionary<string, (JsonObject Node, bool Required, int Index)>(StringComparer.Ordinal);
            void Error(int index, string message)
            {
                var path = slot + "/fields/" + index;
                _invalidRows.Add(path);
                findings.Add(new("SCHEMA_DECLARATION_INVALID", "/declarations" + path, message, ValidationStage: "conversion"));
            }
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i]!; var path = field["path"]!.GetValue<string>();
                try
                {
                    var parts = Segments(path); var depth = 0;
                    for (var part = 0; part < parts.Length; part++)
                    {
                        if (++depth > 32) throw new InvalidOperationException("Schema nesting exceeds 32 levels.");
                        if (parts[part] == "properties" && part + 1 < parts.Length) part++;
                        else if (parts[part] is not ("items" or "additionalProperties"))
                            throw new InvalidOperationException("Schema paths contain /properties/NAME, /items or /additionalProperties edges only.");
                    }
                }
                catch (InvalidOperationException ex) { Error(i, ex.Message); continue; }
                var schema = field["schema"]!.DeepClone().AsObject();
                if (!entries.TryAdd(path, (schema, field["required"]!.GetValue<bool>(), i)))
                { Error(i, "Each schema path must be declared once."); Error(entries[path].Index, "Each schema path must be declared once."); }
                if (schema["kind"]?.ToString() != "inline") continue;
                if (schema["type"]?.ToString() == "object") { schema["properties"] = new JsonArray(); schema["additionalProperties"] = null; }
                if (schema["type"]?.ToString() == "array") schema["items"] = null;
            }
            if (!entries.TryGetValue("", out var root)) { Error(0, "Declare the root schema at the empty path."); continue; }
            foreach (var (path, entry) in entries.Where(p => p.Key.Length != 0).OrderBy(p => p.Key.Length))
            {
                var parts = Segments(path); var property = parts.Length >= 2 && parts[^2] == "properties";
                var count = property ? 2 : 1;
                var parentPath = string.Concat(parts.Take(parts.Length - count).Select(p => "/" + PlanningSchemaReferences.Escape(p)));
                if (!entries.TryGetValue(parentPath, out var parent)) { Error(entry.Index, "Declare the parent schema at '" + parentPath + "'."); continue; }
                var type = parent.Node["type"]?.ToString(); var edge = property ? "properties" : parts[^1];
                if (parent.Node["kind"]?.ToString() != "inline" || edge == "properties" && type != "object" ||
                    edge == "items" && type != "array" || edge == "additionalProperties" && (type != "object" || strict))
                { Error(entry.Index, "The parent type does not permit this child. Authoritative references cannot acquire inline fields."); Error(parent.Index, "Preserve the declared parent type and use a compatible child path."); continue; }
                if (property)
                    parent.Node["properties"]!.AsArray().Add((JsonNode)new JsonObject
                        { ["name"] = parts[^1], ["schema"] = entry.Node, ["required"] = entry.Required, ["default"] = null });
                else parent.Node[edge] = entry.Node;
            }
            foreach (var entry in entries.Values)
                if (entry.Node["kind"]?.ToString() == "inline" && entry.Node["type"]?.ToString() == "array" && entry.Node["items"] is null)
                    Error(entry.Index, "An array requires an /items declaration.");
            if (findings.Count != 0) continue;
            var segments = Segments(slot); var parentNode = result;
            foreach (var part in segments[..^1]) parentNode = parentNode[part]!.AsObject();
            parentNode[segments[^1]] = root.Node;
        }
        if (findings.Count != 0) return null;
        if (_additions.Count == 0) return result;
        var patched = new JsonObject();
        foreach (var coordinate in _additions)
        {
            var root = result[coordinate];
            if (root?["kind"]?.ToString() != "inline" || root["type"]?.ToString() != "object" || root["nullable"]?.GetValue<bool>() == true ||
                root["additionalProperties"] is not null || root["properties"] is not JsonArray properties || properties.Count > 4)
            {
                findings.Add(new("SCHEMA_DECLARATION_INVALID", "/declarations/" + PlanningSchemaReferences.Escape(coordinate),
                    "An additive repair declares an object root and at most four new named properties. Existing fields and map contracts are retained by the host.", ValidationStage: "conversion"));
                continue;
            }
            patched[coordinate] = new JsonObject { ["addProperties"] = properties.DeepClone() };
        }
        return findings.Count == 0 ? new JsonObject { ["changes"] = patched, ["remove"] = new JsonArray() } : null;
    }

    internal List<PlanningDiagnostic> PreservationFindings(JsonObject? previous, JsonObject candidate)
    {
        if (previous is null) return [];
        _ = Expand(previous, out var previousFindings);
        if (previousFindings.Any(d => d.Code == "SCHEMA_DECLARATION_SHAPE_INVALID")) return [];
        var result = new List<PlanningDiagnostic>();
        foreach (var slot in _slots.Keys)
        {
            if (PlanningSchemaReferences.Read(previous, slot)?["fields"] is not JsonArray fields) continue;
            var changed = PlanningSchemaReferences.Read(candidate, slot)?["fields"] as JsonArray ?? [];
            for (var i = 0; i < fields.Count; i++)
                if (!_invalidRows.Contains(slot + "/fields/" + i) && !changed.Any(row => JsonNode.DeepEquals(row, fields[i])))
                    result.Add(new("SCHEMA_DECLARATION_PRESERVATION", "/declarations" + slot + "/fields/" + i,
                        "Preserve this already valid declaration while repairing the diagnosed fields.", ValidationStage: "conversion"));
        }
        return result;
    }

    private static string[] Segments(string path)
    {
        if (path.Length == 0) return [];
        _ = PlanningSchemaReferences.Read(new JsonObject(), path); // shared JSON Pointer escape validation
        var parts = path[1..].Split('/').Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        return parts;
    }

    private static JsonObject Object(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
}
