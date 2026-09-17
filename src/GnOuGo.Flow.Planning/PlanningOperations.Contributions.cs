using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // Derived domains only. The existing pages hold semantic answers; admission carries their proofs.
    internal static string ContributionDecisionId(PlanningRuntimeEvidence evidence) => "contribution_" + evidence.Id;
    private static string ContributionDomainFingerprint(PlanningSnapshot state, Scope scope, JsonObject schema, JsonObject context) =>
        PlanningGraphCompiler.Fingerprint("execution-contribution-v3:" + EvidenceFingerprint(state) + ":" +
            scope.Evidence!.ProofFingerprint + ":" + schema.ToJsonString() + ":" + context.ToJsonString());

    internal static PlanningDecisionPages.Decision ContributionDecision(PlanningSnapshot state, Scope scope)
    {
        var evidence = scope.Evidence!;
        RequireEligibleContribution(state, evidence);
        if (evidence.BaselineReference is not null) throw new InvalidOperationException("Exact baseline execution is engine qualified.");
        var reference = state.References.Single(r => r.Id == evidence.ActionReference);
        var spans = ContributionBoundaries(state, scope);
        var owned = new JsonObject { ["anyOf"] = new JsonArray(PlanningHoleRequests.Enum([reference.Id]), spans.Schema) };
        var domain = EffectDomain(state, evidence);
        var alternatives = new JsonArray(PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["excluded"])),
            ("basis", PlanningHoleRequests.Enum(["no_operation_relevance"])), ("evidence", owned.DeepClone().AsObject())),
            PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["governing_property"])),
                ("evidence", owned.DeepClone().AsObject())));
        foreach (var (id, anchor) in domain)
        {
            // Exact occurrence ownership is necessary for an occurrence support basis.
            // Several owned execution spans may support that same proven occurrence.
            if (anchor.BoundaryKind != "result_realization" && anchor.OccurrenceProof is null) continue;
            alternatives.Add((JsonNode)PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["supports"])),
                ("effect", PlanningHoleRequests.Enum([id])), ("evidence", owned.DeepClone().AsObject()),
                ("basis", PlanningHoleRequests.Enum([anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence"])),
                ("owner", PlanningHoleRequests.Enum([anchor.OwnerReference])),
                ("boundary", PlanningHoleRequests.Enum([anchor.BoundaryReference]))));
        }
        var schema = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["qualified"])), ("contributions", new JsonObject
            { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = new JsonObject { ["anyOf"] = alternatives } }))) };
        var context = new JsonObject
        {
            ["stage"] = "execution_contribution",
            ["task"] = "Qualify all owned evidence before realization coverage. Support requires requested performance/result production, or requested execution of the exact owned occurrence. Qualify a property of execution as governing_property without an effect target. An empty support domain does not establish irrelevance; preliminary execution kind does not restrict governing qualification. Applicability is established separately after realizations exist. Kind, necessity, generated-workflow scope, public declarations and candidate boundaries do not prove requested performance. Semantic labels are nonexclusive context, neither authority nor a veto. Select owned subspans to retain execution and properties separately within a mixed clause; account for the complete evidence. An exclusion must establish neither execution nor execution-governing relevance at this boundary, never optional omission. Preserve other semantic obligations. Return unresolved if qualification, effect ownership or complete coverage cannot be proven within this bounded response.",
            ["reference"] = reference.Id, ["span"] = PlanningChoiceEvidence.Text(state, reference.Id),
            ["clause"] = PlanningChoiceEvidence.Text(state, scope.Clause.Id), ["boundaries"] = spans.Context.DeepClone(),
            ["kind"] = evidence.Kind, ["necessity"] = evidence.Necessity.ToString(),
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference,
                ["boundary"] = p.Value.BoundaryKind, ["boundaryReference"] = p.Value.BoundaryReference,
                ["ownershipProof"] = p.Value.OccurrenceProof?.Fingerprint,
                ["result"] = state.Declarations.SingleOrDefault(d => d.Id == p.Value.OwnerReference) is { } declaration
                    ? PlanningDeclarations.Name(state, declaration) : null })))
        };
        return new(ContributionDecisionId(evidence), schema, context, ContributionDomainFingerprint(state, scope, schema, context),
            SourceDecisionIds: [ContributionDecisionId(evidence)]);
    }

    private static (JsonObject Context, JsonObject Schema, Func<string, string, PlanningReference> Select) ContributionBoundaries(PlanningSnapshot state, Scope scope)
    {
        var parent = state.References.Single(r => r.Id == scope.Evidence!.ActionReference);
        var starts = scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        var ends = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        var from = starts.Where(b => scope.Select(b, ends[^1]).Start is var start && start >= parent.Start && start < parent.Start + parent.Length).ToArray();
        var to = ends.Where(b => scope.Select(starts[0], b) is var span && span.Start + span.Length > parent.Start && span.Start + span.Length <= parent.Start + parent.Length).ToArray();
        PlanningReference Select(string start, string end)
        {
            var span = scope.Select(start, end);
            if (!from.Contains(start) || !to.Contains(end) || span.Start < parent.Start || span.Start + span.Length > parent.Start + parent.Length)
                throw Failure(scope.Evidence!.Id, "Contribution evidence is outside its exact runtime parent.");
            return span;
        }
        return (scope.Words, PlanningHoleRequests.Object(("start", PlanningHoleRequests.Enum(from)), ("end", PlanningHoleRequests.Enum(to))), Select);
    }

    internal static PlanningExecutionContributionProof ParseContributions(PlanningSnapshot state, Scope scope, JsonObject answer)
    {
        var decision = ContributionDecision(state, scope);
        if (PlanningContractValidation.ValidateInstance(answer, decision.Schema).Count != 0)
            throw Failure(scope.Evidence!.Id, "Contribution qualification changed owned evidence, effect bindings or support bases.");
        if (answer["status"]!.ToString() != "qualified") throw Failure(scope.Evidence!.Id, "Executable contribution authority is unresolved.");
        var parent = state.References.Single(r => r.Id == scope.Evidence!.ActionReference);
        var boundaries = ContributionBoundaries(state, scope);
        var selected = new List<PlanningReference>();
        var contributions = new List<PlanningExecutionContribution>();
        foreach (var value in answer["contributions"]!.AsArray())
        {
            var span = value!["evidence"] is JsonObject range
                ? boundaries.Select(range["start"]!.ToString(), range["end"]!.ToString()) : parent;
            // Full-span selections retain the issued reference, independent of answer notation.
            if (span.Start == parent.Start && span.Length == parent.Length) span = parent;
            RequireEligibleContribution(state, scope.Evidence!, span);
            selected.Add(span);
            var item = new PlanningExecutionContribution("", span.Id, value["role"]!.ToString(), value["effect"]?.ToString(),
                value["basis"]?.ToString() ?? "governing_property", value["owner"]?.ToString(), value["boundary"]?.ToString(),
                PlanningContributionOrigin.ModelQualification);
            contributions.Add(SealContribution(scope.Evidence!.Id, item));
        }
        // No dropping unqualified suffixes or automatic source trimming. Whitespace between
        // issued word spans has no independent semantic content; every other character is owned.
        for (var offset = parent.Start; offset < parent.Start + parent.Length; offset++)
            if (!char.IsWhiteSpace(scope.Source.Text[offset]) && !selected.Any(r => r.Start <= offset && r.Start + r.Length > offset))
                throw Failure(scope.Evidence!.Id, "Contribution qualification did not cover its complete owned evidence.");
        foreach (var support in contributions.Where(c => c.Role != "excluded"))
        foreach (var exclusion in contributions.Where(c => c.Role == "excluded"))
        {
            var action = selected.First(r => r.Id == support.EvidenceReference);
            var absent = selected.First(r => r.Id == exclusion.EvidenceReference);
            if (action.Start < absent.Start + absent.Length && absent.Start < action.Start + action.Length)
                throw Failure(scope.Evidence!.Id, "Requested execution and its exclusion have contradictory owned evidence.");
        }
        foreach (var span in selected)
            if (!state.References.Any(r => r.Id == span.Id)) state.References.Add(span);
        return SealContributionProof(new(3, scope.Evidence!.Id, decision.Id, decision.EvidenceFingerprint,
            contributions.Distinct().OrderBy(c => c.Id, StringComparer.Ordinal).ToList(), ""));
    }

    private static PlanningExecutionContribution SealContribution(string parent, PlanningExecutionContribution value) => value with
    { Id = "contribution_" + PlanningGraphCompiler.Fingerprint(parent + ":" + JsonSerializer.Serialize(value with { Id = "" },
        PlanningJsonContext.Default.PlanningExecutionContribution))[..24] };
    private static PlanningExecutionContributionProof SealContributionProof(PlanningExecutionContributionProof proof) => proof with
    { ProofFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(proof with { ProofFingerprint = "" },
        PlanningJsonContext.Default.PlanningExecutionContributionProof)) };

    private static PlanningExecutionContributionProof DeterministicContribution(PlanningSnapshot state, PlanningRuntimeEvidence evidence, bool excluded)
    {
        KeyValuePair<string, PlanningOperationEffectAnchor>? anchor = excluded ? null : EffectDomain(state, evidence).Single();
        var structural = !excluded && PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == state.References.Single(r => r.Id == evidence.SourceReference).SourceId).Structural;
        var exclusion = excluded ? ContractExclusions(state).GetValueOrDefault(evidence.Id) : null;
        var item = SealContribution(evidence.Id, new("", evidence.ActionReference ?? evidence.SourceReference, excluded ? "excluded" : structural ? "supports" : "governing_property",
            structural ? anchor?.Key : null, excluded ? exclusion is { SeparateEvidence.Length: > 0 } ? "canonical_contract_separation" : "canonical_source_or_declaration_exclusion" : structural ? "existing_baseline_execution" : "baseline_owner_annotation",
            anchor?.Value.OwnerReference, anchor?.Value.BoundaryReference,
            excluded ? PlanningContributionOrigin.DeterministicExclusion : PlanningContributionOrigin.DeterministicBaseline));
        return SealContributionProof(new(3, evidence.Id, null, PlanningGraphCompiler.Fingerprint("execution-contribution-v3:" +
            EvidenceFingerprint(state) + ":" + evidence.ProofFingerprint + ":" + state.DeclarationFingerprint), [item], ""));
    }

    internal static PlanningDecisionPages.Decision[] ContributionDecisions(PlanningSnapshot state) => DeriveScopes(state)
        .Where(s => s.Evidence!.BaselineReference is null).Select(s => ContributionDecision(state, s))
        .OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    internal static PlanningExecutionContributionProof[] ReadContributions(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).ToDictionary(s => s.Evidence!.Id, StringComparer.Ordinal);
        var decisions = ContributionDecisions(state);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions); }
        catch (PlanningConflictException) { throw Failure("$plan", "Current canonical contribution qualification is missing.", "INTENT_OPERATION_PROOF_MISSING"); }
        return state.RuntimeEvidence.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e =>
            !scopes.TryGetValue(e.Id, out var scope) ? DeterministicContribution(state, e, true) : e.BaselineReference is not null
                ? DeterministicContribution(state, e, false) : ParseContributions(state, scope, values[ContributionDecisionId(e)]!.AsObject())).ToArray();
    }

    private static async Task GroundContributions(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", ContributionDecisions(state), ct);
        _ = ReadContributions(state);
    }

    internal static Scope[] QualifiedScopes(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).ToDictionary(s => s.Evidence!.Id, StringComparer.Ordinal);
        return ReadContributions(state).SelectMany(p => p.Contributions.Where(c => c.Role != "excluded")
            .Select(c => scopes[p.RuntimeEvidenceId] with { Contribution = c }))
            .OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).ToArray();
    }
}
