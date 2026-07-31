using System.Text.Json;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Prepares ASK Human context once, before it enters the Blazor render tree.
/// Human-input rendering deliberately does not generate Mermaid diagrams:
/// diagram generation is optional presentation work and must not delay the
/// interactive workflow continuation.
/// </summary>
internal static class HumanInputContextMarkdownFormatter
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static string Format(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return string.Empty;

        var normalized = NormalizeLineEndings(context).Trim();

        if (TryExtractSingleCodeFence(normalized, out var language, out var fencedContent))
        {
            var content = NormalizeLineEndings(fencedContent).Trim();
            if (IsJsonLanguage(language) && TryFormatJson(content, out var prettyJson))
                return BuildFencedCode("json", prettyJson);
            if (IsYamlLanguage(language) || (string.IsNullOrWhiteSpace(language) && LooksLikeYamlDocument(content)))
                return BuildFencedCode(string.IsNullOrWhiteSpace(language) ? "yaml" : language, content);
            return BuildFencedCode(language, content);
        }

        if (TryFormatJson(normalized, out var formattedJson))
            return BuildFencedCode("json", formattedJson);

        if (LooksLikeYamlDocument(normalized))
            return BuildFencedCode("yaml", normalized);

        return normalized;
    }

    private static bool TryFormatJson(string input, out string prettyJson)
    {
        prettyJson = string.Empty;
        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(input);
            prettyJson = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeYamlDocument(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("---", StringComparison.Ordinal))
            return true;

        var lines = NormalizeLineEndings(value)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return false;

        // Ordinary Markdown summaries frequently contain lists and colons.
        // Only explicit structured documents are automatically treated as YAML.
        var hasDocumentHeader = lines.Any(static line =>
            line.StartsWith("version:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("dsl:", StringComparison.OrdinalIgnoreCase));
        var hasWorkflowRoot = lines.Any(static line =>
            line.Equals("workflows:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("entrypoint:", StringComparison.OrdinalIgnoreCase));
        return hasDocumentHeader || hasWorkflowRoot;
    }

    private static bool TryExtractSingleCodeFence(string markdown, out string language, out string content)
    {
        language = string.Empty;
        content = string.Empty;

        var lines = markdown.Split('\n');
        if (lines.Length < 2 || !lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
            return false;

        var firstLine = lines[0].Trim();
        if (!lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
            return false;

        language = firstLine.Length > 3 ? firstLine[3..].Trim() : string.Empty;
        content = string.Join('\n', lines[1..^1]);
        return true;
    }

    private static bool IsJsonLanguage(string language)
        => language.Equals("json", StringComparison.OrdinalIgnoreCase)
           || language.Equals("jsonc", StringComparison.OrdinalIgnoreCase);

    private static bool IsYamlLanguage(string language)
        => language.Equals("yaml", StringComparison.OrdinalIgnoreCase)
           || language.Equals("yml", StringComparison.OrdinalIgnoreCase);

    private static string BuildFencedCode(string language, string content)
    {
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim().ToLowerInvariant();
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{normalizedLanguage}\n{content.Trim()}\n{fence}";
    }

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
