using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningStructureValidation
{
    internal static IReadOnlyList<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningCatalog catalog)
    {
        var findings = new List<PlanningDiagnostic>();
        if (graph.Workflows.Count is < 1 or > 100 || graph.Workflows.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != graph.Workflows.Count)
            findings.Add(new("WORKFLOW_IDENTITIES_INVALID", "/workflows", "Declare 1–100 workflows with unique identifiers."));
        if (!graph.Workflows.Any(w => w.Key == graph.Entrypoint)) findings.Add(new("ENTRYPOINT_INVALID", "/entrypoint", "The entrypoint must identify a declared workflow."));
        var total = 0;
        foreach (var workflow in graph.Workflows)
        {
            var path = "/workflows/" + graph.Workflows.IndexOf(workflow);
            var nodes = PlanningGraphValidation.Located(workflow.Steps, path + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, path + "/finally")).ToArray();
            total += nodes.Length;
            if (nodes.Select(n => n.Node.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            { findings.Add(new("NODE_IDENTITIES_INVALID", path, "Step identifiers must be unique within each workflow.")); continue; }
            if (workflow.Inputs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != workflow.Inputs.Count ||
                workflow.Outputs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != workflow.Outputs.Count)
                findings.Add(new("PORT_IDENTITIES_INVALID", path, "Input and output names must be unique."));
            var keys = nodes.Select(n => n.Node.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (node, location) in nodes)
            {
                if (!catalog.AllowedStepTypes.Contains(node.Type, StringComparer.Ordinal) || node.Type is "workflow.plan" or "workflow.execute")
                    findings.Add(new("STEP_TYPE_DENIED", location + "/type", "The step type is outside the host's executable catalog."));
                var cap = catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                if (node.CapabilityId is not null && cap is null || node.Type == "mcp.call" && cap is null)
                    findings.Add(new("CAPABILITY_UNKNOWN", location + "/capabilityId", "Select an existing catalog capability."));
                if (cap is not null && (cap.StepType != node.Type || catalog.Policy.DeniedCapabilityIds.Contains(cap.Id)))
                    findings.Add(new("CAPABILITY_DENIED", location + "/capabilityId", "The selected executor violates the capability or host policy."));
                if (node.Type == "mcp.call" && node.Input.Members.Any(m => !PlanningGeneratedGraph.IsDirectMcpInput(m.Name)))
                    findings.Add(new("CAPABILITY_TARGET_OVERRIDE", location + "/input", "Use typed request arguments and declared deterministic MCP options; targets remain catalog-owned."));
                if (cap is not null)
                    foreach (var member in node.Input.Members)
                    {
                        var locked = cap.FixedInput.TryGetPropertyValue(member.Name, out var expected);
                        // The runtime accepts this direct option as an alias. A locked nested
                        // error policy must not silently ignore a conflicting generated option.
                        if (!locked && node.Type == "mcp.call" && member.Name == "detect_result_errors" &&
                            cap.FixedInput["error_policy"] is JsonObject policy)
                            locked = policy.TryGetPropertyValue(member.Name, out expected);
                        if (locked && (!PlanningGraphValidation.IsLiteral(member.Value) ||
                            !JsonNode.DeepEquals(PlanningGraphValidation.Literal(member.Value), expected)))
                            findings.Add(new("CAPABILITY_BINDING_OVERRIDE", location + "/input/" + member.Name, "The option conflicts with a catalog-owned binding."));
                    }
                if (node.Type == "workflow.call" && PlanningGraphValidation.Member(node.Input, "ref")?.Kind != "workflow")
                    findings.Add(new("WORKFLOW_REFERENCE_INVALID", location + "/input", "Only declared local workflow references are allowed."));
                foreach (var dependency in node.Dependencies)
                    if (!keys.Contains(dependency)) findings.Add(new("DEPENDENCY_UNKNOWN", location + "/dependencies", "Unknown dependency: " + dependency));
                    else if (!DependencyInScope(workflow, location[path.Length..], dependency, nodes.Single(n => n.Node.Key == dependency).Path[path.Length..]))
                        findings.Add(new("DEPENDENCY_SCOPE", location + "/dependencies", "Dependencies must join sibling steps, or finalizers to a main step in the same workflow. Depend on the container to cross another control-flow scope. Ordering does not make results available."));
            }
            var edges = nodes.ToDictionary(n => n.Node.Key, n => n.Node.Dependencies.Concat(PlanningGraphTopology.References(n.Node).Where(v => v.Kind is "output" or "present").Select(v => v.Source!)).Where(keys.Contains).Distinct().ToArray());
            var visiting = new List<string>(); var completed = new HashSet<string>();
            bool Cycle(string key)
            {
                if (completed.Contains(key)) return false;
                if (visiting.Contains(key))
                {
                    findings.Add(new("DEPENDENCY_CYCLE", nodes.Single(n => n.Node.Key == key).Path,
                        "Step dependencies/output references contain a cycle: " + string.Join(" -> ", visiting.Skip(visiting.IndexOf(key)).Append(key)) + ". A step cannot read its own unfinished result; compute related fields from upstream inputs."));
                    return true;
                }
                visiting.Add(key);
                if (edges[key].Any(Cycle)) return true;
                visiting.Remove(key); completed.Add(key); return false;
            }
            _ = keys.Any(Cycle);
        }
        // The three host-owned confirmation steps are outside the user's graph allowance.
        if (total > catalog.Policy.MaxStepsTotal + (graph.Workflows.Any(w => w.Key == PlanningConfirmationGuards.Body) ? 3 : 0))
            findings.Add(new("STEP_LIMIT", "/workflows", "The workflow exceeds the configured step limit."));
        return findings;
    }

    // Relative graph paths keep confirmation wrappers and reordered steps out of intent context.
    internal static bool DependencyInScope(PlanningWorkflow workflow, string location, string source, string sourceLocation)
        => sourceLocation[..sourceLocation.LastIndexOf('/')] == location[..location.LastIndexOf('/')] ||
            location.StartsWith("/finally/", StringComparison.Ordinal) && workflow.Steps.Any(n => n.Key == source);

    internal static IReadOnlyList<string> EligibleDependencies(PlanningWorkflow workflow, string consumer)
    {
        var nodes = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToArray();
        var target = nodes.FirstOrDefault(n => n.Node.Key == consumer);
        if (target.Node is null || nodes.Select(n => n.Node.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length) return [];
        var edges = nodes.ToDictionary(n => n.Node.Key, n => n.Node.Dependencies.Concat(
            PlanningGraphCompiler.Enumerate([n.Node]).SelectMany(PlanningGraphTopology.References).Where(v => v.Kind is "output" or "present").Select(v => v.Source!)).ToArray(), StringComparer.Ordinal);
        return nodes.Where(n => n.Node.Key != consumer && !n.Node.Key.StartsWith("__planning_", StringComparison.Ordinal) &&
            DependencyInScope(workflow, target.Path, n.Node.Key, n.Path) && !Reaches(n.Node.Key, consumer, []))
            .Select(n => n.Node.Key).Order(StringComparer.Ordinal).ToArray();
        bool Reaches(string from, string to, HashSet<string> visited) => from == to || visited.Add(from) && edges.TryGetValue(from, out var next) && next.Any(n => Reaches(n, to, visited));
    }
}
