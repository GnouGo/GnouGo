using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Repair assessment coordinates without dropping supported findings.</summary>
internal sealed class PlanningReviewEvidenceRepair
{
    private readonly Dictionary<string, (int Index, string Field)> _coordinates = new(StringComparer.Ordinal);
    internal JsonObject Schema { get; }
    internal JsonObject Context { get; } = new();

    internal PlanningReviewEvidenceRepair(JsonObject candidate, IReadOnlyList<PlanningDiagnostic> diagnostics,
        IEnumerable<string> evidence, IReadOnlyDictionary<string, string> locations)
    {
        var properties = new JsonObject();
        foreach (var diagnostic in diagnostics)
        {
            var path = diagnostic.Location.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length != 4 || path[0] != "semanticReview" || path[1] != "findings" ||
                !int.TryParse(path[2], out var index) || candidate["findings"] is not JsonArray findings || index < 0 || index >= findings.Count ||
                path[3] is not ("evidence" or "location")) throw new InvalidOperationException("An assessment repair needs an exact invalid field coordinate.");
            var finding = findings[index]!.AsObject(); var key = "finding_" + index + "_" + path[3];
            if (!_coordinates.TryAdd(key, (index, path[3]))) continue;
            var values = (path[3] == "evidence" ? evidence : locations.Where(p => p.Value == finding["workflow"]!.GetValue<string>()).Select(p => p.Key))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length == 0) throw new InvalidOperationException("No authoritative value is available for this assessment field.");
            properties[key] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
            Context[key] = finding.DeepClone();
        }
        Schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray())
        };
    }

    internal JsonObject Apply(JsonObject candidate, JsonObject patch)
    {
        if (PlanningContractValidation.ValidateInstance(patch, Schema).Count != 0) throw new InvalidOperationException("The assessment patch does not match its allowed fields.");
        var result = (JsonObject)candidate.DeepClone();
        foreach (var (key, coordinate) in _coordinates) result["findings"]![coordinate.Index]![coordinate.Field] = patch[key]!.DeepClone();
        return result;
    }
}
