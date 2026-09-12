using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Indexes exact source spans. Referencing evidence never asks the model to transcribe it.</summary>
internal static class PlanningReferences
{
    internal static List<PlanningReference> Register(PlanningSnapshot state, string sourceId, string kind, string text)
    {
        var owner = state.Request.TenantId + ":" + state.Request.SessionId;
        var fingerprint = PlanningGraphCompiler.Fingerprint(text);
        var retained = state.References.Where(r => r.Owner == owner && r.SourceId == sourceId && r.Kind == kind && r.SourceFingerprint == fingerprint).ToList();
        if (retained.Count > 0) return retained;
        var references = Issue(owner, state.Revision, sourceId, kind, text);
        state.References.AddRange(references);
        return references;
    }
    internal static List<PlanningReference> Issue(string owner, long revision, string sourceId, string kind, string text)
    {
        var fingerprint = PlanningGraphCompiler.Fingerprint(text);
        var result = new List<PlanningReference>();
        for (var start = 0; start < text.Length;)
        {
            var end = start;
            while (end < text.Length && end - start < 256)
            {
                var ch = text[end++];
                if (char.IsHighSurrogate(ch) && end < text.Length && char.IsLowSurrogate(text[end])) end++;
                if (ch is '\n' or '.' or '!' or '?' or ';' or '。' or '！' or '？') break;
            }
            var coordinate = owner + ":" + revision.ToString(CultureInfo.InvariantCulture) + ":" + sourceId + ":" + fingerprint + ":" + start + ":" + end;
            result.Add(new("r_" + PlanningGraphCompiler.Fingerprint(coordinate)[..24], owner, revision, sourceId, fingerprint, kind, start, end - start));
            start = end;
        }
        return result;
    }

    internal static string Resolve(PlanningReference reference, string owner, long revision, string sourceId, string text)
    {
        if (reference.Owner != owner || reference.SourceRevision != revision || reference.SourceId != sourceId ||
            reference.SourceFingerprint != PlanningGraphCompiler.Fingerprint(text) || reference.Start < 0 || reference.Length < 1 ||
            reference.Start > text.Length - reference.Length ||
            !(reference.Kind.EndsWith(":clause", StringComparison.Ordinal)
                ? ContainingRange(text, reference.Start) == (reference.Start, reference.Length)
                : Issue(owner, revision, sourceId, reference.Kind, text).Contains(reference)))
            throw new PlanningConflictException("The evidence reference is foreign, stale or outside its issued scope.");
        return text.Substring(reference.Start, reference.Length);
    }

    internal static PlanningReference ContainingClause(PlanningSnapshot state, PlanningReference reference, string text)
    {
        _ = Resolve(state, reference.Id, new Dictionary<string, string> { [reference.SourceId] = text });
        var (start, length) = ContainingRange(text, reference.Start);
        if (reference.Start + reference.Length > start + length)
            throw new PlanningConflictException("The selected subject crosses complete source clauses.");
        var kind = reference.Kind.Split(':')[0] + ":clause";
        var id = "r_" + PlanningGraphCompiler.Fingerprint(reference.Owner + ":" + reference.SourceId + ":" + reference.SourceFingerprint + ":clause:" + start + ":" + length)[..24];
        var clause = reference with { Id = id, Kind = kind, Start = start, Length = length };
        if (!state.References.Any(r => r.Id == id)) state.References.Add(clause);
        return state.References.Single(r => r.Id == id);
    }

    private static (int Start, int Length) ContainingRange(string text, int anchor)
    {
        bool End(int index) => text[index] == '\n' || text[index] is '.' or '!' or '?' or ';' or '。' or '！' or '？' &&
            (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]));
        var start = anchor;
        while (start > 0 && !End(start - 1)) start--;
        var end = anchor;
        while (end < text.Length) { if (End(end++)) break; }
        return (start, end - start);
    }

    internal static string Resolve(PlanningSnapshot state, string id, IReadOnlyDictionary<string, string> sources)
    {
        var reference = state.References.SingleOrDefault(r => r.Id == id);
        if (reference is null || reference.Owner != state.Request.TenantId + ":" + state.Request.SessionId ||
            !sources.TryGetValue(reference.SourceId, out var text) || reference.SourceFingerprint != PlanningGraphCompiler.Fingerprint(text) ||
            reference.Start < 0 || reference.Length < 1 || reference.Start > text.Length - reference.Length)
            throw new PlanningConflictException("The selected reference is not part of the current owned evidence.");
        return text.Substring(reference.Start, reference.Length);
    }

    internal static (JsonObject Context, JsonObject Schema, Func<string, string, PlanningReference> Select) Boundaries(PlanningReference reference, string text)
    {
        var span = Resolve(reference, reference.Owner, reference.SourceRevision, reference.SourceId, text);
        var words = Regex.Matches(span, @"\S+").Cast<Match>().ToArray();
        var starts = words.Select((m, i) => (Id: "b" + i.ToString(CultureInfo.InvariantCulture), Position: m.Index)).ToDictionary(p => p.Id, p => p.Position, StringComparer.Ordinal);
        var ends = words.Select((m, i) => (Id: "b" + (i + 1).ToString(CultureInfo.InvariantCulture), Position: m.Index + m.Length)).ToDictionary(p => p.Id, p => p.Position, StringComparer.Ordinal);
        var context = new JsonObject(words.Select((m, i) => new KeyValuePair<string, JsonNode?>("b" + i.ToString(CultureInfo.InvariantCulture), JsonValue.Create(m.Value))));
        var schema = PlanningHoleRequests.Object(("start", PlanningHoleRequests.Enum(starts.Keys.ToArray())), ("end", PlanningHoleRequests.Enum(ends.Keys.ToArray())));
        return (context, schema, (start, end) =>
        {
            if (!starts.TryGetValue(start, out var from) || !ends.TryGetValue(end, out var to) || to <= from)
                throw new PlanningConflictException("The selected evidence boundaries are unknown or reversed.");
            return reference with { Id = "r_" + PlanningGraphCompiler.Fingerprint(reference.Id + ":" + start + ":" + end)[..24],
                Kind = reference.Kind + ":selection", Start = reference.Start + from, Length = to - from };
        });
    }

    internal static JsonObject Schema(IEnumerable<PlanningReference> references) => PlanningHoleRequests.Enum(references.Select(r => r.Id).ToArray());

    internal static JsonObject Context(IEnumerable<PlanningReference> references, IReadOnlyDictionary<string, string> sources) =>
        new(references.Select(r => new KeyValuePair<string, JsonNode?>(r.Id,
            JsonValue.Create(Resolve(r, r.Owner, r.SourceRevision, r.SourceId, sources[r.SourceId])))));
}
