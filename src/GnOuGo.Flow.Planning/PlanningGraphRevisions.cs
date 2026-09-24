using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>One graph repair boundary; unaffected stages remain immutable.</summary>
internal static class PlanningGraphRevisions
{
    internal static IReadOnlyList<string> Scope(PlanningGraph graph, IReadOnlyList<PlanningDiagnostic> findings)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var prefix = "/workflows/" + wi;
            var local = findings.Where(f => f.Required && (f.Location == prefix || f.Location.StartsWith(prefix + "/", StringComparison.Ordinal))).ToArray();
            if (local.Length == 0) continue;
            var roots = workflow.Steps.Concat(workflow.Finally).ToArray();
            foreach (var finding in local)
            {
                var addressed = roots.FirstOrDefault(n => finding.Location.Contains("/stages/" + n.Key, StringComparison.Ordinal));
                foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, prefix + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, prefix + "/finally")))
                    if (finding.Location == path || finding.Location.StartsWith(path + "/", StringComparison.Ordinal))
                        addressed = roots.FirstOrDefault(root => PlanningGraphCompiler.Enumerate([root]).Contains(node));
                if (addressed is null)
                {
                    foreach (var root in roots) result.Add(workflow.Key + "/" + root.Key);
                    result.Add(workflow.Key + "/$interface");
                }
                else result.Add(workflow.Key + "/" + addressed.Key);
            }
        }
        if (result.Count == 0)
            foreach (var workflow in graph.Workflows)
            {
                result.Add(workflow.Key + "/$interface");
                foreach (var node in workflow.Steps.Concat(workflow.Finally)) result.Add(workflow.Key + "/" + node.Key);
            }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var workflow in graph.Workflows)
            {
                var roots = workflow.Steps.Concat(workflow.Finally).ToArray();
                var affected = roots.Where(n => result.Contains(workflow.Key + "/" + n.Key)).SelectMany(n => PlanningGraphCompiler.Enumerate([n])).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
                bool UsesAffected(PlanningValue value) => PlanningGraphTopology.ReferencedStages(value).Any(affected.Contains);
                foreach (var root in roots)
                    if (PlanningGraphCompiler.Enumerate([root]).Any(n => n.Dependencies.Any(affected.Contains) || PlanningGraphTopology.Values(n).Any(UsesAffected) ||
                        n.Type == "workflow.call" && PlanningGraphValidation.Member(n.Input, "ref")?.Source is { } target && result.Contains(target + "/$interface")))
                        changed |= result.Add(workflow.Key + "/" + root.Key);
                if (workflow.Outputs.Any(o => UsesAffected(o.Value))) changed |= result.Add(workflow.Key + "/$interface");
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningGraph? previous, PlanningGraph candidate, IReadOnlyList<string> scope)
    {
        if (previous is null || scope.Count == 0) yield break;
        if (previous.Entrypoint != candidate.Entrypoint) yield return new("REVISION_SCOPE_CHANGED", "/entrypoint", "Preserve the entrypoint.");
        foreach (var workflow in previous.Workflows)
        {
            var next = candidate.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
            if (next is null) { yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Preserve existing workflows."); continue; }
            var oldJson = JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow)!;
            var newJson = JsonSerializer.SerializeToNode(next, PlanningJsonContext.Default.PlanningWorkflow)!;
            if (!scope.Contains(workflow.Key + "/$interface") && (!JsonNode.DeepEquals(oldJson["inputs"], newJson["inputs"]) || !JsonNode.DeepEquals(oldJson["outputs"], newJson["outputs"])))
                yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Preserve unaffected workflow interfaces.");
            foreach (var (before, after) in new[] { (workflow.Steps, next.Steps), (workflow.Finally, next.Finally) })
            {
                var frozen = before.Where(n => !scope.Contains(workflow.Key + "/" + n.Key)).Select(n => n.Key).ToArray();
                if (!frozen.SequenceEqual(after.Where(n => frozen.Contains(n.Key)).Select(n => n.Key)))
                    yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Preserve the order of unaffected stages.");
                if (!scope.Any(s => s.StartsWith(workflow.Key + "/", StringComparison.Ordinal)) &&
                    !before.Select(n => n.Key).SequenceEqual(after.Select(n => n.Key)))
                    yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Do not add work to an unaffected workflow.");
            }
            foreach (var node in workflow.Steps.Concat(workflow.Finally).Where(n => !scope.Contains(workflow.Key + "/" + n.Key)))
            {
                var replacement = (workflow.Finally.Contains(node) ? next.Finally : next.Steps).FirstOrDefault(n => n.Key == node.Key);
                if (replacement is null || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(node, PlanningJsonContext.Default.PlanningNode), JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.PlanningNode)))
                    yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Preserve unaffected stage " + workflow.Key + "/" + node.Key + ".");
            }
        }
        if (candidate.Workflows.Any(w => previous.Workflows.All(p => p.Key != w.Key)))
            yield return new("REVISION_SCOPE_CHANGED", "/workflows", "Technical repair cannot introduce unrelated workflows.");
    }
}
