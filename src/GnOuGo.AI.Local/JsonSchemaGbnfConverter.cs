using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Local;

/// <summary>
/// Compiles the deterministic JSON Schema subset used by GnOuGo structured requests
/// into llama.cpp GBNF. The shared AI.Core validator remains authoritative after generation.
/// </summary>
internal sealed class JsonSchemaGbnfConverter
{
    private const int MaximumConstrainedArrayItems = 16;

    private readonly JsonObject _root;
    private readonly List<string> _rules = [];
    private readonly HashSet<JsonObject> _referenceStack = new(ReferenceEqualityComparer.Instance);
    private int _nextRule;

    private JsonSchemaGbnfConverter(JsonObject root) => _root = root;

    public static string Convert(JsonNode? schema)
    {
        if (schema is not JsonObject root)
            return GenericJsonGrammar;

        var converter = new JsonSchemaGbnfConverter(root);
        var rootRule = converter.CreateRule(root);
        var grammar = new StringBuilder();
        grammar.Append("root ::= ").Append(rootRule).AppendLine();
        foreach (var rule in converter._rules)
            grammar.AppendLine(rule);
        grammar.AppendLine(CommonRules);
        return grammar.ToString();
    }

    private string CreateRule(JsonObject schema)
    {
        if (schema["$ref"] is JsonValue referenceValue
            && referenceValue.TryGetValue<string>(out var reference)
            && TryResolveReference(reference, out var referenced))
        {
            if (!_referenceStack.Add(referenced))
                return "json-value";
            try { return CreateRule(referenced); }
            finally { _referenceStack.Remove(referenced); }
        }

        if (schema.TryGetPropertyValue("const", out var constant))
            return CreateLiteralRule(constant?.ToJsonString() ?? "null");

        if (schema["enum"] is JsonArray allowed && allowed.Count > 0)
        {
            var alternatives = allowed.Select(value => QuoteTerminal(value?.ToJsonString() ?? "null"));
            return AddRule($"({string.Join(" | ", alternatives)}) ws");
        }

        if (schema["oneOf"] is JsonArray oneOf && oneOf.OfType<JsonObject>().Any())
            return CreateAlternativesRule(oneOf);
        if (schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any())
            return CreateAlternativesRule(anyOf);

        var types = ReadTypes(schema);
        if (types.Count > 1)
        {
            var alternatives = types.Select(type => CreateTypeRule(type, schema));
            return AddRule(string.Join(" | ", alternatives));
        }

        return CreateTypeRule(types.Count == 1 ? types[0] : InferType(schema), schema);
    }

    private string CreateAlternativesRule(JsonArray variants)
        => AddRule(string.Join(" | ", variants.OfType<JsonObject>().Select(CreateRule)));

    private string CreateTypeRule(string? type, JsonObject schema)
        => type switch
        {
            "object" => CreateObjectRule(schema),
            "array" => CreateArrayRule(schema),
            "string" => "json-string",
            "integer" => "json-integer",
            "number" => "json-number",
            "boolean" => "json-boolean",
            "null" => "json-null",
            _ => "json-value"
        };

    private string CreateObjectRule(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties || properties.Count == 0)
            return AddRule("\"{\" ws \"}\" ws");

        var members = new List<string>(properties.Count);
        foreach (var (name, propertySchema) in properties)
        {
            if (propertySchema is not JsonObject propertyObject)
                return "json-object";
            members.Add($"{QuoteTerminal(JsonValue.Create(name)!.ToJsonString())} ws \":\" ws {CreateRule(propertyObject)}");
        }

