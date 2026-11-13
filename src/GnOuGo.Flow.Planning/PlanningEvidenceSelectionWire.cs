using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Isomorphic navigation presentation. No producer, binding or business value is selected.
internal static class PlanningEvidenceSelectionWire
{
    private const string Suffix = "\nGnOuGo evidence selection object encoding v1.";

    internal static JsonNode Project(JsonNode schema)
    {
        var result = schema.DeepClone();
        if (result is not JsonObject) return result;
        if (result["properties"]?["evidenceRequests"]?["anyOf"] is not JsonArray { Count: 3 } branches ||
            branches.Count(b => b?["type"]?.ToString() == "null") != 1) return result;
        var arrays = branches.Where(b => b?["type"]?.ToString() == "array").ToArray();
        if (arrays.Length != 2 || arrays.Any(a => a!.AsObject().Any(p => p.Key is not ("type" or "items" or "minItems" or "maxItems")) ||
            a["minItems"]?.GetValue<int>() != 1 || a["items"] is not JsonObject items || items.Count != 2 ||
            items["type"]?.ToString() != "string" || items["enum"] is not JsonArray { Count: > 0 } values ||
            values.Any(v => v is not JsonValue value || !value.TryGetValue<string>(out _)))) return result;
        var single = arrays.Where(a => a!["maxItems"]?.GetValue<int>() == 1).ToArray();
        if (single.Length != 1) return result;
        var batch = arrays.Single(a => !ReferenceEquals(a, single[0]))!;
        if (batch["maxItems"] is not null && batch["maxItems"]!.GetValue<int>() < 1 ||
            single[0]!["items"]!["enum"]!.AsArray().Select(v => v!.ToString()).Intersect(
                batch["items"]!["enum"]!.AsArray().Select(v => v!.ToString()), StringComparer.Ordinal).Any()) return result;
        for (var i = 0; i < branches.Count; i++)
        {
            if (branches[i]?["type"]?.ToString() != "array") continue;
            var scalar = ReferenceEquals(branches[i], single[0]);
            var name = scalar ? "single" : "batch";
            branches[i] = new JsonObject { ["type"] = "object",
                ["properties"] = new JsonObject { [name] = (scalar ? branches[i]!["items"]! : branches[i]!).DeepClone() },
                ["required"] = new JsonArray(name), ["additionalProperties"] = false };
        }
        result["$comment"] = (result["$comment"]?.ToString() ?? "") + Suffix;
        return result;
    }

    internal static bool Issued(JsonNode schema) => schema is JsonObject && schema["$comment"]?.ToString().EndsWith(Suffix, StringComparison.Ordinal) == true;

    internal static JsonNode CanonicalSchema(JsonNode schema)
    {
        var result = schema.DeepClone();
        if (!Issued(result)) return result;
        if (result["properties"]?["evidenceRequests"]?["anyOf"] is not JsonArray { Count: 3 } branches)
            throw new PlanningConflictException("The issued evidence-selection encoding is malformed.");
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i]!;
            if (branch["type"]?.ToString() == "null") continue;
            if (branch["properties"] is not JsonObject { Count: 1 } properties ||
                branch["required"] is not JsonArray { Count: 1 } required ||
                branch["additionalProperties"]?.GetValue<bool>() != false)
                throw new PlanningConflictException("The issued evidence-selection branch is malformed.");
            if (properties["single"] is { } item && required[0]?.ToString() == "single")
                branches[i] = new JsonObject { ["type"] = "array", ["items"] = item.DeepClone(), ["minItems"] = 1, ["maxItems"] = 1 };
            else if (properties["batch"] is { } batch && required[0]?.ToString() == "batch") branches[i] = batch.DeepClone();
            else throw new PlanningConflictException("The issued evidence-selection branch is unknown.");
        }
        var comment = result["$comment"]!.ToString()[..^Suffix.Length];
        if (comment.Length == 0) result.AsObject().Remove("$comment"); else result["$comment"] = comment;
        return result;
    }

    internal static JsonNode CanonicalResponse(JsonNode response, JsonNode schema)
    {
        var result = response.DeepClone();
        if (!Issued(schema) || result["evidenceRequests"] is null) return result;
        if (result["evidenceRequests"] is not JsonObject { Count: 1 } selector)
            throw new PlanningConflictException("The response has no exact issued evidence selector.");
        if (selector["single"] is JsonValue value && value.TryGetValue<string>(out var id))
            result["evidenceRequests"] = new JsonArray(id);
        else if (selector["batch"] is JsonArray batch) result["evidenceRequests"] = batch.DeepClone();
        else throw new PlanningConflictException("The response evidence selector is malformed.");
        return result;
    }

    internal static JsonNode WireResponse(JsonNode response, JsonNode schema)
    {
        var result = response.DeepClone();
        if (!Issued(schema) || result["evidenceRequests"] is not JsonArray { Count: > 0 } values) return result;
        var single = schema["properties"]!["evidenceRequests"]!["anyOf"]!.AsArray()
            .Single(b => b?["properties"]?["single"] is not null)!["properties"]!["single"]!;
        result["evidenceRequests"] = values.Count == 1 && PlanningContractValidation.ValidateInstance(values[0], single).Count == 0
            ? new JsonObject { ["single"] = values[0]!.DeepClone() }
            : new JsonObject { ["batch"] = values.DeepClone() };
        return result;
    }
}
