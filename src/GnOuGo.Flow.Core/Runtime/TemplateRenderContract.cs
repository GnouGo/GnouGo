using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Successful render results, shared by graph validation and runtime type resolution.</summary>
public static class TemplateRenderContract
{
    /// <param name="literalMode">The literal mode; null means unresolved, not the omitted default.</param>
    public static JsonObject OutputSchema(string? literalMode)
    {
        var schema = BuiltInStepContracts.Get("template.render")!.OutputSchema.DeepClone().AsObject();
        schema["required"] = literalMode is null ? new JsonArray("meta")
            : new JsonArray(literalMode == "json" ? "json" : "text", "meta");
        return schema;
    }
}
