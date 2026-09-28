using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Capabilities;

// A transient metadata index. Scores order discovery; they never establish a contract or binding.
internal static class CapabilityRelevance
{
    internal static string Query(string text) => string.Join(' ', Tokens(text).Order(StringComparer.Ordinal));

    internal static IReadOnlyList<CapabilitySummary> Rank(IEnumerable<CapabilitySummary> capabilities, string query) => Rank(capabilities, _ => query);

    internal static IReadOnlyList<CapabilitySummary> Rank(IEnumerable<CapabilitySummary> capabilities, Func<CapabilitySummary, string> query)
    {
        var entries = capabilities.Select(c => (Capability: c, Terms: Tokens(query(c)), Name: Tokens(c.Name), Description: Tokens(c.Description),
            Fields: Tokens(string.Join(' ', (c.Operation?.Inputs ?? []).Concat(c.Operation?.Outputs ?? [])
                .SelectMany(p => new[] { p.Name }.Concat(FieldNames(p.Schema))))))).ToArray();
        var frequency = entries.SelectMany(e => e.Name.Union(e.Fields).Union(e.Description)).GroupBy(t => t, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        double Score(HashSet<string> words, HashSet<string> terms) => words.Intersect(terms).Order(StringComparer.Ordinal)
            .Sum(t => Math.Log(1d + entries.Length / (double)frequency[t]));
        return entries.OrderByDescending(e => 3 * Score(e.Name, e.Terms) + 2 * Score(e.Fields, e.Terms) + Score(e.Description, e.Terms))
            .ThenBy(e => e.Capability.Id, StringComparer.Ordinal).Select(e => e.Capability).ToArray();
    }

    private static IEnumerable<string> FieldNames(JsonNode? schema)
    {
        if (schema is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
                foreach (var property in properties) yield return property.Key;
            foreach (var child in obj.Select(p => p.Value))
                foreach (var name in FieldNames(child)) yield return name;
        }
        else if (schema is JsonArray array)
            foreach (var child in array)
                foreach (var name in FieldNames(child)) yield return name;
    }

    private static HashSet<string> Tokens(string text)
    {
        var separated = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1]) ||
                char.IsUpper(text[i - 1]) && i + 1 < text.Length && char.IsLower(text[i + 1]))) separated.Append(' ');
            separated.Append(c);
        }
        var normalized = new StringBuilder();
        foreach (var c in separated.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            normalized.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
}
