using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Public deterministic validation boundary shared by independent planning implementations.</summary>
public static class PlanningContractValidation
{
    public static IReadOnlyList<string> ValidateSchema(JsonNode schema, bool strict = false)
        => JsonSchemaContractValidator.ValidateSchema(schema, strict);

    public static IReadOnlyList<string> ValidateInstance(JsonNode? value, JsonNode schema)
        => JsonSchemaContractValidator.ValidateInstance(value, schema);
}
