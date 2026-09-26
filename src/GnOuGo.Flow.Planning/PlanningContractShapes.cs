using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning;
internal static class PlanningContractShapes
{
    internal static JsonObject Opaque() => new() { ["x-gnougo-opaque"] = true };
    internal static bool IsOpaque(JsonObject schema) => schema["x-gnougo-opaque"]?.ToString() == "true";
}
