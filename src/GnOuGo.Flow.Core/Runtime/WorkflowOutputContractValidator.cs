using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
namespace GnOuGo.Flow.Core.Runtime;

public static class WorkflowOutputContractValidator
{
    public const string WeakOutputSchemaCode = "WEAK_OUTPUT_SCHEMA";

    public static void CollectWeakOutputSchemaDiagnostics(
        OutputDef output,
        string path,
        JsonArray diagnostics,
        bool allowSkillScalarTypeShorthand)
    {
        var descriptor = OutputDefToDescriptor(output, allowSkillScalarTypeShorthand);
        CollectWeakDescriptorDiagnostics(descriptor, path, diagnostics);
    }

    public static JsonObject BuildWeakOutputSchemaDiagnostic(string path, string message, string expected)
        => new()
        {
            ["code"] = WeakOutputSchemaCode,
            ["phase"] = "output_schema_validation",
            ["location"] = path,
            ["message"] = message,
            ["expected"] = expected,
            ["hint"] = "Generated workflow outputs are public contracts and must be concrete.",
            ["llm_guidance"] = "Use the exact output path and add a concrete schema. Arrays need items; object outputs and object array items need properties; do not use any."
        };

    private static void CollectWeakDescriptorDiagnostics(
        FlowTypeDescriptor descriptor,
        string path,
        JsonArray diagnostics)
    {
        descriptor = descriptor.RemoveNull();
        switch (descriptor.Kind)
        {
            case FlowTypeKind.Any:
                diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                    path,
                    "Output schema uses type any.",
                    "concrete scalar, object, array, or dictionary schema"));
                break;

            case FlowTypeKind.Array:
                if (descriptor.Items == null || descriptor.Items.IsOpaque)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Array output schema does not declare items.",
                        "array with concrete items schema"));
                }
                else
                {
                    CollectWeakDescriptorDiagnostics(descriptor.Items, path + ".items", diagnostics);
                }
                break;

            case FlowTypeKind.Object:
                if (descriptor.Properties.Count == 0)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Object output schema does not declare properties.",
                        "object with non-empty properties"));
                }
                foreach (var (name, property) in descriptor.Properties)
                    CollectWeakDescriptorDiagnostics(property.Type, $"{path}.properties.{name}", diagnostics);
                break;

            case FlowTypeKind.Dictionary:
                if (descriptor.AdditionalProperties == null)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Dictionary output schema does not declare additional_properties.",
                        "dictionary with concrete additional_properties schema"));
                }
                else
                {
                    CollectWeakDescriptorDiagnostics(descriptor.AdditionalProperties, path + ".additional_properties", diagnostics);
                }
                break;

            case FlowTypeKind.Union:
                if (descriptor.Variants.Count == 0)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Output schema uses type any.",
                        "concrete scalar, object, array, or dictionary schema"));
                    break;
                }
                foreach (var variant in descriptor.Variants)
                    CollectWeakDescriptorDiagnostics(variant, path, diagnostics);
                break;
        }
    }

    private static FlowTypeDescriptor OutputDefToDescriptor(OutputDef output, bool allowSkillScalarTypeShorthand)
    {
        var descriptor = FlowTypeDescriptorConverter.FromOutputDef(output);
        if (!allowSkillScalarTypeShorthand || !descriptor.IsOpaque)
            return descriptor;

        return NormalizeType(output.Expr) switch
        {
            "string" => FlowTypeDescriptor.String,
            "number" => FlowTypeDescriptor.Number,
            "integer" => FlowTypeDescriptor.Integer,
            "boolean" => FlowTypeDescriptor.Boolean,
            "array" => FlowTypeDescriptor.Array(),
            "object" => FlowTypeDescriptor.Object(),
            "dictionary" => FlowTypeDescriptor.Dictionary(),
            _ => descriptor
        };
    }

    private static string NormalizeType(string? type) => type?.ToLowerInvariant() switch
    {
        "string" => "string",
        "number" => "number",
        "integer" => "integer",
        "boolean" or "bool" => "boolean",
        "array" => "array",
        "object" => "object",
        "dictionary" => "dictionary",
        "null" => "null",
        "any" => "any",
        _ => "any"
    };
}
