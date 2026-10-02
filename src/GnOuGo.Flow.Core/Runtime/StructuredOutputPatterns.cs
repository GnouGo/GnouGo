using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;

namespace GnOuGo.Flow.Core.Runtime;

// Authoritative contracts use the runtime regex engine. The wire profile is the
// conservative intersection with JavaScript Unicode and nonbacktracking syntax.
internal sealed class StructuredOutputPatterns
{
    private readonly Dictionary<string, Compatibility> _cache = new(StringComparer.Ordinal);
    private Engine? _engine;
    internal enum Compatibility { Invalid, Nonportable, Portable }

    internal Compatibility Check(string pattern)
    {
        if (_cache.TryGetValue(pattern, out var saved)) return saved;
        var result = CheckUncached(pattern);
        _cache.Add(pattern, result);
        return result;
    }

    private Compatibility CheckUncached(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return Compatibility.Invalid; }
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
            _engine ??= new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(1)).MaxStatements(100).LimitMemory(8_000_000));
            _engine.SetValue("pattern", pattern).Evaluate("new RegExp(pattern, 'u')");
            return Compatibility.Portable;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or JavaScriptException)
        { return Compatibility.Nonportable; }
    }

    internal JsonObject Project(JsonObject schema)
    {
        var clone = schema.DeepClone().AsObject();
        Visit(clone);
        return clone;
    }

    private void Visit(JsonNode? node)
    {
        if (node is not JsonObject schema) return;
        if (schema["pattern"] is JsonValue value && value.TryGetValue<string>(out var pattern) &&
            pattern is not null && Check(pattern) == Compatibility.Nonportable)
            schema.Remove("pattern");
        // Visit schema positions only. Business values inside enum/const/default
        // may have fields named pattern, properties or items and are never edited.
        foreach (var keyword in new[] { "$defs", "definitions", "properties", "patternProperties", "dependentSchemas" })
            if (schema[keyword] is JsonObject map)
                foreach (var child in map) Visit(child.Value);
        foreach (var keyword in new[] { "items", "additionalProperties", "contains", "not", "if", "then", "else", "propertyNames", "unevaluatedProperties", "unevaluatedItems" })
            Visit(schema[keyword]);
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
            if (schema[keyword] is JsonArray list)
                foreach (var child in list) Visit(child);
    }
}
