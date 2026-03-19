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

            merged[name] = ConvertDefaultToNode(def.Default);
        }

        return merged;
    }

    private static JsonNode? ConvertDefaultToNode(object value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            short s16 => JsonValue.Create(s16),
            byte b8 => JsonValue.Create(b8),
            uint ui => JsonValue.Create(ui),
            ulong ul => JsonValue.Create(ul),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            IDictionary<string, object?> dict => ConvertDictionary(dict),
            System.Collections.IDictionary dict => ConvertDictionary(dict),
            System.Collections.IEnumerable enumerable when value is not string => ConvertArray(enumerable),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonObject ConvertDictionary(IDictionary<string, object?> dict)
    {
        var obj = new JsonObject();
        foreach (var (key, item) in dict)
            obj[key] = item == null ? null : ConvertDefaultToNode(item);
        return obj;
    }

    private static JsonObject ConvertDictionary(System.Collections.IDictionary dict)
    {
        var obj = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                obj[key] = entry.Value == null ? null : ConvertDefaultToNode(entry.Value);
        }
        return obj;
    }

    private static JsonArray ConvertArray(System.Collections.IEnumerable enumerable)
    {
        var array = new JsonArray();
        foreach (var item in enumerable)
            array.Add(item == null ? null : ConvertDefaultToNode(item));
        return array;
    }
}
