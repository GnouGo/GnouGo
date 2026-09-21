using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningCapabilityArguments
{
    internal static JsonObject EditableArguments(PlanningCapability capability)
    {
        var input = capability.InputSchema.DeepClone().AsObject();
        foreach (var binding in capability.RequestBindings)
        {
            var parts = binding.Path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonNode? parent = input;
            for (var i = 0; i < parts.Length - 1; i++) parent = parent?["properties"]?[parts[i]];
            if (parts.Length > 0 && parent?["properties"] is JsonObject fields)
            {
                fields.Remove(parts[^1]);
                if (parent["required"] is JsonArray required)
                    for (var i = required.Count - 1; i >= 0; i--) if (required[i]?.ToString() == parts[^1]) required.RemoveAt(i);
            }
        }
        return input;
    }
}
