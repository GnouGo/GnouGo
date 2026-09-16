using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Frozen business-case assertions belong only to the isolated harness.
    private static void CheckEffectFixture(string name, PlanningSnapshot state, PlanningObligation[] operations)
    {
        var inputs = new[] { name == "local" ? "record" : "sourceId", "threshold" }
            .Select(n => state.Declarations.Single(d => d.Direction == "input" && PlanningDeclarations.Name(state, d) == n).Id).ToArray();
        var output = state.Declarations.Single(d => d.Direction == "output" && PlanningDeclarations.Name(state, d) == "classifiedResult");
        var identityDecisions = state.DecisionPages.Where(p => p.RequestId is not null).SelectMany(p => p.Decisions)
            .Where(d => d.StartsWith("operation_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        var dependencyDecisions = state.DecisionPages.Where(p => p.RequestId is not null).SelectMany(p => p.Decisions)
            .Where(d => d.StartsWith("data_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        RuntimeAdmissionDiagnosticRules.RequireEffects(name, operations, inputs, output.Id, identityDecisions, dependencyDecisions);
        if (state.RuntimeEvidence.Any(e => e.Role == "unresolved" || e.ExecutionScope == PlanningRuntimeExecutionScope.Unknown))
            throw new WorkflowRuntimeException("DIAGNOSTIC_RUNTIME_UNRESOLVED", "Runtime evidence remains unresolved.");
        var local = operations.Single(o => o.Kind == "local_processing");
        bool Covers(string reference, string text)
        {
            var span = state.References.Single(r => r.Id == reference);
            var start = state.Request.Prompt.IndexOf(text, StringComparison.Ordinal);
            return start >= 0 && span.SourceId == "request" && span.Start <= start && span.Start + span.Length >= start + text.Length;
        }
        var rules = name == "local"
            ? "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise."
            : "Classify the loaded record: rejected when approved is false, high when approved is true and amount>=threshold, standard otherwise.";
        if (!local.OperationAdmission!.Assignments.Any(a => Covers(a.ClauseReference, rules)))
            throw new WorkflowRuntimeException("DIAGNOSTIC_GOVERNING_EVIDENCE", "Classification rules and fallback do not govern the canonical local effect.");
        if (name == "local" && !local.OperationAdmission.Assignments.Any(a => a.Disposition == "attach" &&
            Covers(a.ClauseReference, "This is deterministic, local, in-memory business processing.")))
            throw new WorkflowRuntimeException("DIAGNOSTIC_DESCRIPTIVE_EVIDENCE", "Descriptive local evidence must attach to the established effect.");
        var preservation = name == "local" ? "Preserve the original id and amount." : "preserve the loaded record's original id and amount.";
        if (!output.ModifierReferences.Any(r => Covers(r, preservation)) || operations.Any(o => Covers(o.OperationAdmission!.AnchorReference, preservation)))
            throw new WorkflowRuntimeException("DIAGNOSTIC_PRESERVATION_EFFECT", "Preservation must remain output-contract evidence, not a standalone occurrence.");
    }

    private static void AddEffectReport(JsonObject report, PlanningSnapshot state, IReadOnlyDictionary<string, LLMResponse?> receipts, JsonArray domains)
    {
        static string Category(string decision) => decision.StartsWith("interpret_", StringComparison.Ordinal) ? "interpretation" :
            decision.StartsWith("coverage_", StringComparison.Ordinal) ? "realization_coverage" :
            decision.StartsWith("effect_governing_", StringComparison.Ordinal) ? "effect_governing" :
            decision.StartsWith("effect_", StringComparison.Ordinal) ? "effect_realizations" :
            decision.StartsWith("boundary_", StringComparison.Ordinal) ? "boundary_scope" :
            decision.StartsWith("operation_", StringComparison.Ordinal) ? "occurrence_identity" :
            decision.StartsWith("data_", StringComparison.Ordinal) ? "operation_dependencies" : "relationships_or_other";
        long? Sum(IEnumerable<long?> values)
        { var all = values.ToArray(); return all.Any(v => v is null) ? null : all.Sum(v => v!.Value); }
        report["decisionClasses"] = new JsonArray(new[] { "interpretation", "realization_coverage", "effect_realizations", "effect_governing", "boundary_scope", "occurrence_identity", "operation_dependencies", "relationships_or_other" }.Select(category =>
        {
            var requests = domains.Where(d => d!["decisions"]!.AsArray().Any(v => Category(v!.ToString()) == category)).ToArray();
            var verified = requests.Where(d => receipts.GetValueOrDefault(d!["requestId"]!.ToString()) is not null).ToArray();
            var responses = verified.Select(d => receipts[d!["requestId"]!.ToString()]).ToArray();
            return (JsonNode)new JsonObject { ["category"] = category, ["journalRequests"] = requests.Length, ["verifiedCalls"] = verified.Length,
                ["distinctExposedDecisions"] = requests.SelectMany(d => d!["decisions"]!.AsArray().Select(v => v!.ToString())).Where(d => Category(d) == category).Distinct().Count(),
                ["inputTokens"] = Sum(responses.Select(r => ProgressiveReport.Usage(r, false))),
                ["outputTokens"] = Sum(responses.Select(r => ProgressiveReport.Usage(r, true))),
                ["reasoningTokens"] = Sum(responses.Select(ProgressiveReport.ReasoningUsage)),
                ["unverifiableUsage"] = requests.Length - verified.Length };
        }).ToArray());
        report["decisionClassUsageOverlapsIfRequestContainsSeveralClasses"] = true;
        report["effectMappings"] = state.OperationAdmissionFingerprint is null ? null : new JsonArray(state.Obligations.Where(PlanningSourceDecisions.IsOperation).Select(o =>
            (JsonNode)new JsonObject { ["operationId"] = o.Id, ["kind"] = o.Kind, ["required"] = o.Required,
                ["admissionProofFingerprint"] = o.OperationAdmission!.ProofFingerprint,
                ["dependencyProofVersion"] = o.OperationAdmission.Dependencies!.Version,
                ["dependencyDomainFingerprint"] = o.OperationAdmission.Dependencies.DomainFingerprint,
                ["dependencyProofFingerprint"] = o.OperationAdmission.Dependencies.ProofFingerprint,
                ["inputs"] = new JsonArray(o.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct().Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["outputs"] = new JsonArray(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Outputs).Distinct().Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["producers"] = new JsonArray(o.OperationAdmission.Dependencies!.Assignments.Where(a => a.Disposition == "data").Select(a => a.Producer).Distinct().Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["dependencies"] = new JsonArray(o.OperationAdmission.Dependencies.Assignments.Select(a => (JsonNode)new JsonObject
                { ["producer"] = a.Producer, ["consumer"] = a.Consumer, ["disposition"] = a.Disposition, ["origin"] = a.Origin.ToString(), ["decisionId"] = a.DecisionId,
                    ["evidenceReferences"] = new JsonArray(a.EvidenceReferences.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }).ToArray()),
                ["effectAnchors"] = new JsonArray(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Candidates).Distinct().Select(e => (JsonNode)new JsonObject
                { ["scope"] = e.WorkflowScope, ["owner"] = e.OwnerReference, ["boundaryKind"] = e.BoundaryKind, ["boundaryReference"] = e.BoundaryReference,
                    ["occurrenceProofVersion"] = e.OccurrenceProof?.Version, ["occurrenceProofFingerprint"] = e.OccurrenceProof?.Fingerprint,
                    ["occurrenceEvidenceKind"] = e.OccurrenceProof?.Evidence.Kind }).ToArray()),
                ["contributions"] = new JsonArray(o.OperationAdmission.Assignments.Select(a => (JsonNode)new JsonObject
                { ["decisionId"] = a.Effect!.DecisionId, ["contribution"] = a.Effect.Contribution, ["origin"] = a.ResolutionOrigin, ["effectOrigin"] = a.Effect.Origin, ["evidenceFingerprint"] = a.Effect.EvidenceFingerprint,
                    ["selectedEffect"] = a.EffectId, ["governingReference"] = a.ClauseReference }).ToArray()) }).ToArray());
        report["effectValidationPassed"] = report["status"]?.ToString() == "passed";
    }
}
