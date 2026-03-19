using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Templating;

/// <summary>
/// Minimal AOT-friendly Mustache rendering engine.
/// Operates on JsonNode data — no reflection.
/// </summary>
public sealed class MustacheEngine
{
    /// <summary>
    /// Render a Mustache template with given data.
    /// </summary>
    public static string Render(string template, JsonNode? data, bool strict = false)
    {
        var tokens = MustacheParser.Parse(template);
        var sb = new StringBuilder();
        RenderTokens(tokens, data, sb, strict);
        return sb.ToString();
    }

    /// <summary>
    /// Render pre-parsed tokens.
    /// </summary>
    public static string Render(List<MustacheToken> tokens, JsonNode? data, bool strict = false)
    {
        var sb = new StringBuilder();
        RenderTokens(tokens, data, sb, strict);
        return sb.ToString();
    }

    private static void RenderTokens(List<MustacheToken> tokens, JsonNode? data, StringBuilder sb, bool strict)
    {
        foreach (var token in tokens)
        {
            switch (token)
            {
                case TextToken text:
                    sb.Append(text.Text);
                    break;

                case VariableToken variable:
                    var val = Resolve(variable.Name, data, strict);
                    if (val != null)
                        sb.Append(WebUtility.HtmlEncode(JsonValueToString(val)));
                    break;

                case RawVariableToken raw:
                    var rawVal = Resolve(raw.Name, data, strict);
                    if (rawVal != null)
                        sb.Append(JsonValueToString(rawVal));
                    break;

                case SectionToken section:
                    RenderSection(section.Name, section.Children, data, sb, strict);
                    break;

                case InvertedSectionToken inverted:
                    var invertedVal = Resolve(inverted.Name, data, strict: false);
                    if (IsFalsy(invertedVal))
                        RenderTokens(inverted.Children, data, sb, strict);
                    break;
            }
        }
    }

    private static void RenderSection(string name, List<MustacheToken> children, JsonNode? data, StringBuilder sb, bool strict)
    {
        var val = Resolve(name, data, strict: false);

        if (val is JsonArray arr)
        {
            // Iterate
            foreach (var item in arr)
                RenderTokens(children, item, sb, strict);
        }
        else if (val is JsonObject obj)
        {
            // Push context
            RenderTokens(children, obj, sb, strict);
        }
        else if (!IsFalsy(val))
        {
            // Truthy
            RenderTokens(children, data, sb, strict);
        }
    }

    private static JsonNode? Resolve(string dotPath, JsonNode? data, bool strict)
    {
        if (data == null)
        {
            if (strict) throw new MustacheRenderException($"Missing variable: {dotPath}");
            return null;
        }

        // Handle "." for current element
        if (dotPath == ".") return data;

        var parts = dotPath.Split('.');
        JsonNode? current = data;

        foreach (var part in parts)
        {
            if (current is JsonObject obj)
            {
                if (obj.TryGetPropertyValue(part, out var child))
                {
                    current = child;
                    continue;
                }
            }
            if (strict) throw new MustacheRenderException($"Missing variable: {dotPath}");
            return null;
        }

        return current;
    }

    private static bool IsFalsy(JsonNode? node)
    {
        if (node == null) return true;
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out bool b)) return !b;
            if (val.TryGetValue(out string? s)) return string.IsNullOrEmpty(s);
            if (val.TryGetValue(out int i)) return i == 0;
            if (val.TryGetValue(out double d)) return d == 0;
        }
        if (node is JsonArray arr) return arr.Count == 0;
        return false;
    }

    private static string JsonValueToString(JsonNode node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out string? s)) return s ?? "";
            if (val.TryGetValue(out bool b)) return b ? "true" : "false";
            if (val.TryGetValue(out int i)) return i.ToString();
            if (val.TryGetValue(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (val.TryGetValue(out long l)) return l.ToString();
        }
        return node.ToJsonString();
    }
}

public sealed class MustacheRenderException : Exception
{
    public MustacheRenderException(string message) : base(message) { }
}

