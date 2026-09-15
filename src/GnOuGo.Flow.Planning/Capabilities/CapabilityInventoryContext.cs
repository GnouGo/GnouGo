using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageContext;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityInventoryContext
{

    internal static string MatchingCatalog(CapabilityCatalog catalog)
    {
        // Lossless factoring of repeated transport text. IDs and candidate boundaries are unchanged.
        var best = catalog.Text;
        Dictionary<string, string[]> sharedFragments = new(StringComparer.Ordinal);
        foreach (var widths in new[] { new[] { 12, 24, 48 }, new[] { 1, 2, 4, 8, 12, 16, 24, 32, 48 } })
        {
            var fragments = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in catalog.Entries)
            {
                // Compact contract members have punctuation boundaries without
                // whitespace. Factoring remains lossless, including quoted commas.
                var words = Regex.Matches(entry.Card, @"[^\s,;]+[,;\s]*").Cast<Match>().ToArray();
                foreach (var width in widths)
                    for (var i = 0; i + width <= words.Length; i++)
                    {
                        var end = words[i + width - 1].Index + words[i + width - 1].Length;
                        var fragment = entry.Card[words[i].Index..end];
                        if (fragment.Length >= 32) fragments[fragment] = fragments.GetValueOrDefault(fragment) + 1;
                    }
            }
            sharedFragments = fragments.Where(p => p.Value > 1).Select(p => p.Key).GroupBy(p => p[..32], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            foreach (var separator in new[] { "", ", ", ". ", "; ", "\n" })
            // Prefix sharing is an encoding choice, not a capability boundary.
            // Distinct method IDs and their full contracts survive either grouping.
            foreach (var separateMethods in new[] { true, false })
            {
                var compact = Pack(separator, separateMethods);
                if (PlanningJsonTransport.EstimateInputTokens(compact, new()) < PlanningJsonTransport.EstimateInputTokens(best, new())) best = compact;
            }
        }
        return best;

        string Pack(string separator, bool separateMethods)
        {
            var groups = new JsonArray();
            foreach (var group in catalog.Entries.GroupBy(e => (e.Resolution, e.Server, e.Kind, Method: separateMethods ? e.Method : "")))
            {
                var entries = group.ToArray(); var prefix = entries[0].Card;
                foreach (var entry in entries.Skip(1))
                {
                    var length = 0;
                    while (length < prefix.Length && length < entry.Card.Length && prefix[length] == entry.Card[length]) length++;
                    if (length > 0 && char.IsHighSurrogate(prefix[length - 1])) length--;
                    prefix = prefix[..length];
                }
                if (entries.Length == 1) prefix = "";
                var suffix = entries.Length == 1 ? "" : entries[0].Card[prefix.Length..];
                foreach (var entry in entries.Skip(1))
                {
                    var length = 0;
                    while (length < suffix.Length && length < entry.Card.Length - prefix.Length && suffix[^(length + 1)] == entry.Card[^(length + 1)]) length++;
                    if (length > 0 && char.IsLowSurrogate(suffix[^length])) length--;
                    suffix = length == 0 ? "" : suffix[^length..];
                }
                groups.Add((JsonNode)new JsonObject
                {
                    ["prefix"] = prefix,
                    ["suffix"] = suffix,
                    ["entries"] = new JsonObject(entries.Select(e => new KeyValuePair<string, JsonNode?>(e.Id,
                        new JsonArray(Parts(e.Card.Substring(prefix.Length, e.Card.Length - prefix.Length - suffix.Length), separator).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()))))
                });
            }
            return "{$contextRef:id} expands to that exact shared value; JSON Schema references and constraints are unchanged. Entry contract = group.prefix + join(entry parts, separator) + group.suffix.\n" +
                PlanningPromptContext.Json(PlanningPromptContext.Share(new JsonObject { ["separator"] = separator, ["groups"] = groups }));
        }

        IEnumerable<string> Parts(string value, string separator)
        {
            if (separator.Length > 0) return value.Split(separator, StringSplitOptions.None);
            var parts = new List<string>(); var start = 0;
            for (var i = 0; i + 32 <= value.Length; i++)
            {
                if (!sharedFragments.TryGetValue(value.Substring(i, 32), out var candidates)) continue;
                var fragment = candidates.FirstOrDefault(p => value.AsSpan(i).StartsWith(p.AsSpan(), StringComparison.Ordinal));
                if (fragment is null) continue;
                if (i > start) parts.Add(value[start..i]);
                parts.Add(fragment); i += fragment.Length - 1; start = i + 1;
            }
            if (start < value.Length) parts.Add(value[start..]);
            return parts;
        }
    }
}
