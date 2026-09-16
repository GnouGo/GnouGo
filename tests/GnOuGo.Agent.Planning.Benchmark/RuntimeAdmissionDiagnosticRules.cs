using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class RuntimeAdmissionDiagnosticRules
{
    internal const string Identity = "schema5-governing-applicability-diagnostics-1";
    internal const string ComparisonIdentity = "schema5-execution-contributions-diagnostics-1";
    internal const string ProductionCommit = "23bce4bc842e598d19866d92d497772e1f3c0538";
    internal const string ComparisonProductionCommit = "5617c1b9024b48f09c0baf49d5e6f146df130879";
    internal const int MaxCalls = 16;
    internal static readonly string[] Cases = ["local", "mixed"];
    internal static JsonObject WithTypedPolicy(JsonObject options)
    {
        var policy = AgentPlanningPolicy.Create();
        var legacy = policy.DeepClone().AsObject(); legacy.Remove("declared_evidence");
        var supplied = options["policy"]?.DeepClone().AsObject() ?? throw new InvalidOperationException("Missing policy.");
        if (supplied["declared_evidence"] is { } metadata && !JsonNode.DeepEquals(metadata, policy["declared_evidence"]))
            throw new InvalidOperationException("Typed policy metadata changed.");
        supplied.Remove("declared_evidence");
        if (!JsonNode.DeepEquals(supplied, legacy)) throw new InvalidOperationException("The frozen host policy changed.");
        var result = options.DeepClone().AsObject(); result["policy"] = policy; return result;
    }
    internal static void RequireTypedPolicy(PlanningSnapshot state, int modelDecisions)
    {
        var declared = JsonSerializer.Deserialize(state.Request.Options["policy"]?["declared_evidence"], PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence)
            ?? throw new WorkflowRuntimeException("DIAGNOSTIC_TYPED_POLICY", "Typed host policy evidence is missing.");
        var evidence = state.RuntimeEvidence.Where(e => state.References.Any(r => r.Id == e.SourceReference && r.SourceId == "host")).ToArray();
        if (modelDecisions != 0 || evidence.Length != declared.Clauses.Count || evidence.Any(e => e.Origin != PlanningRuntimeEvidenceOrigin.EngineSourceAuthority || e.Role != "policy" || e.ActionReference is not null) ||
            state.Obligations.Count(o => o.Grounding?.DeclaredPolicyFingerprint is not null) != declared.Clauses.Sum(c => c.Meanings.Count))
            throw new WorkflowRuntimeException("DIAGNOSTIC_TYPED_POLICY", "All declared host clauses must be engine-owned constraints without model interpretation.");
    }
    internal static void RequireCase(string name, JsonObject? previous)
    {
        if (name != "local") throw new InvalidOperationException("This campaign authorizes LOCAL only; MIXED and later gates require separate authorization.");
    }
    internal static void RequireFreshStart(bool checkpoint, bool report, bool budget, bool reservations)
    {
        if (checkpoint || report || budget || reservations)
            throw new InvalidOperationException("This LOCAL has already started. Read its report; do not start or resume it again.");
    }
    internal static void RequireRequest(PlanningSnapshot state)
    {
        if (state.RequestAccounting.Any(c => c.Phase is not ("intent" or "intent_repair" or "intent_operations" or "intent_operations_repair" or "intent_relations" or "intent_relations_repair") || c.Reasoning != "low"))
            throw new InvalidOperationException("This diagnostic cannot dispatch other phases or reasoning profiles.");
        if (state.Graph is not null || state.BehaviorPlan is not null || state.ApprovedBehaviorHash is not null || state.ApprovedHash is not null)
            throw new InvalidOperationException("An admission diagnostic cannot construct or approve a workflow.");
    }

    internal static void RequirePreflight(int interpretationPages)
    {
        if (interpretationPages > MaxCalls) throw new InvalidOperationException("Packed interpretation exceeds the frozen diagnostic budget before dispatch.");
    }

    internal static void RequireStructuralBaseline(PlanningSnapshot state, IReadOnlyCollection<string> sources, int baselineModelDecisions)
    {
        var evidence = state.RuntimeEvidence.Where(e => state.References.Any(r => r.Id == e.SourceReference && r.Baseline is not null)).ToArray();
        if (baselineModelDecisions != 0 || evidence.Any(e => e.Origin != PlanningRuntimeEvidenceOrigin.EngineBaseline ||
            e.Role != "contract" || e.ExecutionScope != PlanningRuntimeExecutionScope.PublicContract || e.ActionReference is not null || e.BaselineReference is not null) ||
            !evidence.Select(e => state.References.Single(r => r.Id == e.SourceReference).SourceId).ToHashSet(StringComparer.Ordinal).SetEquals(sources))
            throw new WorkflowRuntimeException("DIAGNOSTIC_BASELINE_PROJECTION", "The port-only fixture requires engine-owned structural contracts without baseline model decisions or actions.");
    }

    internal static void RequireEffects(string name, IReadOnlyList<PlanningObligation> operations,
        IReadOnlyList<string> inputs, string output, int identityDecisions, int dependencyDecisions = 0)
    {
        void Require(bool condition, string code, string message)
        { if (!condition) throw new WorkflowRuntimeException(code, message); }
        Require(identityDecisions == 0, "DIAGNOSTIC_IDENTITY_DECISION", "The fixture requires deterministic occurrence identity after effect grounding.");
        Require(operations.Count == (name == "local" ? 1 : 2) && operations.All(o => o.Required && o.OperationAdmission is { Version: 12, Dependencies.Version: 1 }) &&
            operations.Count(o => o.Kind == "local_processing") == 1 && operations.Count(o => o.Kind == "external_read") == (name == "mixed" ? 1 : 0),
            "DIAGNOSTIC_ADMISSION_MISMATCH", "The frozen fixture requires exactly its declared runtime effects.");
        var local = operations.Single(o => o.Kind == "local_processing");
        Require(operations.All(o => o.OperationAdmission!.ExecutionContributions.Count > 0 &&
            o.OperationAdmission.ExecutionContributions.All(p => p.Version == 2 && !string.IsNullOrEmpty(p.ProofFingerprint)) &&
            o.OperationAdmission.Assignments.All(a => o.OperationAdmission.ExecutionContributions.Any(p => p.RuntimeEvidenceId == a.RuntimeEvidenceId &&
                p.Contributions.Any(c => c.Id == a.ContributionId && c.EvidenceReference == a.ActionReference &&
                    (a.Disposition == "supports" ? c.Role == "supports" && c.EffectId == o.Id : c.Role == "governing_property" && c.EffectId is null &&
                        o.OperationAdmission.GoverningApplicability.Any(g => g.Version == 1 && g.ContributionId == c.Id && g.Outcome == "active" && g.Targets.Contains(o.Id))))))),
            "DIAGNOSTIC_CONTRIBUTION_AUTHORITY", "Every contribution requires current effect-specific execution or governing qualification.");
        Require(operations.All(o => o.OperationAdmission!.RealizationCoverage is { Version: 3 } coverage &&
            !string.IsNullOrEmpty(coverage.ProofFingerprint) && coverage.SelectedEffects.Contains(o.Id) &&
            coverage.Effects.Count(e => e.Id == o.Id) == 1 &&
            coverage.Effects.Single(e => e.Id == o.Id).SupportingEvidence.Count > 0 &&
            coverage.Effects.Single(e => e.Id == o.Id).SupportingEvidence.All(id => coverage.Contributions.Any(c =>
                c.ContributionId == id && c.Disposition == "supports" && c.Effects.Contains(o.Id) && c.EvidenceReferences.Count > 0))),
            "DIAGNOSTIC_REALIZATION_COVERAGE", "Every admitted effect requires current complete coverage with owned executable support.");
        var effects = local.OperationAdmission!.Assignments.Select(a => a.Effect!).ToArray();
        Require(operations.SelectMany(o => o.OperationAdmission!.Assignments).All(a => a.Effect is { Producers.Count: 0 }),
            "DIAGNOSTIC_LEGACY_PRODUCER_AUTHORITY", "Current effect mappings cannot select operation producers.");
        Require(operations.SelectMany(o => o.OperationAdmission!.Assignments).All(a => a.Effect is { Version: 7 }) && effects.SelectMany(e => e.Outputs).ToHashSet(StringComparer.Ordinal).SetEquals([output]),
            "DIAGNOSTIC_EFFECT_OWNERSHIP", "The transformation must produce the canonical public result.");
        Require(effects.SelectMany(e => e.Candidates).All(e => e.BoundaryKind == "result_realization" && e.OwnerReference == output && e.WorkflowScope == "main" && e.OccurrenceProof is null) &&
            effects.SelectMany(e => e.Candidates).Distinct().Count() == 1,
            "DIAGNOSTIC_RESULT_REALIZATION", "The fixture requires one canonical main result realization.");
        var consumed = effects.SelectMany(e => e.Inputs).ToHashSet(StringComparer.Ordinal);
        if (name == "local")
        {
            Require(consumed.SetEquals(inputs), "DIAGNOSTIC_INPUT_EFFECT", "The local effect must consume both canonical business inputs.");
            Require(dependencyDecisions == 0 && local.OperationAdmission.Dependencies!.Assignments.Count == 0,
                "DIAGNOSTIC_DEPENDENCY_DECISION", "The singleton requires an engine-established empty operation-producer set without dependency-model decisions.");
        }
        else
        {
            var read = operations.Single(o => o.Kind == "external_read");
            var readEffects = read.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Candidates).Distinct().ToArray();
            Require(readEffects.Length == 1 && readEffects[0] is { WorkflowScope: "main", OccurrenceProof: { Version: 1, WorkflowScope: "main" } } &&
                readEffects[0].OccurrenceProof!.Evidence.Kind == "external_effect" &&
                !string.IsNullOrEmpty(readEffects[0].OccurrenceProof!.Fingerprint),
                "DIAGNOSTIC_OCCURRENCE_OWNERSHIP", "The read requires its independently proven external occurrence.");
            var edges = operations.SelectMany(o => o.OperationAdmission!.Dependencies!.Assignments).Where(a => a.Disposition == "data").ToArray();
            Require(consumed.Contains(inputs[1]) && read.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Inputs).Contains(inputs[0]) &&
                edges.Length == 1 && edges[0].Producer == read.Id && edges[0].Consumer == local.Id && edges[0].EvidenceReferences.Count > 0 &&
                edges[0].Origin is PlanningDependencyOrigin.DeterministicBaseline or PlanningDependencyOrigin.DeterministicInterface or PlanningDependencyOrigin.ModelSemanticSelection,
                "DIAGNOSTIC_DEPENDENCY_MISMATCH", "Read ownership and read-to-local dataflow must be grounded before relationship assessment.");
        }
    }

    internal static void RequireLocalApplicability(PlanningObligation local)
    {
        var proof = local.OperationAdmission!;
        if (proof.Assignments.Count(a => a.Disposition == "supports") != 1 ||
            proof.GoverningApplicability.Count == 0 ||
            proof.GoverningApplicability.Any(p => p.Version != 1 || p.Outcome != "active" ||
                !p.Targets.SequenceEqual([local.Id]) || string.IsNullOrEmpty(p.ProofFingerprint) ||
                p.Origin is not (PlanningApplicabilityOrigin.ModelApplicability or PlanningApplicabilityOrigin.DeterministicOwner) ||
                p.Origin == PlanningApplicabilityOrigin.ModelApplicability && p.DecisionId is null ||
                p.Origin == PlanningApplicabilityOrigin.DeterministicOwner && p.OwnerReferences.Count == 0))
            throw new WorkflowRuntimeException("DIAGNOSTIC_APPLICABILITY", "LOCAL requires one qualified support and proven property applicability; singleton cardinality is insufficient.");
    }

    // Fixture acceptance only: inspect exact qualified subspans, not preliminary
    // labels or whole-clause membership. Even an additional governing assignment
    // cannot hide property evidence incorrectly qualified as executable support.
    internal static void RequireGoverningOnly(PlanningSnapshot state, PlanningObligation operation,
        string sourceId, int start, int length)
    {
        var proof = operation.OperationAdmission!;
        var contributions = proof.ExecutionContributions.SelectMany(p => p.Contributions)
            .Where(c => c.EffectId == operation.Id || proof.Assignments.Any(a => a.ContributionId == c.Id)).DistinctBy(c => c.Id).ToArray();
        PlanningReference Reference(string id) => state.References.Single(r => r.Id == id);
        bool Overlaps(string id)
        {
            var r = Reference(id);
            return r.SourceId == sourceId && r.Start < start + length && start < r.Start + r.Length;
        }
        var governing = contributions.Where(c => c.Role == "governing_property" && proof.Assignments.Any(a =>
            a.ContributionId == c.Id && a.Disposition == "attach")).Select(c => Reference(c.EvidenceReference)).ToArray();
        if (start < 0 || length <= 0 || contributions.Any(c => c.Role == "supports" && Overlaps(c.EvidenceReference)) ||
            proof.Assignments.Any(a => a.Disposition == "supports" && Overlaps(a.ActionReference)) ||
            Enumerable.Range(start, length).Any(i => !char.IsWhiteSpace(state.Request.Prompt[i]) &&
                !governing.Any(r => r.SourceId == sourceId && r.Start <= i && i < r.Start + r.Length)))
            throw new WorkflowRuntimeException("DIAGNOSTIC_DESCRIPTIVE_SUPPORT", "Property evidence must be completely attached as governing evidence and must not authorize executable support.");
    }

    internal static void RequireRelationDomain(JsonNode? schema)
    {
        if (schema is JsonObject obj)
        {
            if (obj["enum"] is JsonArray choices && choices.Any(v => v?.ToString() == "data"))
                throw new WorkflowRuntimeException("DIAGNOSTIC_DATA_AUTHORITY", "Later relationship schemas cannot establish operation data dependencies.");
            foreach (var field in obj) RequireRelationDomain(field.Value);
        }
        else if (schema is JsonArray array) foreach (var item in array) RequireRelationDomain(item);
    }

    internal static void RequireProjectedData(IReadOnlyList<PlanningObligation> operations, IEnumerable<PlanningObligationRelation> relations)
    {
        var expected = operations.SelectMany(o => o.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct(StringComparer.Ordinal)
                .Select(id => (id, o.Id)).Concat(o.OperationAdmission.Dependencies!.Assignments.Where(a => a.Disposition == "data").Select(a => (a.Producer, o.Id))))
            .ToHashSet();
        var actual = relations.Where(r => r.Role == "data").Select(r => (r.Producer, r.Consumer)).ToHashSet();
        if (!expected.SetEquals(actual)) throw new WorkflowRuntimeException("DIAGNOSTIC_DATA_AUTHORITY", "Data relations must project only canonical public inputs and the admission dependency proof.");
    }

    // Report only semantic enum domains. Source text, names, scoped references
    // and arbitrary literal/contract content stay in the encrypted manifest.
    internal static JsonObject Domains(JsonObject schema)
    {
        var values = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"] is JsonObject fields)
                    foreach (var name in new[] { "role", "kind", "status", "evidence", "execution", "ownership", "resourceAction", "state" })
                        if (fields[name]?["enum"] is JsonArray choices)
                        {
                            if (!values.TryGetValue(name, out var domain)) values[name] = domain = new(StringComparer.Ordinal);
                            foreach (var choice in choices) domain.Add(choice!.ToString());
                        }
                foreach (var child in obj) Visit(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(schema);
        return new(values.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonArray(p.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()))));
    }
}
