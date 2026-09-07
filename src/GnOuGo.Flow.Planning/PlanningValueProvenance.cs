using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Transparent value lineage across native containers and explicit workflow boundaries.</summary>
internal static class PlanningValueProvenance
{
    internal static bool Proves(PlanningWorkflow workflow, PlanningValue value, PlanningGraph graph,
        Func<PlanningNode, PlanningValue, bool> source, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.Ordinal);
        var key = workflow.Key + ":" + PlanningOutputBindings.Id(value);
        if (!visited.Add(key)) return false;
        try
        {
            if (value.Kind == "input")
            {
                var callers = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))
                    .Where(n => n.Type == "workflow.call" && PlanningGraphValidation.Member(n.Input, "ref")?.Source == workflow.Key)
                    .Select(n => (Workflow: w, Node: n))).ToArray();
                return callers.Length > 0 && callers.All(c => Select(PlanningGraphValidation.Member(c.Node.Input, "args"), new[] { value.Source! }.Concat(value.Path)) is { } argument
                    && Proves(c.Workflow, argument, graph, source, visited));
            }
            if (value.Kind != "output" || value.ResultChannel == "structured") return false;
            var producer = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source);
            if (producer is null) return false;
            if (source(producer, value)) return true;
            if (producer.Type is "set" or "assert.non_null")
            {
                var selected = Select(producer.Type == "set" ? producer.Input : PlanningGraphValidation.Member(producer.Input, "value"), value.Path);
                return selected is not null && Proves(workflow, selected, graph, source, visited);
            }
            if (producer.Type is "sequence" or "switch" && value.Path.Count > 0)
            {
                var children = producer.Steps.Concat(producer.Cases.SelectMany(c => c.Steps)).Concat(producer.Default)
                    .Where(n => n.Key == value.Path[0]).ToArray();
                if (children.Length != 1) return false;
                var child = children[0]; var path = value.Path.Skip(1).ToList();
                if (child.Type is "mcp.call" or "workflow.call")
                {
                    var envelope = child.Type == "mcp.call" ? "response" : "outputs";
                    if (path.Count == 0 || path[0] != envelope) return false;
                    path.RemoveAt(0);
                }
                return Proves(workflow, new() { Kind = "output", Source = child.Key, Path = path }, graph, source, visited);
            }
            if (producer.Type == "workflow.call" && value.Path.Count > 0)
            {
                var target = graph.Workflows.FirstOrDefault(w => w.Key == PlanningGraphValidation.Member(producer.Input, "ref")?.Source);
                var selected = Select(target?.Outputs.FirstOrDefault(p => p.Name == value.Path[0])?.Value, value.Path.Skip(1));
                return target is not null && selected is not null && Proves(target, selected, graph, source, visited);
            }
            return false;
        }
        finally { visited.Remove(key); }
    }

    internal static PlanningValue? Select(PlanningValue? source, IEnumerable<string> path)
    {
        var remaining = path.ToArray();
        for (var i = 0; i < remaining.Length && source is not null; i++)
        {
            if (source.Kind is "output" or "input") return new() { Kind = source.Kind, Source = source.Source, ResultChannel = source.ResultChannel, Path = source.Path.Concat(remaining.Skip(i)).ToList() };
            if (source.Kind == "object") source = PlanningGraphValidation.Member(source, remaining[i]);
            else if (source.Kind == "array" && int.TryParse(remaining[i], out var index) && index >= 0 && index < source.Items.Count) source = source.Items[index];
            else return null;
        }
        return source;
    }
}
