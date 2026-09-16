using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // Derived from the complete, surviving realization set. Never a second graph.
    internal sealed record DependencyDomain(string Fingerprint,
        PlanningOperationDependencyAssignment[] Facts, PlanningDecisionPages.Decision[] Decisions);

    internal static string DependencyDecisionId(string producer, string consumer) => "data_" +
        PlanningGraphCompiler.Fingerprint(new JsonArray(producer, consumer).ToJsonString())[..24];

    internal static DependencyDomain DependencyDecisions(PlanningSnapshot state, IReadOnlyList<PlanningObligation> operations)
    {
        var ordered = operations.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var fingerprint = DependencyDomainFingerprint(state, ordered);
        var facts = BaselineDependencies(state, ordered);
        ValidateDependencyEdges(ordered, facts);
        var decisions = new List<PlanningDecisionPages.Decision>();
        foreach (var consumer in ordered)
        foreach (var producer in ordered)
        {
            if (producer.Id == consumer.Id || EffectAnchor(state, producer).WorkflowScope != EffectAnchor(state, consumer).WorkflowScope ||
                facts.Any(f => f.Producer == producer.Id && f.Consumer == consumer.Id)) continue;
            // Filter only facts established before any semantic selection. Never
            // let pair traversal or page completion order choose an acyclic graph.
            if (Reaches(facts, consumer.Id, producer.Id)) continue;
            var sourceRefs = DependencyReferences(producer); var targetRefs = DependencyReferences(consumer);
            var refs = sourceRefs.Concat(targetRefs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var schema = new JsonObject { ["anyOf"] = new JsonArray(
                PlanningHoleRequests.Object(("relation", PlanningHoleRequests.Enum(["data", "none"])),
                    ("producerEvidence", ReferencesArray(sourceRefs, 1)), ("consumerEvidence", ReferencesArray(targetRefs, 1))),
                PlanningHoleRequests.Object(("relation", PlanningHoleRequests.Enum(["unresolved"])))) };
            decisions.Add(new(DependencyDecisionId(producer.Id, consumer.Id), schema, new()
            {
                ["stage"] = "operation_dependencies", ["producer"] = producer.Id, ["consumer"] = consumer.Id,
                ["producerKind"] = producer.Kind, ["consumerKind"] = consumer.Kind,
                ["producerEvidence"] = Strings(sourceRefs),
                ["consumerEvidence"] = Strings(targetRefs),
                ["references"] = new JsonObject(refs.Select(id => new KeyValuePair<string, JsonNode?>(id, JsonValue.Create(PlanningChoiceEvidence.Text(state, id))))),
                ["task"] = "Assess only explicit operation-to-operation result consumption. data requires owned evidence that this consumer needs this producer's result. none requires the complete bounded evidence to establish that no explicit producer relationship is required; it is not a claim of semantic independence. A required but unproven relationship is unresolved. Shared inputs, compatible kinds, order of mention or shared governing rules do not prove consumption. Permission, policy, resource ownership and failure handling are assessed separately."
            }, fingerprint));
        }
        return new(fingerprint, facts, decisions.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray());
    }

    private static string[] DependencyReferences(PlanningObligation operation) => operation.OperationAdmission!.Assignments
        .SelectMany(a => new[] { a.ActionReference, a.ClauseReference }.Concat(a.Effect!.EvidenceReferences))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string DependencyDomainFingerprint(PlanningSnapshot state, IEnumerable<PlanningObligation> operations)
    {
        var contracts = new JsonArray(operations.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => (JsonNode)new JsonObject
        {
            ["id"] = o.Id, ["kind"] = o.Kind, ["baseline"] = o.OperationAdmission!.BaselineReference,
            ["anchor"] = JsonSerializer.SerializeToNode(EffectAnchor(state, o), PlanningJsonContext.Default.PlanningOperationEffectAnchor),
            ["inputs"] = Strings(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Inputs)),
            ["outputs"] = Strings(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Outputs)),
            ["evidence"] = new JsonObject(DependencyReferences(o).Select(id => new KeyValuePair<string, JsonNode?>(id,
                JsonValue.Create(PlanningChoiceEvidence.Current(state, id) ? PlanningChoiceEvidence.Text(state, id) : throw Failure(id, "Dependency evidence is stale or foreign.")))))
        }).ToArray());
        return PlanningGraphCompiler.Fingerprint("operation-dependencies-v1:" + state.Request.TenantId + ":" + state.Request.SessionId + ":" +
            state.DeclarationFingerprint + ":" + (state.Request.Baseline is null ? "" : PlanningGraphCompiler.Fingerprint(state.Request.Baseline)) + ":" + contracts.ToJsonString());
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static PlanningOperationDependencyAssignment[] BaselineDependencies(PlanningSnapshot state, PlanningObligation[] operations)
    {
        var nodes = PlanningSourceGroundingRules.BaselineNodes(state);
        var result = new List<PlanningOperationDependencyAssignment>();
        foreach (var consumer in operations.Where(o => o.OperationAdmission!.BaselineReference is not null))
        {
            var reference = consumer.OperationAdmission!.BaselineReference!; var baseline = nodes[reference];
            // These exact bindings already own authority. A model cannot replace
            // one with none. Loop recurrence remains a loop contract, not a DAG self edge.
            var sources = PlanningDataflow.References(baseline.Node.Input).Concat(PlanningDataflow.Conditions(baseline.Node).SelectMany(PlanningDataflow.References))
                .Where(v => v.Kind is "output" or "artifact_collection" or "loop_item" or "loop_index" or "loop_previous")
                .Where(v => !(v.Source == baseline.Node.Key && v.Kind is "loop_index" or "loop_previous"))
                .Select(v => v.Source!).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var source in sources)
            {
                var producer = operations.SingleOrDefault(o => o.OperationAdmission!.BaselineReference is { } r && nodes[r].Workflow == baseline.Workflow && nodes[r].Node.Key == source)
                    ?? throw Failure(consumer.Id, "An exact baseline producer has no surviving canonical realization.");
                var producerRef = producer.OperationAdmission!.BaselineReference!;
                if (nodes[producerRef].Node.Type == "workflow.call" &&
                    (PlanningWorkflowProvenance.Target(nodes[producerRef].Node) is not { } target ||
                     state.Request.Baseline!.Workflows.All(w => w.Key != target)))
                    throw Failure(consumer.Id, "A caller result requires an already established workflow interface.");
                result.Add(new(producer.Id, consumer.Id, "data", nodes[producerRef].Node.Type == "workflow.call"
                    ? PlanningDependencyOrigin.DeterministicInterface : PlanningDependencyOrigin.DeterministicBaseline,
                    new[] { reference, producerRef }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), null));
            }
            // Exact baseline-to-baseline absence is structural exclusion. It does
            // not assert independence of arbitrary newly requested operations.
            foreach (var producer in operations.Where(o => o.Id != consumer.Id && o.OperationAdmission!.BaselineReference is { } r && nodes[r].Workflow == baseline.Workflow))
                if (!result.Any(a => a.Producer == producer.Id && a.Consumer == consumer.Id))
                    result.Add(new(producer.Id, consumer.Id, "none", PlanningDependencyOrigin.DeterministicBaseline,
                        new[] { reference, producer.OperationAdmission!.BaselineReference! }.Order(StringComparer.Ordinal).ToList(), null));
        }
        return result.OrderBy(a => a.Consumer, StringComparer.Ordinal).ThenBy(a => a.Producer, StringComparer.Ordinal).ToArray();
    }

    internal static async Task ResolveDependenciesAsync(PlanningSnapshot state, IPlanningRuntime runtime, List<PlanningObligation> operations, CancellationToken ct)
    {
        var domain = DependencyDecisions(state, operations);
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", domain.Decisions, ct);
        InstallDependencies(state, operations, domain, values);
    }

    internal static void InstallDependencies(PlanningSnapshot state, List<PlanningObligation> operations, DependencyDomain domain, JsonObject values)
    {
        var proofs = ReadDependencyProofs(state, operations, domain, values);
        foreach (var operation in operations.ToArray())
            Replace(operations, Prove(state, operation, operation.OperationAdmission! with { Dependencies = proofs[operation.Id] }));
    }

    private static Dictionary<string, PlanningOperationDependencyProof> ReadDependencyProofs(PlanningSnapshot state,
        IReadOnlyList<PlanningObligation> operations, DependencyDomain domain, JsonObject values)
    {
        if (domain.Fingerprint != DependencyDomainFingerprint(state, operations) || values.Count != domain.Decisions.Length)
            throw Failure("$plan", "Dependency domain or decision coverage changed.");
        var assignments = domain.Facts.ToList();
        foreach (var decision in domain.Decisions)
        {
            if (values[decision.Id] is not JsonObject answer || PlanningContractValidation.ValidateInstance(answer, decision.Schema).Count != 0)
                throw Failure(decision.Id, "Dependency selection changed its issued references or disposition.");
            var relation = answer["relation"]!.ToString();
            if (relation == "unresolved") throw Failure(decision.Id, "A required producer relationship could not be established from owned evidence.");
            var evidence = answer["producerEvidence"]!.AsArray().Concat(answer["consumerEvidence"]!.AsArray()).Select(v => v!.ToString())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            assignments.Add(new(decision.Context["producer"]!.ToString(), decision.Context["consumer"]!.ToString(), relation,
                PlanningDependencyOrigin.ModelSemanticSelection, evidence, decision.Id));
        }
        ValidateDependencyEdges(operations, assignments);
        return operations.ToDictionary(o => o.Id, o =>
        {
            var accepted = assignments.Where(a => a.Consumer == o.Id).OrderBy(a => a.Producer, StringComparer.Ordinal).ToList();
            return new PlanningOperationDependencyProof(1, domain.Fingerprint, accepted, PlanningGraphCompiler.Fingerprint(
                domain.Fingerprint + ":" + o.Id + ":" + JsonSerializer.Serialize(accepted, PlanningJsonContext.Default.ListPlanningOperationDependencyAssignment)));
        }, StringComparer.Ordinal);
    }

    internal static IEnumerable<PlanningObligationRelation> EffectRelations(IEnumerable<PlanningObligation> operations) => operations.SelectMany(o =>
        o.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct(StringComparer.Ordinal)
            .Select(id => new PlanningObligationRelation(id, o.Id, "data"))
            .Concat((o.OperationAdmission.Dependencies?.Assignments ?? []).Where(a => a.Disposition == "data")
                .Select(a => new PlanningObligationRelation(a.Producer, o.Id, "data"))));

    private static void ValidateEffectDependencies(PlanningSnapshot state, IReadOnlyList<PlanningObligation> operations)
    {
        var domain = DependencyDecisions(state, operations);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", domain.Decisions); }
        catch (PlanningConflictException) { throw Failure("$plan", "Dependency proof requires its original completed scope.", "INTENT_OPERATION_PROOF_MISSING"); }
        var expected = ReadDependencyProofs(state, operations, domain, values);
        foreach (var operation in operations)
            if (operation.OperationAdmission!.Dependencies is not { Version: 1 } actual ||
                JsonSerializer.Serialize(actual, PlanningJsonContext.Default.PlanningOperationDependencyProof) !=
                JsonSerializer.Serialize(expected[operation.Id], PlanningJsonContext.Default.PlanningOperationDependencyProof))
                throw Failure(operation.Id, "Canonical dependency proof is missing, altered or stale.", "INTENT_OPERATION_PROOF_MISSING");
    }

    private static bool Reaches(IEnumerable<PlanningOperationDependencyAssignment> assignments, string from, string to)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(from);
        while (pending.TryPop(out var id))
        {
            if (id == to) return true;
            if (!visited.Add(id)) continue;
            foreach (var next in assignments.Where(a => a.Disposition == "data" && a.Producer == id)) pending.Push(next.Consumer);
        }
        return false;
    }

    private static void ValidateDependencyEdges(IReadOnlyList<PlanningObligation> operations, IEnumerable<PlanningOperationDependencyAssignment> values)
    {
        var assignments = values.ToArray(); var ids = operations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in assignments.Where(a => a.Disposition == "data"))
            if (!ids.Contains(edge.Producer) || !ids.Contains(edge.Consumer) || edge.Producer == edge.Consumer || Reaches(assignments, edge.Consumer, edge.Producer))
                throw Failure(edge.Consumer, "Grounded effects contain a dependency cycle or an unrealized producer.");
    }
}
