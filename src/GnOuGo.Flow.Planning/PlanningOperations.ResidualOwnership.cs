using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    internal sealed record ResidualPortion(string Key, PlanningReference Reference, string Ownership, string[] Kinds);

    // Coordinates establish an obligation to answer, never the answer's meaning.
    internal static ResidualPortion[] ResidualPortions(PlanningSnapshot state, Scope scope, PlanningExecutionRequestProof[]? requests = null)
    {
        var members = ContributionMembers(state, scope);
        var obligations = ContributionObligations(state, scope);
        var fixedCoverage = ContractCoverage(state).Select(c => c.Span).Concat(RequestUnits(state, scope, requests)
            .SelectMany(u => u.EvidenceReferences).Select(id => state.References.Single(r => r.Id == id)))
            .Where(r => SameSource(scope.Clause, r)).ToArray();
        var runtime = members.Select(m => (Id: m.Evidence!.Id, Span: state.References.Single(r => r.Id == m.Evidence.ActionReference))).ToArray();
        var semantic = obligations.SelectMany(o => o.EvidenceReferences.Select(id =>
            (Obligation: o, Span: state.References.Single(r => r.Id == id)))).Where(v => SameSource(scope.Clause, v.Span)).ToArray();
        var edges = fixedCoverage.Concat(runtime.Select(v => v.Span)).Concat(semantic.Select(v => v.Span))
            .SelectMany(r => new[] { r.Start, r.Start + r.Length }).Distinct().ToArray();
        var result = new List<ResidualPortion>();
        for (var i = 0; i < scope.Words.Count; i++)
        {
            var word = scope.Select("b" + i.ToString(CultureInfo.InvariantCulture), "b" + (i + 1).ToString(CultureInfo.InvariantCulture));
            var points = edges.Where(p => p > word.Start && p < word.Start + word.Length)
                .Append(word.Start).Append(word.Start + word.Length).Distinct().Order().ToArray();
            for (var n = 1; n < points.Length; n++)
            {
                var reference = GoverningRange(scope, points[n - 1], points[n]);
                if (fixedCoverage.Any(r => ContainsSpan(r, reference))) continue;
                var kinds = AllowedGoverningKinds(state, scope, reference, obligations);
                var ownership = string.Join('|', runtime.Where(v => ContainsSpan(v.Span, reference)).Select(v => "runtime:" + v.Id)
                    .Concat(semantic.Where(v => ContainsSpan(v.Span, reference)).Select(v => "semantic:" + v.Obligation.Id + ":" + v.Obligation.Grounding!.Fingerprint))
                    .Order(StringComparer.Ordinal));
                result.Add(new("p" + result.Count.ToString(CultureInfo.InvariantCulture), reference, ownership,
                    kinds.Length == GoverningKinds.Length ? kinds.Append("excluded").ToArray() : kinds));
            }
        }
        return result.ToArray();
    }

    private static string[] AllowedGoverningKinds(PlanningSnapshot state, Scope scope, PlanningReference reference, PlanningObligation[]? currentObligations = null)
    {
        var established = (currentObligations ?? ContributionObligations(state, scope)).Where(o => o.Kind is "runtime_condition" or "runtime_fallback" &&
            o.EvidenceReferences.Any(id => Overlaps(state.References.Single(r => r.Id == id), reference)))
            .Select(o => o.Kind).Distinct(StringComparer.Ordinal).ToArray();
        return established.Length == 0 ? GoverningKinds : established.Length == 1 ? established : [];
    }

    private static PlanningReference GoverningRange(Scope scope, int start, int end)
    {
        // Reuse issued lexical notation when possible; exact ownership edges may
        // also split a word. Neither case extends an execution reference.
        for (var i = 0; i < scope.Words.Count; i++)
            if (scope.Select("b" + i.ToString(CultureInfo.InvariantCulture), "b" + (i + 1).ToString(CultureInfo.InvariantCulture)).Start == start)
                for (var j = i + 1; j <= scope.Words.Count; j++)
                {
                    var candidate = scope.Select("b" + i.ToString(CultureInfo.InvariantCulture), "b" + j.ToString(CultureInfo.InvariantCulture));
                    if (candidate.Start + candidate.Length == end) return candidate;
                }
        return scope.Clause with { Id = "r_" + PlanningGraphCompiler.Fingerprint(scope.Clause.Id + ":governing-range-v1:" +
            start.ToString(CultureInfo.InvariantCulture) + ":" + end.ToString(CultureInfo.InvariantCulture))[..24],
            Kind = scope.Clause.Kind + ":selection", Start = start, Length = end - start };
    }

    private static JsonObject ResidualSchema(ResidualPortion[] portions)
    {
        if (portions.Any(p => p.Kinds.Length == 0)) throw Failure(portions.First(p => p.Kinds.Length == 0).Reference.Id,
            "Residual evidence has conflicting established semantic kinds.");
        var groups = Enumerable.Range(0, MaxContributionUnits).Select(i => "g" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        return PlanningHoleRequests.Object(portions.Select(p => (p.Key, PlanningHoleRequests.Object(
            ("kind", PlanningHoleRequests.Enum(p.Kinds)), ("group", PlanningHoleRequests.Enum(groups))))).ToArray());
    }

    private static JsonObject ResidualContext(Scope scope, ResidualPortion[] portions) => new(portions.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
        new JsonObject { ["start"] = p.Reference.Start, ["length"] = p.Reference.Length, ["text"] = scope.Source.Text.Substring(p.Reference.Start, p.Reference.Length), ["ownership"] = p.Ownership })));

    private static Dictionary<string, PlanningReference> ProjectResidual(PlanningSnapshot state, Scope scope,
        PlanningExecutionRequestProof[] requests, JsonObject answer, JsonArray units)
    {
        var portions = ResidualPortions(state, scope, requests);
        var derived = new Dictionary<string, PlanningReference>(StringComparer.Ordinal);
        var selected = answer["residual"]!.AsObject();
        var index = 0;
        while (index < portions.Length)
        {
            var first = portions[index]; var last = first;
            var kind = selected[first.Key]!["kind"]!.ToString(); var group = selected[first.Key]!["group"]!.ToString();
            index++;
            while (index < portions.Length && portions[index] is var next && next.Ownership == first.Ownership &&
                selected[next.Key]!["kind"]!.ToString() == kind && selected[next.Key]!["group"]!.ToString() == group &&
                Enumerable.Range(last.Reference.Start + last.Reference.Length, next.Reference.Start - last.Reference.Start - last.Reference.Length)
                    .All(p => char.IsWhiteSpace(scope.Source.Text[p])))
            { last = next; index++; }
            var span = GoverningRange(scope, first.Reference.Start, last.Reference.Start + last.Reference.Length);
            derived[span.Id] = span;
            var unit = new JsonObject { ["role"] = kind == "excluded" ? "excluded" : "governing_property",
                ["scope"] = scope.Clause.Id, ["evidence"] = span.Id };
            if (kind == "excluded") unit["basis"] = "no_operation_relevance"; else unit["governingKind"] = kind;
            units.Add((JsonNode)unit);
        }
        return derived;
    }
}
