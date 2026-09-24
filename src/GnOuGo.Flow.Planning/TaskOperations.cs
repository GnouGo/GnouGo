using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Schema-derived ordinary ports, or exact producer-declared mappings. No semantic name inference.</summary>
public static class TaskOperations
{
    public static PlanningOperation Describe(PlanningCapability capability) => capability.Operation ?? new()
    {
        Id = capability.Id, Version = capability.Version, Description = capability.Description,
        Inputs = Ports(capability.InputSchema), Outputs = Ports(capability.OutputSchema)
    };

    private static List<OperationPort> Ports(JsonObject schema) => schema["properties"] is not JsonObject properties ? [] :
        properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new OperationPort
        {
            Name = p.Key, Path = [p.Key], Schema = p.Value!.DeepClone().AsObject(),
            Required = schema["required"] is JsonArray required && required.Any(v => v?.ToString() == p.Key)
        }).ToList();

    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningCapability capability)
    {
        var operation = Describe(capability);
        var errors = new List<PlanningDiagnostic>();
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
