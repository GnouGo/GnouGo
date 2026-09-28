using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Schema-derived ordinary ports, or exact producer-declared mappings. No semantic name inference.</summary>
public static class TaskOperations
{
    // Compact presentation of declared constraints, never inferred from descriptions.
    internal static JsonObject FiniteDomain(JsonObject schema) => new(schema
        .Where(p => p.Key is "enum" or "const")
        .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));

    // Requiredness belongs to every containing object, not just the leaf port.
    internal static bool OutputNeedsCheck(JsonObject schema, IReadOnlyList<string> path)
    {
        var current = schema;
        foreach (var segment in path)
        {
            if (current["type"]?.ToString() != "object" || current["anyOf"] is not null || current["oneOf"] is not null ||
                current["required"] is not JsonArray required || !required.Any(n => n?.ToString() == segment)) return true;
            if (current["properties"]?[segment] is not JsonObject child) return true;
            current = child;
        }
        return false;
    }

    public static PlanningOperation Describe(PlanningCapability capability) => capability.Operation ?? new()
    {
        Id = capability.Id, Version = capability.Version, Description = capability.Description,
        Inputs = Ports(capability.InputSchema), Outputs = Ports(capability.OutputSchema)
    };

    internal static JsonObject ArtifactPorts(PlanningCapability capability)
    {
        var result = new JsonObject();
        if (capability.ArtifactContract is not { } contract) return result;
        var operation = PlanningCapabilityArguments.Editable(capability);
        var produced = new JsonArray(); var consumed = new JsonArray();
        foreach (var artifact in contract.Produces)
            if (Port(operation.Outputs, artifact.Pointer, artifact.Kind) is { } port) produced.Add((JsonNode)port);
        foreach (var artifact in contract.Consumes)
            if (Port(operation.Inputs, artifact.Pointer, artifact.Kind) is { } port)
            { port["required"] = artifact.Required; consumed.Add((JsonNode)port); }
        if (produced.Count > 0) result["produces"] = produced;
        if (consumed.Count > 0) result["consumes"] = consumed;
        return result;

        static JsonObject? Port(List<OperationPort> ports, string pointer, string kind)
        {
            var path = TaskArtifactBindings.Decode(pointer);
            var matches = ports.Where(p => path.Take(p.Path.Count).SequenceEqual(p.Path, StringComparer.Ordinal)).OrderByDescending(p => p.Path.Count).ToArray();
            if (matches.Length == 0) return null;
            var port = matches[0];
            var item = new JsonObject { ["port"] = port.Name, ["kind"] = kind };
            if (path.Length > port.Path.Count) item["fields"] = new JsonArray(path.Skip(port.Path.Count).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            return item;
        }
    }

    private static List<OperationPort> Ports(JsonObject schema) => schema["properties"] is not JsonObject properties ? [] :
        properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new OperationPort
        {
            Name = p.Key, Path = [p.Key], Schema = p.Value!.DeepClone().AsObject(),
            Required = schema["required"] is JsonArray required && required.Any(v => v?.ToString() == p.Key)
        }).ToList();

    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningCapability capability)
    {
        var operation = Describe(capability);
        var errors = PlanningCapabilityArguments.Validate(capability).ToList();
        if (string.IsNullOrWhiteSpace(operation.Id) || operation.Version != capability.Version)
            errors.Add(new("OPERATION_MAPPING_INVALID", "/operations", "Operation identity and exact contract version must be declared together."));
        Check(operation.Inputs, capability.InputSchema); Check(operation.Outputs, capability.OutputSchema);
        return errors;
        void Check(List<OperationPort> ports, JsonObject schema)
        {
            if (ports.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != ports.Count ||
                ports.Select(p => string.Join("\u001f", p.Path)).Distinct(StringComparer.Ordinal).Count() != ports.Count)
                errors.Add(new("OPERATION_MAPPING_AMBIGUOUS", "/operations/" + operation.Id, "Business ports and wire bindings must be unambiguous."));
            foreach (var port in ports)
            {
                JsonNode? resolved = schema;
                foreach (var segment in port.Path) resolved = resolved?["properties"]?[segment];
                if (string.IsNullOrWhiteSpace(port.Name) || port.Path.Count == 0 || !JsonNode.DeepEquals(resolved, port.Schema))
                    errors.Add(new("OPERATION_MAPPING_INVALID", "/operations/" + operation.Id + "/" + port.Name, "Business ports must map to an exact authoritative contract; opaque values have no typed ports."));
            }
        }
    }
}
