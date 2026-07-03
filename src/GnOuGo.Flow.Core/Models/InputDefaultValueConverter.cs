using System.Globalization;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Converts workflow input defaults to JSON values, preserving the declared input type
/// when YAML scalar parsing leaves typed defaults as strings.
/// </summary>
internal static class InputDefaultValueConverter
{
    public static JsonNode? ConvertToNode(object? value, InputDef? definition = null)
    {
        if (value == null)
            return null;

        if (value is JsonNode node)
            return CoerceNode(node.DeepClone(), definition);

        if (value is string text && TryCoerceString(text, definition, out var coerced))
            return coerced;

        return value switch
        {
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
            IDictionary<string, object?> dict => ConvertDictionary(dict, definition),
            System.Collections.IDictionary dict => ConvertDictionary(dict, definition),
            System.Collections.IEnumerable enumerable when value is not string => ConvertArray(enumerable, definition),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonNode? CoerceNode(JsonNode? node, InputDef? definition)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && TryCoerceString(text, definition, out var coerced))
        {
            return coerced;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var child = obj[key];
                var childResult = CoerceNode(child, GetChildDefinition(definition, key));
                if (!ReferenceEquals(childResult, child))
                    obj[key] = childResult;
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                var childResult = CoerceNode(child, definition?.Items);
                if (!ReferenceEquals(childResult, child))
                    array[i] = childResult;
            }
        }

        return node;
    }

    private static bool TryCoerceString(string text, InputDef? definition, out JsonNode? coerced)
    {
        coerced = null;
        var type = definition?.Type.Trim().ToLowerInvariant();

        if (type is "number" && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            coerced = JsonValue.Create(number);
            return true;
        }

        if (type is "integer" && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            coerced = JsonValue.Create(integer);
            return true;
        }

        if (type is "boolean" or "bool")
        {
            var normalized = text.Trim();
            if (bool.TryParse(normalized, out var boolean))
            {
                coerced = JsonValue.Create(boolean);
                return true;
            }

            if (normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("y", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                coerced = JsonValue.Create(true);
                return true;
            }

            if (normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("n", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                coerced = JsonValue.Create(false);
                return true;
            }
        }

        return false;
    }

    private static JsonObject ConvertDictionary(IDictionary<string, object?> dict, InputDef? definition)
    {
        var obj = new JsonObject();
        foreach (var (key, item) in dict)
            obj[key] = ConvertToNode(item, GetChildDefinition(definition, key));
        return obj;
    }

    private static JsonObject ConvertDictionary(System.Collections.IDictionary dict, InputDef? definition)
    {
        var obj = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                obj[key] = ConvertToNode(entry.Value, GetChildDefinition(definition, key));
        }
        return obj;
    }

    private static JsonArray ConvertArray(System.Collections.IEnumerable enumerable, InputDef? definition)
    {
        var array = new JsonArray();
        foreach (var item in enumerable)
            array.Add(ConvertToNode(item, definition?.Items));
        return array;
    }

    private static InputDef? GetChildDefinition(InputDef? definition, string key)
    {
        if (definition?.Properties != null && definition.Properties.TryGetValue(key, out var propertyDefinition))
            return propertyDefinition;

        return definition?.AdditionalProperties;
    }
}
