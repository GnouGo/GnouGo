using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Transparent value lineage across native containers and explicit workflow boundaries.</summary>
internal static class PlanningValueProvenance
{
    internal static bool Proves(PlanningWorkflow workflow, PlanningValue value, PlanningGraph graph,
        Func<PlanningNode, PlanningValue, bool> source, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.Ordinal);
        var key = workflow.Key + ":" + PlanningBindingIdentity.Id(value);
        if (!visited.Add(key)) return false;
        try
        {
            if (value.Kind == "artifact_collection")
            {
                var loop = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source && n.Type is "loop.sequential" or "loop.parallel");
                var child = loop?.Steps.SingleOrDefault(n => n.Key == value.Path.FirstOrDefault());
                return child is { Type: "mcp.call", If: null } && value.Path.Count >= 3 && value.Path[1] == "response" && !child.OnError.Any(h => h.Action == "continue") &&
                    Proves(workflow, new() { Kind = "output", Source = child.Key, Path = value.Path.Skip(2).ToList() }, graph, source, visited);
            }
            if (value.Kind == "loop_item")
            {
                var loop = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source && n.Type is "loop.sequential" or "loop.parallel");
                var items = loop is null ? null : PlanningGraphValidation.Member(loop.Input, "items");
                if (items is { Kind: "flatten", Items.Count: 1 } && items.Items[0] is { Kind: "array" } groups && groups.Items.All(v => v.Kind == "array"))
                    items = new() { Kind = "array", Items = groups.Items.SelectMany(v => v.Items).ToList() };
                return items is not null && ProvesItems(workflow, items, value.Path, graph, source, visited);
            }
            if (value.Kind == "input")
            {
                if (value.Source is null) return false;
                var callers = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))
                    .Where(n => n.Type == "workflow.call" && PlanningGraphValidation.Member(n.Input, "ref")?.Source == workflow.Key)
                    .Select(n => (Workflow: w, Node: n))).ToArray();
                return callers.Length > 0 && callers.All(c => Select(PlanningGraphValidation.Member(c.Node.Input, "args"), new[] { value.Source! }.Concat(value.Path)) is { } argument
                    && Proves(c.Workflow, argument, graph, source, visited));
            }
            if (value.Kind != "output" || value.ResultChannel == "structured") return false;
            var producer = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source);
            if (producer is null) return false;
            if (value.ResultChannel == "envelope")
            {
                if (producer.Type != "mcp.call" || value.Path.FirstOrDefault() != "response") return false;
                return Proves(workflow, new() { Kind = "output", Source = value.Source, Path = value.Path.Skip(1).ToList() }, graph, source, visited);
            }
            if (source(producer, value)) return true;
            if (producer.Type == "set" && producer.Input.Kind == "projection")
            {
                if (producer.OutputSchema is null || value.Path.FirstOrDefault() != "value" || producer.OnError.Any(h => h.Action == "continue")) return false;
                var input = PlanningGraphValidation.Member(producer.Input, "value");
                var each = PlanningGraphValidation.Member(producer.Input, "each");
                if (each is not null && each is not { Kind: "boolean", Boolean: not null }) return false;
                var remaining = value.Path.Skip(1);
                if (each?.Boolean == true)
                {
                    if (value.Path.Count < 2 || !int.TryParse(value.Path[1], out var index) || index < 0) return false;
                    input = Select(input, [value.Path[1]]); remaining = value.Path.Skip(2);
                }
                var paths = PlanningGraphValidation.Member(producer.Input, "paths");
                // Every reachable alternative must preserve origin. Checked types alone prove nothing.
                return paths is { Kind: "array", Items.Count: > 0 } && paths.Items.All(path =>
                    path.Kind == "array" && path.Items.All(p => p.Kind == "string" && p.Text is not null) &&
                    Select(input, path.Items.Select(p => p.Text!).Concat(remaining)) is { } projected && Proves(workflow, projected, graph, source, visited));
            }
            if (producer.Type == "set")
            {
                var selected = Select(producer.Input, value.Path);
                return selected is not null && Proves(workflow, selected, graph, source, visited);
            }
            if (producer.Type is "sequence" or "switch" or "parallel" && value.Path.Count > 0)
            {
                var path = value.Path.ToList();
                IEnumerable<PlanningNode> candidates = producer.Steps.Concat(producer.Cases.SelectMany(c => c.Steps)).Concat(producer.Default);
                if (producer.Type == "parallel")
                {
                    if (path.Count < 3 || path[0] != "branches" || !int.TryParse(path[1], out var index) || index < 0 || index >= producer.Branches.Count) return false;
                    candidates = producer.Branches[index].Steps; path = path.Skip(2).ToList();
                }
                var children = candidates.Where(n => n.Key == path[0]).ToArray();
                if (children.Length != 1) return false;
                var child = children[0]; path = path.Skip(1).ToList();
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

    // A selected identity is not evidence. Every possible source record must
    // establish the requested lineage, including through captured collections.
    private static bool ProvesItems(PlanningWorkflow workflow, PlanningValue items, IReadOnlyList<string> path, PlanningGraph graph,
        Func<PlanningNode, PlanningValue, bool> source, HashSet<string> visited)
    {
        var key = "items:" + workflow.Key + ":" + PlanningBindingIdentity.Id(items) + ":" + string.Join('/', path);
        if (!visited.Add(key)) return false;
        try
        {
            if (items.Kind == "array") return items.Items.Count > 0 && items.Items.All(item =>
                Select(item, path) is { } selected && Proves(workflow, selected, graph, source, visited));
            if (items is { Kind: "lookup", Items.Count: 2 }) return ProvesItems(workflow, items.Items[0], path, graph, source, visited);
            if (items is { Kind: "input", Source: not null })
            {
                var callers = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))
                    .Where(n => n.Type == "workflow.call" && PlanningGraphValidation.Member(n.Input, "ref")?.Source == workflow.Key)
                    .Select(n => (Workflow: w, Node: n))).ToArray();
                return callers.Length > 0 && callers.All(c => Select(PlanningGraphValidation.Member(c.Node.Input, "args"), new[] { items.Source }.Concat(items.Path)) is { } argument &&
                    ProvesItems(c.Workflow, argument, path, graph, source, visited));
            }
            if (items.Kind != "output" || items.ResultChannel == "structured") return false;
            var producer = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == items.Source);
            if (producer is null || producer.OnError.Any(h => h.Action == "continue")) return false;
            if (producer.Type == "set")
            {
                if (producer.Input.Kind != "projection") return Select(producer.Input, items.Path) is { } selected && ProvesItems(workflow, selected, path, graph, source, visited);
                if (items.Path.FirstOrDefault() != "value" || PlanningGraphValidation.Member(producer.Input, "each")?.Boolean == true) return false;
                var input = PlanningGraphValidation.Member(producer.Input, "value");
                var paths = PlanningGraphValidation.Member(producer.Input, "paths");
                return paths is { Kind: "array", Items.Count: > 0 } && paths.Items.All(p =>
                    p.Kind == "array" && p.Items.All(v => v.Kind == "string" && v.Text is not null) &&
                    Select(input, p.Items.Select(v => v.Text!).Concat(items.Path.Skip(1))) is { } selected && ProvesItems(workflow, selected, path, graph, source, visited));
            }
            if (producer.Type != "workflow.call" || items.Path.Count == 0) return false;
            var target = graph.Workflows.FirstOrDefault(w => w.Key == PlanningGraphValidation.Member(producer.Input, "ref")?.Source);
            return target is not null && Select(target.Outputs.FirstOrDefault(o => o.Name == items.Path[0])?.Value, items.Path.Skip(1)) is { } output &&
                ProvesItems(target, output, path, graph, source, visited);
        }
        finally { visited.Remove(key); }
    }

    internal static PlanningValue? Select(PlanningValue? source, IEnumerable<string> path)
    {
        var remaining = path.ToArray();
        for (var i = 0; i < remaining.Length && source is not null; i++)
        {
            if (source is { Kind: "flatten", Items.Count: 1 } && source.Items[0] is { Kind: "array" } groups && groups.Items.All(v => v.Kind == "array"))
                source = new() { Kind = "array", Items = groups.Items.SelectMany(v => v.Items).ToList() };
            if (source.Kind is "output" or "input" or "loop_item") return new() { Kind = source.Kind, Source = source.Source, ResultChannel = source.ResultChannel, Path = source.Path.Concat(remaining.Skip(i)).ToList() };
            if (source.Kind == "object") source = PlanningGraphValidation.Member(source, remaining[i]);
            else if (source.Kind == "array" && int.TryParse(remaining[i], out var index) && index >= 0 && index < source.Items.Count) source = source.Items[index];
            else return null;
        }
        return source;
    }
}
