using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed record PlanningHoleDomain(JsonObject? Contract, IReadOnlyList<PlanningBinding> Direct,
    IReadOnlyList<PlanningBinding> Parameters, bool Literal, IReadOnlySet<string> Outstanding, IReadOnlySet<string> RequiredHere,
    bool ProvenTransfer, IReadOnlySet<string> BoundaryObligations, bool Omission = false);

/// <summary>The single source of admissible choices for deterministic resolution and model transport.</summary>
internal static class PlanningHoleEligibility
{
    internal static PlanningHoleDomain Analyze(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        var expected = PlanningHoleContracts.Expected(state, workflow, hole);
        if (hole.Kind is "schema" or "default") return new(expected, [], [], hole.Kind == "default", new HashSet<string>(), new HashSet<string>(), false, new HashSet<string>());
        var node = PlanningHoleContracts.Target(workflow, hole).Node;
        var available = Available(state, workflow, hole).ToArray();
        var obligations = Obligations(state, workflow, node);
        // The graph is immutable throughout this analysis. Reuse one validated
        // contract resolver and one provenance result per source, not one full
        // graph validation for every source/neighbor comparison.
        var resolver = node is null ? null : PlanningGraphValidation.OptionalValueContractResolver(state.Graph!, workflow, state.Preparation!);
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> SourceDependencies(PlanningBinding binding)
        {
            if (!dependencies.TryGetValue(binding.Id, out var result))
                dependencies[binding.Id] = result = Dependencies(state, workflow, node, binding.Value, resolver);
            return result;
        }
        var established = node is null ? new HashSet<string>(StringComparer.Ordinal) : Dependencies(state, workflow, node, null, resolver);
        var remaining = obligations.Except(established).ToHashSet(StringComparer.Ordinal);
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in state.Construction.Holes.Where(h => !h.Resolved && h.Kind == "value" && h.WorkflowKey == hole.WorkflowKey && h.NodeKey == hole.NodeKey && h.Id != hole.Id))
            foreach (var binding in available) // All neighbors have the same consumer and availability boundary.
                if (ArtifactFits(state, workflow, other, binding)) alternatives.UnionWith(SourceDependencies(binding));
        var boundary = node is null ? new HashSet<string>(StringComparer.Ordinal) : PlanningWorkflowProvenance.RequiredAtInvocation(state.Graph!, workflow, node, state.Preparation!)
            .Select(op => "operation:" + op).ToHashSet(StringComparer.Ordinal);
        // A callee's incoming operation requirements are contracts for its invocations,
        // not evidence of a local producer. Call arguments must prove them when callers
        // are validated. Do not invent that proof or deadlock callee-first construction.
        var forced = remaining.Except(boundary).Except(alternatives).ToHashSet(StringComparer.Ordinal);
        var original = ArtifactKinds(state, workflow, hole).ToArray();
        var artifactOrigins = obligations.Where(o => o.StartsWith("operation:", StringComparison.Ordinal)).Select(o => o[10..]).ToHashSet(StringComparer.Ordinal);
        if (original.Length > 0)
        {
            // A locked composed operation can produce an artifact in an earlier
            // capability and consume that exact artifact in its next capability.
            // Availability still excludes self, future and unguarded branch results.
            if (node is not null) artifactOrigins.UnionWith(node.OperationIds);
            bool changed;
            do
            {
                changed = false;
                foreach (var capability in state.Preparation!.Capabilities.Where(c => c.OperationIds.Any(artifactOrigins.Contains)))
                    foreach (var input in capability.InputOperationIds) changed |= artifactOrigins.Add(input);
            } while (changed);
        }
        var implicitArtifacts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kind in original)
        {
            // An operation dependency constrains artifact identity only when that
            // dependency actually declares an artifact of this kind. The required
            // artifact contract can independently identify a sole owned materializer.
            // Never substitute another origin when an explicit one is unavailable.
            if (state.Preparation!.Capabilities.Any(c => c.OperationIds.Any(artifactOrigins.Contains) &&
                c.ArtifactContract?.Produces.Any(p => p.Kind == kind) == true)) continue;
            var producers = new HashSet<string>(StringComparer.Ordinal);
            var local = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToHashSet();
            foreach (var binding in available)
                PlanningValueProvenance.Proves(workflow, binding.Value, state.Graph!, (producer, reference) =>
                {
                    var contract = state.Preparation.Capabilities.SingleOrDefault(c => c.Id == producer.CapabilityId);
                    if (!local.Contains(producer) || producer.Type != "mcp.call" || !producer.OperationIds.Any(workflow.OperationIds.Contains) ||
                        contract?.ArtifactContract?.Produces.Any(p => p.Kind == kind && p.Mode == "materialize" &&
                            p.Pointer == "/" + string.Join("/", reference.Path.Select(PlanningSchemaReferences.Escape))) != true) return false;
                    producers.Add(producer.Key); return true;
                });
            implicitArtifacts[kind] = producers.Count == 1 ? producers.Single() : null;
        }
        bool Relevant(PlanningBinding binding)
        {
            if (node is null) return binding.Value.Kind is "output" or "artifact_collection" || workflow.OperationIds.Count == 0 && binding.Value.Kind == "input";
            if (original.Length > 0 && artifactOrigins.Count > 0)
                return original.All(kind => implicitArtifacts.TryGetValue(kind, out var producer)
                    ? producer is not null && PlanningArtifactBindings.Proves(workflow, binding.Value, kind, state.Preparation!, state.Graph!, new(StringComparer.Ordinal), originNode: producer)
                    : PlanningArtifactBindings.Proves(workflow, binding.Value, kind, state.Preparation!, state.Graph!, new(StringComparer.Ordinal), artifactOrigins));
            if (obligations.Count == 0) return false; // Type equality alone cannot invent a business relationship.
            return SourceDependencies(binding).Overlaps(obligations);
        }
        var direct = expected is null ? [] : available.Where(b => Relevant(b) && PlanningContractCompatibility.Fits(b.Schema, expected) &&
            ArtifactFits(state, workflow, hole, b) && forced.IsSubsetOf(SourceDependencies(b))).ToArray();
        var parameters = original.Length > 0 ? [] : available.Where(b => Relevant(b) && b.Value.Path.Count == 0 && b.Value.Kind != "loop_index").ToArray();
        // A proven direct choice is also a valid operand of an otherwise permitted
        // computation. Reuse its issued identity instead of exposing a binding ID
        // that becomes an undeclared variable when the model applies a scalar transform.
        // Original-artifact fields still prohibit computations entirely.
        if (parameters.Length > 0) parameters = parameters.Concat(direct).DistinctBy(b => b.Id).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        // A computation's complete scoped parameter set must be able to discharge the field's obligations.
        if (!forced.IsSubsetOf(parameters.SelectMany(SourceDependencies).ToHashSet(StringComparer.Ordinal))) parameters = [];
        // A direct result is established by explicit operation/input evidence, not merely a matching scalar type.
        var transfer = original.Length > 0 || node is null || node.Type is "workflow.call" or "mcp.call" or "loop.sequential" or "loop.parallel";
        // An executable workflow's public result must derive from its execution.
        // An explicitly fixed result contract remains a legitimate constant; an
        // unconstrained output schema does not authorize fabricated evidence.
        var resultNeedsSource = node is null && workflow.OperationIds.Count > 0 &&
            !(expected is not null && (expected.ContainsKey("const") || expected["enum"] is JsonArray { Count: 1 }));
        var literal = original.Length == 0 && forced.Count == 0 && !resultNeedsSource;
        // A neighboring argument can cover a shared obligation. One compatible
        // scalar source then does not prove that this argument is its identity:
        // constants and transformations remain admissible business choices.
        transfer &= node is null || !literal;
        return new(expected, direct, parameters, literal, remaining, forced, transfer, boundary,
            hole.Optional && forced.Count == 0 && original.Length == 0);
    }

    internal static IReadOnlyList<PlanningBinding> Available(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        if (hole.Kind is "schema" or "default") return [];
        return PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, hole.NodeKey ?? PlanningDataflow.WorkflowOutputs).Values
            .Where(b => b.Availability is not ("opaque" or "absent") && PlanningSchemaPropagation.Established(b.Schema) &&
                (b.Value.Kind != "output" || !state.Construction.Holes.Any(h => h.WorkflowKey == workflow.Key && h.NodeKey == b.Value.Source && !h.Resolved)))
            .OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
    }

    internal static HashSet<string> Obligations(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode? node)
    {
        if (node is null) return new(StringComparer.Ordinal);
        var inputs = state.Construction.Dataflow?.InputObligations.GetValueOrDefault(workflow.Key + "/" + node.Key) ?? [];
        var operations = PlanningOperationCompositions.RequiredInputs(workflow, node, state.Preparation!);
        if (node.Type is "loop.sequential" or "loop.parallel")
        {
            var body = PlanningGraphCompiler.Enumerate(node.Steps).ToArray();
            var contained = body.SelectMany(n => n.OperationIds).Concat(body.Select(PlanningWorkflowProvenance.Target).OfType<string>()
                .SelectMany(k => state.Graph!.Workflows.Single(w => w.Key == k).OperationIds)).ToHashSet(StringComparer.Ordinal);
            operations = operations.Concat(body.SelectMany(n => PlanningOperationCompositions.RequiredInputs(workflow, n, state.Preparation!)))
                .Where(op => !contained.Contains(op)).Distinct(StringComparer.Ordinal).ToArray();
        }
        return inputs.Select(i => "input:" + i).Concat(operations.Select(o => "operation:" + o)).ToHashSet(StringComparer.Ordinal);
    }

    internal static HashSet<string> Dependencies(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode? node, PlanningValue? value, Func<PlanningValue, JsonObject?>? resolver = null)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        if (node is null)
        {
            if (value?.Kind == "input") inputs.Add("input:" + value.Source);
            return inputs;
        }
        var operations = PlanningDataflow.OperationDependencies(workflow, node, state.Preparation!, state.Graph!, inputs, value, resolver).Operations;
        if (value is null) inputs.UnionWith(PlanningDataflow.BusinessInputs(workflow, node));
        return inputs.Select(i => "input:" + i).Concat(operations.Select(o => "operation:" + o)).ToHashSet(StringComparer.Ordinal);
    }

    internal static IEnumerable<string> ArtifactKinds(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        var (node, members) = PlanningHoleContracts.Target(workflow, hole);
        var capability = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node?.CapabilityId);
        if (node?.Type == "mcp.call" && members.FirstOrDefault() == "request") members = members[1..];
        var pointer = "/" + string.Join("/", members.Select(PlanningFieldPaths.Escape));
        return capability?.ArtifactContract?.Consumes.Where(c => c.Pointer == pointer).Select(c => c.Kind) ?? [];
    }
    private static bool ArtifactFits(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole, PlanningBinding binding) =>
        ArtifactKinds(state, workflow, hole).All(kind => PlanningArtifactBindings.Proves(workflow, binding.Value, kind, state.Preparation!, state.Graph!, new(StringComparer.Ordinal)));

    internal static PlanningDiagnostic? Validate(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole, PlanningValue? value)
    {
        if (hole.Kind == "schema") return null;
        var domain = Analyze(state, workflow, hole);
        if (value?.Kind == PlanningSkeletonInputs.Omitted)
            return domain.Omission ? null : Finding("Omitting this field would lose a required argument or dependency.");
        if (value is null || PlanningGraphValidation.IsLiteral(value))
            return domain.Literal ? null : Finding("A literal cannot fulfill this field's remaining dynamic or artifact obligations.");
        if (value.Kind == "compute")
        {
            var eligible = domain.Parameters.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            if (value.Members.Any(m => !eligible.Contains(PlanningBindingIdentity.Id(m.Value)))) return Finding("The computation references a parameter outside the current field domain.");
            if (!domain.Literal && value.Members.Count == 0) return Finding("The computation must reference an eligible execution source.");
            var node = PlanningHoleContracts.Target(workflow, hole).Node;
            if (!domain.RequiredHere.IsSubsetOf(Dependencies(state, workflow, node, value))) return Finding("The computation omits a required field dependency.");
            return null;
        }
        return domain.Direct.Any(b => b.Id == PlanningBindingIdentity.Id(value)) ? null : Finding("The binding does not satisfy this field's current contract, availability and provenance obligations.");
        PlanningDiagnostic Finding(string message) => new("HOLE_BINDING_INELIGIBLE", hole.Path, message, ValidationStage: "dataflow", Rule: "field_domain");
    }
}
