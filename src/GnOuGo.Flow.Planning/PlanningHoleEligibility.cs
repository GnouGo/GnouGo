using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed record PlanningHoleDomain(JsonObject? Contract, IReadOnlyList<PlanningBinding> Direct,
    IReadOnlyList<PlanningBinding> Parameters, bool Literal, IReadOnlySet<string> Outstanding, IReadOnlySet<string> RequiredHere,
    bool ProvenTransfer, IReadOnlySet<string> BoundaryObligations);

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
        var established = node is null ? new HashSet<string>(StringComparer.Ordinal) : Dependencies(state, workflow, node, null);
        var remaining = obligations.Except(established).ToHashSet(StringComparer.Ordinal);
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in state.Construction.Holes.Where(h => !h.Resolved && h.Kind == "value" && h.WorkflowKey == hole.WorkflowKey && h.NodeKey == hole.NodeKey && h.Id != hole.Id))
            foreach (var binding in Available(state, workflow, other))
                if (ArtifactFits(state, workflow, other, binding)) alternatives.UnionWith(Dependencies(state, workflow, node, binding.Value));
        var boundary = node is null ? new HashSet<string>(StringComparer.Ordinal) : PlanningWorkflowProvenance.RequiredAtInvocation(state.Graph!, workflow, node, state.Preparation!)
            .Select(op => "operation:" + op).ToHashSet(StringComparer.Ordinal);
        // A callee's incoming operation requirements are contracts for its invocations,
        // not evidence of a local producer. Call arguments must prove them when callers
        // are validated. Do not invent that proof or deadlock callee-first construction.
        var forced = remaining.Except(boundary).Except(alternatives).ToHashSet(StringComparer.Ordinal);
        var original = ArtifactKinds(state, workflow, hole).ToArray();
        bool Relevant(PlanningBinding binding)
        {
            if (node is null) return binding.Value.Kind is "output" or "artifact_collection" || workflow.OperationIds.Count == 0 && binding.Value.Kind == "input";
            if (obligations.Count == 0) return false; // Type equality alone cannot invent a business relationship.
            return Dependencies(state, workflow, node, binding.Value).Overlaps(obligations);
        }
        var direct = expected is null ? [] : available.Where(b => Relevant(b) && PlanningContractCompatibility.Fits(b.Schema, expected) &&
            ArtifactFits(state, workflow, hole, b) && forced.IsSubsetOf(Dependencies(state, workflow, node, b.Value))).ToArray();
        var parameters = original.Length > 0 ? [] : available.Where(b => Relevant(b) && b.Value.Path.Count == 0 && b.Value.Kind != "loop_index").ToArray();
        // A computation's complete scoped parameter set must be able to discharge the field's obligations.
        if (!forced.IsSubsetOf(parameters.SelectMany(b => Dependencies(state, workflow, node, b.Value)).ToHashSet(StringComparer.Ordinal))) parameters = [];
        // A direct result is established by explicit operation/input evidence, not merely a matching scalar type.
        var transfer = original.Length > 0 || node is null || node.Type is "workflow.call" or "mcp.call" or "loop.sequential" or "loop.parallel";
        return new(expected, direct, parameters, original.Length == 0 && forced.Count == 0, remaining, forced, transfer, boundary);
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
            operations = operations.Where(op => !contained.Contains(op)).ToArray();
        }
        return inputs.Select(i => "input:" + i).Concat(operations.Select(o => "operation:" + o)).ToHashSet(StringComparer.Ordinal);
    }

    internal static HashSet<string> Dependencies(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode? node, PlanningValue? value)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        if (node is null)
        {
            if (value?.Kind == "input") inputs.Add("input:" + value.Source);
            return inputs;
        }
        var operations = PlanningDataflow.OperationDependencies(workflow, node, state.Preparation!, state.Graph!, inputs, value).Operations;
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
        if (value is null || PlanningGraphValidation.IsLiteral(value))
            return domain.Literal ? null : Finding("A literal cannot fulfill this field's remaining dynamic or artifact obligations.");
        if (value.Kind == "compute")
        {
            var eligible = domain.Parameters.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            if (value.Members.Any(m => !eligible.Contains(PlanningBindingIdentity.Id(m.Value)))) return Finding("The computation references a parameter outside the current field domain.");
            var node = PlanningHoleContracts.Target(workflow, hole).Node;
            if (!domain.RequiredHere.IsSubsetOf(Dependencies(state, workflow, node, value))) return Finding("The computation omits a required field dependency.");
            return null;
        }
        return domain.Direct.Any(b => b.Id == PlanningBindingIdentity.Id(value)) ? null : Finding("The binding does not satisfy this field's current contract, availability and provenance obligations.");
        PlanningDiagnostic Finding(string message) => new("HOLE_BINDING_INELIGIBLE", hole.Path, message, ValidationStage: "dataflow", Rule: "field_domain");
    }
}
