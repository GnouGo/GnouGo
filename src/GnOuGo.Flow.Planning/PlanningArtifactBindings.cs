using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Artifact identity comes from declared producer provenance, not matching scalar types.</summary>
internal static class PlanningArtifactBindings
{
    internal static IEnumerable<PlanningDiagnostic> PrerequisiteFindings(PlanningGraph graph, PlanningCatalog catalog)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            var located = PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{wi}/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{wi}/finally")).ToArray();
            foreach (var (producer, path) in located)
            {
                if (producer.Type != "mcp.call" || !producer.OnError.Any(h => h.Action == "continue")) continue;
                var produced = catalog.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.ArtifactContract?.Produces;
                if (produced is null) continue;
                var consumers = located.Where(p => p.Node != producer && catalog.Capabilities.FirstOrDefault(c => c.Id == p.Node.CapabilityId)?.ArtifactContract?.Consumes
                    .Any(c => c.Required && produced.Any(a => a.Kind == c.Kind)) == true).ToArray();
                if (consumers.Length == 0) continue;
                yield return new("ARTIFACT_FAILURE_PATH_UNPROVEN", path + "/onError",
                    "A required downstream artifact comes from this original producer. Continuing after its failure cannot manufacture that artifact in a fallback or structured result. " +
                    "Fail closed at this producer while retaining workflow cleanup, or revise the intent plan to establish a guarded consumer and an explicit failure route. A copied value does not prove artifact identity.",
                    ValidationStage: "dataflow");
            }
        }
    }

    // Locations are optional producer declarations. Unknown addresses remain reviewable;
    // neither strings nor lifecycle effects establish ownership or permission.
    internal static IEnumerable<PlanningDiagnostic> LifecycleFindings(PlanningGraph graph, PlanningCatalog catalog)
    {
        var events = new List<(PlanningWorkflow Workflow, PlanningNode Node, string Path, Dictionary<string, int> Branches, McpArtifactLocation Location, string Address)>();
        var activeWorkflows = new HashSet<string>(StringComparer.Ordinal);
        var entry = graph.Workflows.Single(w => w.Key == graph.Entrypoint);
        Walk(entry, entry.Steps, "/workflows/" + graph.Workflows.IndexOf(entry) + "/steps", []);
        Walk(entry, entry.Finally, "/workflows/" + graph.Workflows.IndexOf(entry) + "/finally", []);
        bool Together(int left, int right) => events[left].Branches.All(b => !events[right].Branches.TryGetValue(b.Key, out var branch) || branch == b.Value);
        bool Covers(int release, int target) => (events[release].Location.Kind != "handle" && events[target].Location.Kind != "handle" || events[release].Location.Space == events[target].Location.Space) &&
            (events[release].Address == events[target].Address || events[release].Location.Kind == "directory" && events[target].Location.Kind != "handle" &&
                events[target].Address.StartsWith(events[release].Address.TrimEnd('/') + "/", StringComparison.Ordinal));
        for (var released = 0; released < events.Count; released++)
        {
            if (events[released].Location.Action != "release") continue;
            for (var consumer = released + 1; consumer < events.Count; consumer++)
                if (events[consumer].Location.Action == "use" && Together(released, consumer) && Covers(released, consumer) &&
                    !Enumerable.Range(released + 1, consumer - released - 1).Any(i => events[i].Location.Action == "materialize" && events[i].Address == events[consumer].Address && events[i].Branches.Count == 0))
                    yield return new("ARTIFACT_USE_AFTER_RELEASE", events[consumer].Path + "/input", "This declared resource can be consumed after release at " + events[released].Path + ". Retain it until its last consumer.", ValidationStage: "dataflow");
            for (var producer = 0; producer < released; producer++)
            {
                var item = events[producer];
                if (item.Location.Action != "materialize" || item.Location.OutputPointer is null || !Together(producer, released) || !Covers(released, producer)) continue;
                if (entry.Outputs.Any(o => Returned(entry, o.Value, item.Node, item.Location.OutputPointer)))
                    yield return new("ARTIFACT_OUTPUT_RELEASED", events[released].Path + "/input", "A returned artifact produced at " + item.Path + " is inside this declared release. Materialize the business result outside the released resource before cleanup and return that surviving producer.", ValidationStage: "dataflow");
            }
        }

        bool Returned(PlanningWorkflow workflow, PlanningValue value, PlanningNode producer, string pointer) =>
            value.Kind == "object" ? value.Members.Any(m => Returned(workflow, m.Value, producer, pointer)) :
            value.Kind == "lookup" && value.Items.Count == 2 ? Returned(workflow, value.Items[0], producer, pointer) :
            value.Kind is "array" or "flatten" ? value.Items.Any(v => Returned(workflow, v, producer, pointer)) :
            PlanningValueProvenance.Proves(workflow, value, graph, (node, reference) => ReferenceEquals(node, producer) && reference.Path.SequenceEqual(TaskArtifactBindings.Decode(pointer)));

        string? Literal(PlanningWorkflow workflow, PlanningValue? value, HashSet<string>? visiting = null)
        {
            if (value is { Kind: "string" }) return value.Text;
            if (value is null) return null;
            visiting ??= new(StringComparer.Ordinal); var key = workflow.Key + ":" + PlanningBindingIdentity.Id(value);
            if (!visiting.Add(key)) return null;
            string? found = null;
            PlanningValueProvenance.Proves(workflow, value, graph, (producer, reference) =>
            {
                if (producer.Type == "set") found = Literal(workflow, PlanningValueProvenance.Select(producer.Input, reference.Path), visiting);
                else foreach (var location in Locations(producer))
                    if (location.OutputPointer is not null && reference.Path.SequenceEqual(TaskArtifactBindings.Decode(location.OutputPointer)))
                        found = Literal(workflow, PlanningValueProvenance.Select(PlanningGraphValidation.Member(producer.Input, "request"), TaskArtifactBindings.Decode(location.Pointer)), visiting);
                return found is not null;
            });
            visiting.Remove(key); return found;
        }
        IEnumerable<McpArtifactLocation> Locations(PlanningNode node) => catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.ArtifactContract?.Locations ?? [];
        void Walk(PlanningWorkflow workflow, List<PlanningNode> nodes, string path, Dictionary<string, int> branches)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.If is { Kind: "boolean", Boolean: false }) continue;
                var current = path + "/" + i; var args = PlanningGraphValidation.Member(node.Input, "request");
                foreach (var location in Locations(node))
                {
                    if (location.SelectorPointer is not null && Literal(workflow, PlanningValueProvenance.Select(args, TaskArtifactBindings.Decode(location.SelectorPointer))) != location.SelectorValue) continue;
                    var value = Literal(workflow, PlanningValueProvenance.Select(args, TaskArtifactBindings.Decode(location.Pointer)));
                    if (value is null) continue;
                    var address = location.Kind == "handle" ? value : Uri.TryCreate(location.Space, UriKind.Absolute, out var space) && Uri.TryCreate(space, value.Replace('\\', '/'), out var resolved) ? resolved.AbsoluteUri : null;
                    if (address is not null) events.Add((workflow, node, current, branches, location, address));
                }
                Walk(workflow, node.Steps, current + "/steps", branches);
                // Parallel siblings may overlap; only mutually exclusive switch branches are separated.
                for (var b = 0; b < node.Branches.Count; b++) Walk(workflow, node.Branches[b].Steps, current + "/branches/" + b + "/steps", branches);
                var selector = node.Expr is { Kind: "boolean", Boolean: { } boolean } ? boolean ? "true" : "false" : node.Expr is { Kind: "string" } ? node.Expr.Text : null;
                for (var b = 0; b < node.Cases.Count; b++)
                    if (selector is null || node.Cases[b].When is not null || node.Cases[b].Value == selector) Walk(workflow, node.Cases[b].Steps, current + "/cases/" + b + "/steps", new(branches) { [current] = b });
                if (selector is null || node.Cases.All(c => c.Value != selector)) Walk(workflow, node.Default, current + "/default", new(branches) { [current] = -1 });
                if (node.Type == "workflow.call" && graph.Workflows.FirstOrDefault(w => w.Key == PlanningGraphValidation.Member(node.Input, "ref")?.Source) is { } child)
                {
                    var childPath = "/workflows/" + graph.Workflows.IndexOf(child);
                    if (activeWorkflows.Add(child.Key))
                    { Walk(child, child.Steps, childPath + "/steps", branches); Walk(child, child.Finally, childPath + "/finally", branches); activeWorkflows.Remove(child.Key); }
                }
            }
        }
    }

    internal static bool Proves(PlanningWorkflow workflow, PlanningValue value, string kind, PlanningCatalog catalog, PlanningGraph graph, HashSet<string> visited, string? originNode = null)
    {
        return PlanningValueProvenance.Proves(workflow, value, graph, (producer, reference) =>
        {
            var capability = catalog.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId);
            return producer.Type == "mcp.call" &&
                (originNode is null || producer.Key == originNode && PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Contains(producer)) &&
                capability?.ArtifactContract?.Produces.Any(p => p.Kind == kind && p.Pointer == "/" + string.Join("/", reference.Path.Select(PlanningSchemaReferences.Escape))) == true;
        });
    }
}
