using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    private static readonly string[] GoverningKinds = ["runtime_rule", "runtime_condition", "runtime_fallback", "descriptive_property"];

    internal static string ContributionDecisionId(Scope scope) => "contribution_clause_" +
        PlanningGraphCompiler.Fingerprint(scope.Clause.Id)[..24];

    internal static Scope[] ContributionScopes(PlanningSnapshot state)
    {
        var runtime = DeriveScopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var semantic = state.Obligations.Where(o => o.OperationAdmission is null && o.Kind is "runtime_condition" or "runtime_fallback")
            .Select(o => o.Grounding?.ClauseReference).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var coverage = ContractCoverage(state);
        return runtime.Concat(SourceScopes(state).Where(s => semantic.Contains(s.Clause.Id) &&
                !Covered(state, s.Clause, coverage.Select(c => c.Span))))
            .GroupBy(s => s.Clause.Id, StringComparer.Ordinal)
            .Select(g => g.OrderBy(s => s.Evidence is null ? 1 : 0).ThenBy(s => s.Evidence?.Id, StringComparer.Ordinal).First())
            .OrderBy(s => s.Clause.Id, StringComparer.Ordinal).ToArray();
    }

    private static PlanningObligation[] ContributionObligations(PlanningSnapshot state, Scope scope)
    {
        var values = state.Obligations.Where(o => o.OperationAdmission is null && o.Grounding?.ClauseReference == scope.Clause.Id)
            .OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        foreach (var value in values) PlanningSourceGroundingRules.Validate(state, value);
        return values;
    }

    // The clause issues coordinates for governing qualification only. It never
    // expands the independent, runtime-owned support domain.
    private static JsonObject ContributionSourceSchema(PlanningSnapshot state, Scope scope, Scope[] members)
    {
        var contracts = ContractCoverage(state);
        var starts = scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray().Select(n => n!.ToString()).ToArray();
        var ends = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Select(n => n!.ToString()).ToArray();
        var choices = new JsonArray();
        var startRun = new List<string>(); var endRun = new List<string>();
        void Flush()
        {
            if (startRun.Count != 0) choices.Add((JsonNode)PlanningHoleRequests.Object(
                ("start", PlanningHoleRequests.Enum(startRun.ToArray())), ("end", PlanningHoleRequests.Enum(endRun.ToArray()))));
            startRun.Clear(); endRun.Clear();
        }
        for (var i = 0; i < starts.Length; i++)
        {
            var word = scope.Select(starts[i], ends[i]);
            if (contracts.Any(c => Overlaps(c.Span, word))) { Flush(); continue; }
            startRun.Add(starts[i]); endRun.Add(ends[i]);
        }
        Flush();
        var references = members.Select(s => s.Evidence!.ActionReference!).Concat(ContributionObligations(state, scope).SelectMany(o => o.EvidenceReferences))
            .Append(scope.Clause.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => state.References.Single(r => r.Id == id) is { } span && ContainsSpan(scope.Clause, span) &&
                !contracts.Any(c => Overlaps(c.Span, span))).ToArray();
        if (references.Length != 0) choices.Add((JsonNode)PlanningHoleRequests.Enum(references));
        return new() { ["anyOf"] = choices };
    }

    private static List<PlanningContributionSourceBinding> SourceBindings(PlanningSnapshot state, Scope scope, IEnumerable<PlanningReference> spans,
        PlanningObligation[]? currentObligations = null)
    {
        var obligations = currentObligations ?? ContributionObligations(state, scope);
        return spans.SelectMany(span =>
        {
            var owners = obligations.Where(o => o.EvidenceReferences.Any(id => ContainsSpan(state.References.Single(r => r.Id == id), span))).ToArray();
            return owners.Length == 0 ? new[] { new PlanningContributionSourceBinding(span.Id, scope.Clause.Id, null, null) } :
                owners.Select(o => new PlanningContributionSourceBinding(span.Id, scope.Clause.Id, o.Id, o.Grounding!.Fingerprint));
        }).Distinct().OrderBy(b => b.EvidenceReference, StringComparer.Ordinal).ThenBy(b => b.SemanticObligationId, StringComparer.Ordinal).ToList();
    }

    private static string GoverningKind(PlanningSnapshot state, PlanningObligation[] obligations, PlanningReference span, string selected)
    {
        var kinds = obligations.Where(o => o.Kind is "runtime_condition" or "runtime_fallback" &&
                o.EvidenceReferences.Any(id => Overlaps(state.References.Single(r => r.Id == id), span)))
            .Select(o => o.Kind).Distinct(StringComparer.Ordinal).ToArray();
        if (!GoverningKinds.Contains(selected, StringComparer.Ordinal) || kinds.Any(kind => kind != selected))
            throw Failure(span.Id, "Governing qualification changed its current owned semantic kind.");
        return selected;
    }

    private static void ValidateSourceBindings(PlanningSnapshot state, PlanningExecutionContribution contribution, string clause)
    {
        var source = SourceScopes(state).SingleOrDefault(s => s.Clause.Id == clause)
            ?? throw Failure(clause, "Governing source ownership is missing.");
        var reference = state.References.SingleOrDefault(r => r.Id == contribution.EvidenceReference);
        if (reference is null || !PlanningChoiceEvidence.Current(state, reference.Id) || !ContainsSpan(source.Clause, reference) ||
            JsonSerializer.Serialize(contribution.SourceBindings, PlanningJsonContext.Default.ListPlanningContributionSourceBinding) !=
            JsonSerializer.Serialize(SourceBindings(state, source, [reference]), PlanningJsonContext.Default.ListPlanningContributionSourceBinding))
            throw Failure(clause, "Contribution source bindings are foreign, stale or incomplete.");
    }
}
