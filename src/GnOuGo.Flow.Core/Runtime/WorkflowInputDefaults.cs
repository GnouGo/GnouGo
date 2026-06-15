using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Applies workflow-declared input defaults to a runtime input object.
/// Explicit values always win; only missing keys are populated.
/// </summary>
public static class WorkflowInputDefaults
{
    public static JsonObject Apply(WorkflowDef? workflow, JsonNode? inputs)
    {
        var merged = inputs?.DeepClone() as JsonObject ?? new JsonObject();
        var definitions = workflow?.Inputs;
        if (definitions == null || definitions.Count == 0)
            return merged;

        foreach (var (name, def) in definitions)
        {
            if (merged.ContainsKey(name) || def.Default == null)
                continue;

            merged[name] = InputDefaultValueConverter.ConvertToNode(def.Default, def);
        }

        return merged;
    }
}
