using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningWorkflowConstruction
{
    // Derived from the current revision. No queue, cached graph or mutable worker state.
    internal static Dictionary<string, HashSet<string>> HoleDependencies(PlanningSnapshot state, PlanningWorkflow workflow)
    {
        var holes = state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && !h.Superseded).ToArray();
        var edges = holes.ToDictionary(h => h.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var nodes = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToArray();
        var root = "/workflows/" + state.Graph!.Workflows.IndexOf(workflow);
        foreach (var hole in holes.Where(h => h.Kind == "schema"))
        {
            PlanningValue? value = null;
            if (hole.NodeKey is null && hole.Path.StartsWith(root + "/outputs/", StringComparison.Ordinal) && hole.Path.EndsWith("/schema", StringComparison.Ordinal))
                value = workflow.Outputs[int.Parse(hole.Path.Split('/')[4], System.Globalization.CultureInfo.InvariantCulture)].Value;
            else if (hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal)) value = nodes.FirstOrDefault(n => n.Node.Key == hole.NodeKey && n.Node.Type == "set").Node?.Input;
            if (value is null || value.Path.Count > 0) continue;
            string? sourcePath = value.Kind switch
            {
                "input" => root + "/inputs/" + workflow.Inputs.FindIndex(p => p.Name == value.Source) + "/schema",
                "output" when value.ResultChannel is null or "default" => nodes.FirstOrDefault(n => n.Node.Key == value.Source) is { Node: not null } producer ? root + producer.Path + "/outputSchema" : null,
                _ => null
            };
            // Established equality needs one schema decision, followed by deterministic propagation.
            foreach (var source in holes.Where(h => h.Kind == "schema" && h.Id != hole.Id && (h.Path == sourcePath || sourcePath is not null && h.Path.StartsWith(sourcePath + "/", StringComparison.Ordinal))))
                edges[hole.Id].Add(source.Id);
        }
        foreach (var hole in holes)
        {
            // Public inputs are roots. Result contracts depend on the contracts of
            // their declared inputs/producers even before executable bindings exist.
            if (hole.Kind == "schema" && hole.NodeKey is null && hole.Path.StartsWith(root + "/inputs/", StringComparison.Ordinal)) continue;
            var (node, _) = PlanningHoleContracts.Target(workflow, hole);
            var obligations = PlanningHoleEligibility.Obligations(state, workflow, node);
            var sources = PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, node?.Key ?? PlanningDataflow.WorkflowOutputs, includeUnresolved: true).Values
                .Where(b => node is null || PlanningHoleEligibility.Dependencies(state, workflow, node, b.Value).Overlaps(obligations) ||
                    b.Value.Kind == "output" && nodes.Any(n => n.Node.Key == b.Value.Source && PlanningHoleEligibility.Obligations(state, workflow, n.Node).Overlaps(obligations))).ToArray();
            var producers = sources.Where(b => b.Value.Kind is "output" or "artifact_collection" or "loop_item" or "loop_previous")
                .Select(b => b.Value.Source!).ToHashSet(StringComparer.Ordinal);
            // An unresolved producer contract may not yet have a dataflow address.
            foreach (var producer in nodes.Where(p => p.Node.Key != node?.Key && p.Node.OperationIds.Any(op => obligations.Contains("operation:" + op))))
                producers.Add(producer.Node.Key);
            var inputs = sources.Where(b => b.Value.Kind == "input").Select(b => b.Value.Source!).Concat(obligations.Where(o => o.StartsWith("input:", StringComparison.Ordinal)).Select(o => o[6..])).ToHashSet(StringComparer.Ordinal);
            var location = nodes.FirstOrDefault(n => n.Node.Key == node?.Key).Path;
            foreach (var prerequisite in holes.Where(h => h.Id != hole.Id))
            {
                // Public result schemas need established producer contracts;
                // independently unresolved executable values do not change them.
                if (hole.Kind == "schema" && hole.NodeKey is null)
                { if (prerequisite.Kind == "schema" && prerequisite.NodeKey is not null) edges[hole.Id].Add(prerequisite.Id); continue; }
                var sameField = FieldRoot(prerequisite.Path) == FieldRoot(hole.Path);
                var schema = hole.Kind != "schema" && prerequisite.Kind == "schema" && (sameField || prerequisite.NodeKey == hole.NodeKey && node?.Type == "set");
                var producer = prerequisite.NodeKey is not null && prerequisite.NodeKey != hole.NodeKey && producers.Contains(prerequisite.NodeKey);
                var sourceInput = prerequisite.NodeKey is null && prerequisite.Path.Contains("/inputs/", StringComparison.Ordinal) &&
                    inputs.Contains(workflow.Inputs[int.Parse(prerequisite.Path.Split('/')[4], System.Globalization.CultureInfo.InvariantCulture)].Name);
                var ancestor = prerequisite.NodeKey is not null && nodes.Any(n => n.Node.Key == prerequisite.NodeKey &&
                    n.Node.Type is "switch" or "loop.sequential" or "loop.parallel" && location?.StartsWith(n.Path + "/", StringComparison.Ordinal) == true) && prerequisite.Kind != "schema";
                if (schema || hole.Kind != "default" && (producer || sourceInput || ancestor)) edges[hole.Id].Add(prerequisite.Id);
            }
        }
        return edges;
        static string FieldRoot(string path)
        {
            var parts = path.Split('/');
            return parts.Length > 4 && parts[3] is "inputs" or "outputs" ? string.Join('/', parts.Take(5)) : path;
        }
    }

    internal static (PlanningHole[] Holes, PlanningHoleRequests.Request Request) Batch(PlanningSnapshot state, PlanningWorkflow workflow)
    {
        var holes = state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && !h.Superseded)
            .OrderBy(h => h.CanonicalLocation, StringComparer.Ordinal).ThenBy(h => h.Id, StringComparer.Ordinal).ToArray();
        var dependencies = HoleDependencies(state, workflow);
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hole in holes) Visit(hole.Id);
        var ready = holes.Where(h => dependencies[h.Id].Count == 0).ToArray();
        if (ready.Length == 0) throw new PlanningHoleUnavailableException(holes[0].CanonicalLocation, "Unresolved field dependencies form a cycle. Resolve the governing contracts before construction.");
        var domains = ready.ToDictionary(h => h.Id, h => PlanningHoleEligibility.Analyze(state, workflow, h), StringComparer.Ordinal);
        var selected = new List<PlanningHole>(); PlanningHoleRequests.Request? request = null;
        foreach (var hole in ready)
        {
            if (selected.Any(other => Coupled(hole, other))) continue;
            var candidate = PlanningHoleRequests.Create(state, workflow, [.. selected, hole]);
            var estimate = PlanningJsonTransport.EstimateInputTokens(candidate.Prompt, candidate.Schema);
            if (estimate > state.Request.Generation.MaxInputTokensPerRequest)
            {
                if (selected.Count > 0) continue;
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"Field '{hole.CanonicalLocation}' needs approximately {estimate} input tokens; the ceiling is {state.Request.Generation.MaxInputTokensPerRequest}. Narrow this field's contract or obligation before retrying.");
            }
            selected.Add(hole); request = candidate;
        }
        return (selected.ToArray(), request!);

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new PlanningHoleUnavailableException(holes.Single(h => h.Id == id).CanonicalLocation, "The unresolved field dependency graph contains a cycle. Resolve the governing contracts before dispatch.");
            foreach (var prerequisite in dependencies[id]) Visit(prerequisite);
            visiting.Remove(id); visited.Add(id);
        }

        bool Coupled(PlanningHole left, PlanningHole right)
        {
            if (left.NodeKey is null || left.NodeKey != right.NodeKey || left.Kind != "value" || right.Kind != "value") return false;
            var l = domains[left.Id]; var r = domains[right.Id];
            if (!l.Outstanding.Overlaps(r.Outstanding)) return false;
            var node = PlanningHoleContracts.Target(workflow, left).Node;
            HashSet<string> MayCover(PlanningHoleDomain domain) => domain.Direct.Concat(domain.Parameters)
                .SelectMany(b => PlanningHoleEligibility.Dependencies(state, workflow, node, b.Value)).ToHashSet(StringComparer.Ordinal);
            // Only variable coverage couples assignments; disjoint obligations remain independent.
            var shared = MayCover(l).Intersect(MayCover(r)).Intersect(l.Outstanding).ToHashSet(StringComparer.Ordinal);
            shared.ExceptWith(Guaranteed(l)); shared.ExceptWith(Guaranteed(r));
            return shared.Count > 0;

            HashSet<string> Guaranteed(PlanningHoleDomain domain)
            {
                if (domain.Literal) return new(StringComparer.Ordinal);
                var guaranteed = domain.Direct.Count == 0 ? new HashSet<string>(domain.RequiredHere, StringComparer.Ordinal) :
                    PlanningHoleEligibility.Dependencies(state, workflow, node, domain.Direct[0].Value);
                foreach (var binding in domain.Direct.Skip(1)) guaranteed.IntersectWith(PlanningHoleEligibility.Dependencies(state, workflow, node, binding.Value));
                if (domain.Parameters.Count > 0) guaranteed.IntersectWith(domain.RequiredHere);
                return guaranteed;
            }
        }
    }
}
