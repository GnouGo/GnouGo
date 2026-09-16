using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class RuntimeAdmissionDiagnosticRules
{
    internal const string Identity = "schema5-occurrence-boundaries-diagnostics-1";
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
        if (name != "local") throw new InvalidOperationException("This campaign authorizes LOCAL only; later gates require separate authorization.");
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
        Require(operations.Count == (name == "local" ? 1 : 2) && operations.All(o => o.Required && o.OperationAdmission is { Version: 9, Dependencies.Version: 1 }) &&
            operations.Count(o => o.Kind == "local_processing") == 1 && operations.Count(o => o.Kind == "external_read") == (name == "mixed" ? 1 : 0),
            "DIAGNOSTIC_ADMISSION_MISMATCH", "The frozen fixture requires exactly its declared runtime effects.");
        var local = operations.Single(o => o.Kind == "local_processing");
        var effects = local.OperationAdmission!.Assignments.Select(a => a.Effect!).ToArray();
        Require(operations.SelectMany(o => o.OperationAdmission!.Assignments).All(a => a.Effect is { Producers.Count: 0 }),
            "DIAGNOSTIC_LEGACY_PRODUCER_AUTHORITY", "Current effect mappings cannot select operation producers.");
        Require(effects.All(e => e is { Version: 4 }) && effects.SelectMany(e => e.Outputs).ToHashSet(StringComparer.Ordinal).SetEquals([output]),
            "DIAGNOSTIC_EFFECT_OWNERSHIP", "The transformation must produce the canonical public result.");
        var consumed = effects.SelectMany(e => e.Inputs).ToHashSet(StringComparer.Ordinal);
        if (name == "local")
        {
            Require(effects.SelectMany(e => e.Candidates).All(e => e.BoundaryKind == "result_realization" && e.OwnerReference == output && e.WorkflowScope == "main" && e.OccurrenceProof is null) &&
                effects.SelectMany(e => e.Candidates).Distinct().Count() == 1,
                "DIAGNOSTIC_RESULT_REALIZATION", "LOCAL requires one canonical main result realization.");
            Require(consumed.SetEquals(inputs), "DIAGNOSTIC_INPUT_EFFECT", "The local effect must consume both canonical business inputs.");
            Require(dependencyDecisions == 0 && local.OperationAdmission.Dependencies!.Assignments.Count == 0,
                "DIAGNOSTIC_DEPENDENCY_DECISION", "The singleton requires an engine-established empty operation-producer set without dependency-model decisions.");
        }
        else
        {
            var read = operations.Single(o => o.Kind == "external_read");
            Require(consumed.Contains(inputs[1]) && local.OperationAdmission.Dependencies!.Assignments.Any(a => a.Disposition == "data" && a.Producer == read.Id) &&
                read.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Contains(inputs[0]) &&
                !read.OperationAdmission.Dependencies!.Assignments.Any(a => a.Disposition == "data" && a.Producer == local.Id),
                "DIAGNOSTIC_DEPENDENCY_MISMATCH", "Read ownership and read-to-local dataflow must be grounded before relationship assessment.");
        }
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