        return AddRule($"\"{{\" ws {string.Join(" \",\" ws ", members)} \"}}\" ws");
    }

    private string CreateArrayRule(JsonObject schema)
    {
        if (schema["items"] is not JsonObject items)
            return "json-array";

        var minimum = ReadNonNegativeInteger(schema["minItems"]) ?? 0;
        var maximum = ReadNonNegativeInteger(schema["maxItems"]);
        if (maximum is null || maximum > MaximumConstrainedArrayItems || minimum > maximum)
            return "json-array";

        var itemRule = CreateRule(items);
        var body = new StringBuilder("\"[\" ws");
        if (minimum == 0)
        {
            if (maximum > 0)
            {
                body.Append(" (").Append(itemRule);
                for (var index = 1; index < maximum; index++)
                    body.Append(" (\",\" ws ").Append(itemRule).Append(")?");
                body.Append(")?");
            }
        }
        else
        {
            body.Append(' ').Append(itemRule);
            for (var index = 1; index < minimum; index++)
                body.Append(" \",\" ws ").Append(itemRule);
            for (var index = minimum; index < maximum; index++)
                body.Append(" (\",\" ws ").Append(itemRule).Append(")?");
        }
        body.Append(""" "]" ws""");
        return AddRule(body.ToString());
    }

    private string CreateLiteralRule(string json)
        => AddRule($"{QuoteTerminal(json)} ws");

    private string AddRule(string expression)
    {
        var name = $"schema{_nextRule++}";
        _rules.Add($"{name} ::= {expression}");
        return name;
    }

    private bool TryResolveReference(string? reference, out JsonObject resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith('#'))
            return false;
        if (reference.Length == 1)
        {
            resolved = _root;
            return true;
        }
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            return false;

        JsonNode? current = _root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var child) => child,
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                                     && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current is null)
                return false;
        }

        resolved = current as JsonObject ?? null!;
        return resolved is not null;
    }

    private static List<string> ReadTypes(JsonObject schema)
    {
        if (schema["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type is not null)
            return [type];
        if (schema["type"] is JsonArray array)
            return array.OfType<JsonValue>()
                .Select(candidate => candidate.TryGetValue<string>(out var type) ? type : null)
                .Where(static type => type is not null)
                .Cast<string>()
                .ToList();
        return [];
    }

    private static string? InferType(JsonObject schema)
    {
        if (schema.ContainsKey("properties") || schema.ContainsKey("required") || schema.ContainsKey("additionalProperties")) return "object";
        if (schema.ContainsKey("items") || schema.ContainsKey("minItems") || schema.ContainsKey("maxItems")) return "array";
        if (schema.ContainsKey("pattern") || schema.ContainsKey("minLength") || schema.ContainsKey("maxLength")) return "string";
        if (schema.ContainsKey("minimum") || schema.ContainsKey("maximum") || schema.ContainsKey("multipleOf")) return "number";
        return null;
    }

    private static int? ReadNonNegativeInteger(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var integer) && integer >= 0)
            return integer;
        if (value.TryGetValue<long>(out var longInteger) && longInteger is >= 0 and <= int.MaxValue)
            return (int)longInteger;
        return null;
    }

    private static string QuoteTerminal(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    private const string CommonRules = """
        json-value ::= json-object | json-array | json-string | json-number | json-boolean | json-null
        json-object ::= "{" ws (json-string ":" ws json-value ("," ws json-string ":" ws json-value)*)? "}" ws
        json-array ::= "[" ws (json-value ("," ws json-value)*)? "]" ws
        json-string ::= "\"" ([^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt/] | "u" [0-9a-fA-F]{4}))* "\"" ws
        json-integer ::= "-"? ("0" | [1-9] [0-9]*) ws
        json-number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [-+]? [0-9]+)? ws
        json-boolean ::= ("true" | "false") ws
        json-null ::= "null" ws
        ws ::= ([ \t\n] ws)?
        """;

    private const string GenericJsonGrammar = """
        root ::= ws json-value ws
        json-value ::= json-object | json-array | json-string | json-number | json-boolean | json-null
        json-object ::= "{" ws (json-string ":" ws json-value ("," ws json-string ":" ws json-value)*)? "}" ws
        json-array ::= "[" ws (json-value ("," ws json-value)*)? "]" ws
        json-string ::= "\"" ([^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt/] | "u" [0-9a-fA-F]{4}))* "\"" ws
        json-integer ::= "-"? ("0" | [1-9] [0-9]*) ws
        json-number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [-+]? [0-9]+)? ws
        json-boolean ::= ("true" | "false") ws
        json-null ::= "null" ws
        ws ::= ([ \t\n] ws)?
        """;
}
