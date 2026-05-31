using System.Collections;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;
using YamlDotNet.Serialization;
namespace GnOuGo.Agent.Server.Components.Tracing;
internal static class TraceDebugValueRenderer
{
    private static readonly Regex OrderedListRegex = new(@"^\d+\.\s+", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^#{1,6}\s+\S+", RegexOptions.Compiled);
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    public static RenderFragment RenderValue(object? value, bool renderFormatted) => builder =>
    {
        try
        {
            if (!renderFormatted)
            {
                var rawText = TraceDebugUiHelpers.ToDisplayString(value);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    builder.OpenElement(0, "span");
                    builder.AddContent(1, string.Empty);
                    builder.CloseElement();
                    return;
                }
                builder.OpenElement(2, "pre");
                builder.AddAttribute(3, "class", "gnougo-trace-detail__code");
                builder.AddContent(4, rawText);
                builder.CloseElement();
                return;
            }
            var display = PrepareDisplayValue(value);
            switch (display.Format)
            {
                case TraceValueFormat.Markdown:
                    builder.OpenElement(0, "div");
                    builder.AddAttribute(1, "class", "gnougo-trace-detail__markdown markdown-body");
                    builder.AddContent(2, RenderMarkdown(display.Content));
                    builder.CloseElement();
                    return;
                case TraceValueFormat.Json:
                    builder.OpenElement(3, "pre");
                    builder.AddAttribute(4, "class", "gnougo-trace-detail__code gnougo-trace-detail__code--json");
                    builder.OpenElement(5, "code");
                    builder.AddAttribute(6, "class", "gnougo-trace-detail__syntax");
                    builder.AddMarkupContent(7, BuildJsonHighlightedHtml(display.Content));
                    builder.CloseElement();
                    builder.CloseElement();
                    return;
                case TraceValueFormat.Yaml:
                    builder.OpenElement(8, "pre");
                    builder.AddAttribute(9, "class", "gnougo-trace-detail__code gnougo-trace-detail__code--yaml");
                    builder.OpenElement(10, "code");
                    builder.AddAttribute(11, "class", "gnougo-trace-detail__syntax");
                    builder.AddMarkupContent(12, BuildYamlHighlightedHtml(display.Content));
                    builder.CloseElement();
                    builder.CloseElement();
                    return;
                default:
                    if (display.Content.Length > 180)
                    {
                        builder.OpenElement(13, "pre");
                        builder.AddAttribute(14, "class", "gnougo-trace-detail__code");
                        builder.AddContent(15, display.Content);
                        builder.CloseElement();
                        return;
                    }
                    builder.OpenElement(16, "span");
                    builder.AddContent(17, display.Content);
                    builder.CloseElement();
                    return;
            }
        }
        catch
        {
            builder.OpenElement(20, "pre");
            builder.AddAttribute(21, "class", "gnougo-trace-detail__code");
            builder.AddContent(22, TraceDebugUiHelpers.ToDisplayString(value));
            builder.CloseElement();
        }
    };
    private static DisplayValue PrepareDisplayValue(object? value)
    {
        var text = TraceDebugUiHelpers.ToDisplayString(value);
        if (string.IsNullOrWhiteSpace(text))
            return new DisplayValue(TraceValueFormat.Plain, string.Empty);
        var trimmed = text.Trim();
        if (TryExtractSingleCodeFence(trimmed, out var language, out var fencedContent))
        {
            var content = fencedContent.Trim();
            if (IsJsonLanguage(language) && TryFormatJson(content, out var fencedJson))
                return new DisplayValue(TraceValueFormat.Json, fencedJson);
            if (IsYamlLanguage(language) || LooksLikeYaml(content))
                return new DisplayValue(TraceValueFormat.Yaml, content);
            return new DisplayValue(TraceValueFormat.Plain, content);
        }
        if (TryFormatJson(trimmed, out var prettyJson))
            return new DisplayValue(TraceValueFormat.Json, prettyJson);
        if (LooksLikeMarkdown(trimmed) && !LooksLikeYaml(trimmed))
            return new DisplayValue(TraceValueFormat.Markdown, trimmed);
        if (LooksLikeYaml(trimmed))
            return new DisplayValue(TraceValueFormat.Yaml, trimmed);
        return new DisplayValue(TraceValueFormat.Plain, text);
    }
    private static bool TryFormatJson(string input, out string prettyJson)
    {
        prettyJson = string.Empty;
        if (!(input.StartsWith('{') || input.StartsWith('[')))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(input);
            prettyJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
    private static bool TryExtractSingleCodeFence(string markdown, out string language, out string content)
    {
        language = string.Empty;
        content = string.Empty;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 2 || !lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
            return false;
        var firstLine = lines[0].Trim();
        if (!lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
            return false;
        language = firstLine.Length > 3 ? firstLine[3..].Trim() : string.Empty;
        content = string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
        return true;
    }
    private static bool IsJsonLanguage(string language)
        => language.Equals("json", StringComparison.OrdinalIgnoreCase)
           || language.Equals("jsonc", StringComparison.OrdinalIgnoreCase);
    private static bool IsYamlLanguage(string language)
        => language.Equals("yaml", StringComparison.OrdinalIgnoreCase)
           || language.Equals("yml", StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeYaml(string value)
    {
        if (value.StartsWith("---", StringComparison.Ordinal) || value.StartsWith("...", StringComparison.Ordinal))
            return true;
        if (!value.Contains(':', StringComparison.Ordinal) && !value.Contains("- ", StringComparison.Ordinal))
            return false;
        var lines = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return false;
        var yamlLikeLines = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith('-') || line.StartsWith('*') || line.StartsWith("```", StringComparison.Ordinal))
                continue;
            if (!line.Contains(':', StringComparison.Ordinal))
                continue;
            var idx = line.IndexOf(':', StringComparison.Ordinal);
            if (idx <= 0)
                continue;
            var key = line[..idx].Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Contains(' ') || key.Contains('{') || key.Contains('['))
                continue;
            yamlLikeLines++;
            if (yamlLikeLines >= 2)
                break;
        }
        if (yamlLikeLines < 2)
            return false;
        try
        {
            var parsed = YamlDeserializer.Deserialize<object?>(value);
            return parsed is IDictionary or IList;
        }
        catch
        {
            return false;
        }
    }
    private static bool LooksLikeMarkdown(string value)
    {
        if (value.Contains("```", StringComparison.Ordinal)
            || value.Contains("\n> ", StringComparison.Ordinal)
            || value.Contains("| ---", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (HeadingRegex.IsMatch(trimmed)
                || trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("> ", StringComparison.Ordinal)
                || OrderedListRegex.IsMatch(trimmed))
            {
                return true;
            }
        }
        return false;
    }
    private static string BuildJsonHighlightedHtml(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        try
        {
            var node = JsonNode.Parse(json);
            var sb = new StringBuilder(json.Length + 128);
            WriteJsonNode(sb, node, 0);
            return sb.ToString();
        }
        catch
        {
            return WebUtility.HtmlEncode(json);
        }
    }
    private static string BuildYamlHighlightedHtml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return string.Empty;
        try
        {
            var parsed = YamlDeserializer.Deserialize<object?>(yaml);
            var sb = new StringBuilder(yaml.Length + 128);
            WriteYamlNode(sb, parsed, 0, isRoot: true);
            return sb.ToString();
        }
        catch
        {
            return WebUtility.HtmlEncode(yaml);
        }
    }
    private static void WriteJsonNode(StringBuilder sb, JsonNode? node, int indent)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">{</span>");
                if (obj.Count > 0)
                {
                    var index = 0;
                    foreach (var pair in obj)
                    {
                        sb.Append('\n').Append(new string(' ', (indent + 1) * 2));
                        sb.Append("<span class=\"gnougo-trace-detail__tok-key\">\"")
                          .Append(WebUtility.HtmlEncode(pair.Key))
                          .Append("\"</span><span class=\"gnougo-trace-detail__tok-punc\">: </span>");
                        WriteJsonNode(sb, pair.Value, indent + 1);
                        if (index++ < obj.Count - 1)
                            sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">,</span>");
                    }
                    sb.Append('\n').Append(new string(' ', indent * 2));
                }
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">}</span>");
                return;
            case JsonArray arr:
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">[</span>");
                for (var i = 0; i < arr.Count; i++)
                {
                    sb.Append('\n').Append(new string(' ', (indent + 1) * 2));
                    WriteJsonNode(sb, arr[i], indent + 1);
                    if (i < arr.Count - 1)
                        sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">,</span>");
                }
                if (arr.Count > 0)
                    sb.Append('\n').Append(new string(' ', indent * 2));
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">]</span>");
                return;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s))
                {
                    sb.Append("<span class=\"gnougo-trace-detail__tok-string\">\"")
                      .Append(WebUtility.HtmlEncode(s))
                      .Append("\"</span>");
                    return;
                }
                if (val.TryGetValue<bool>(out var b))
                {
                    sb.Append("<span class=\"gnougo-trace-detail__tok-bool\">")
                      .Append(b ? "true" : "false")
                      .Append("</span>");
                    return;
                }
                var raw = val.ToJsonString();
                if (string.Equals(raw, "null", StringComparison.Ordinal))
                {
                    sb.Append("<span class=\"gnougo-trace-detail__tok-null\">null</span>");
                    return;
                }
                sb.Append("<span class=\"gnougo-trace-detail__tok-number\">")
                  .Append(WebUtility.HtmlEncode(raw))
                  .Append("</span>");
                return;
            default:
                sb.Append("<span class=\"gnougo-trace-detail__tok-null\">null</span>");
                return;
        }
    }
    private static void WriteYamlNode(StringBuilder sb, object? node, int indent, bool isRoot = false)
    {
        if (node is IDictionary dictionary)
        {
            var hasLines = false;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (hasLines)
                    sb.Append('\n');
                sb.Append(new string(' ', indent * 2));
                sb.Append("<span class=\"gnougo-trace-detail__tok-key\">")
                  .Append(WebUtility.HtmlEncode(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty))
                  .Append("</span><span class=\"gnougo-trace-detail__tok-punc\">:</span>");
                if (entry.Value is IDictionary or IList)
                {
                    sb.Append('\n');
                    WriteYamlNode(sb, entry.Value, indent + 1);
                }
                else
                {
                    sb.Append(' ');
                    AppendYamlScalarToken(sb, entry.Value);
                }
                hasLines = true;
            }
            if (!hasLines && isRoot)
                sb.Append("<span class=\"gnougo-trace-detail__tok-null\">null</span>");
            return;
        }
        if (node is IList list)
        {
            var hasItems = false;
            foreach (var item in list)
            {
                if (hasItems)
                    sb.Append('\n');
                sb.Append(new string(' ', indent * 2));
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">-</span>");
                if (item is IDictionary or IList)
                {
                    sb.Append('\n');
                    WriteYamlNode(sb, item, indent + 1);
                }
                else
                {
                    sb.Append(' ');
                    AppendYamlScalarToken(sb, item);
                }
                hasItems = true;
            }
            if (!hasItems && isRoot)
                sb.Append("<span class=\"gnougo-trace-detail__tok-punc\">[]</span>");
            return;
        }
        AppendYamlScalarToken(sb, node);
    }
    private static void AppendYamlScalarToken(StringBuilder sb, object? value)
    {
        if (value is null)
        {
            sb.Append("<span class=\"gnougo-trace-detail__tok-null\">null</span>");
            return;
        }
        switch (value)
        {
            case bool b:
                sb.Append("<span class=\"gnougo-trace-detail__tok-bool\">")
                  .Append(b ? "true" : "false")
                  .Append("</span>");
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                sb.Append("<span class=\"gnougo-trace-detail__tok-number\">")
                  .Append(WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty))
                  .Append("</span>");
                return;
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                sb.Append("<span class=\"gnougo-trace-detail__tok-string\">\"")
                  .Append(WebUtility.HtmlEncode(text))
                  .Append("\"</span>");
                return;
        }
    }
    private static RenderFragment RenderMarkdown(string markdown) => builder =>
    {
        try
        {
            var html = Markdig.Markdown.ToHtml(markdown, MarkdownPipeline);
            builder.AddMarkupContent(0, html);
        }
        catch
        {
            builder.OpenElement(1, "pre");
            builder.AddAttribute(2, "class", "gnougo-trace-detail__code");
            builder.AddContent(3, markdown);
            builder.CloseElement();
        }
    };
}

