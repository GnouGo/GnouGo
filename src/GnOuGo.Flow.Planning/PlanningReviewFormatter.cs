using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Renders the same typed graph that is lowered to executable YAML.</summary>
public static class PlanningReviewFormatter
{
    public static IReadOnlyList<string> Diff(PlanningGraph? before, PlanningGraph? after)
    {
        if (before is null || after is null) return [];
        var changes = new List<string>();
        foreach (var workflow in after.Workflows)
        {
            var prior = before.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
            if (prior is null) { changes.Add("Added workflow: " + workflow.Purpose); continue; }
            var oldNodes = PlanningGraphCompiler.Enumerate(prior.Steps.Concat(prior.Finally)).ToDictionary(n => n.Key, StringComparer.Ordinal);
            var newNodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToDictionary(n => n.Key, StringComparer.Ordinal);
            foreach (var node in newNodes.Values)
            {
                var label = workflow.Purpose + " / " + (node.Purpose.Length == 0 ? node.Key : node.Purpose);
                if (!oldNodes.TryGetValue(node.Key, out var old)) changes.Add("Added step: " + label);
                else if (JsonSerializer.Serialize(old, PlanningJsonContext.Default.PlanningNode) != JsonSerializer.Serialize(node, PlanningJsonContext.Default.PlanningNode))
                    changes.Add("Changed step: " + label + (JsonSerializer.Serialize(old.If, PlanningJsonContext.Default.PlanningValue) != JsonSerializer.Serialize(node.If, PlanningJsonContext.Default.PlanningValue) ? " (condition changed)" : " (implementation or bindings changed)"));
            }
            foreach (var removed in oldNodes.Values.Where(n => !newNodes.ContainsKey(n.Key))) changes.Add("Removed step: " + workflow.Purpose + " / " + (removed.Purpose.Length == 0 ? removed.Key : removed.Purpose));
            if (!prior.Inputs.Select(p => JsonSerializer.Serialize(p, PlanningJsonContext.Default.PlanningPort)).SequenceEqual(workflow.Inputs.Select(p => JsonSerializer.Serialize(p, PlanningJsonContext.Default.PlanningPort))) ||
                !prior.Outputs.Select(p => JsonSerializer.Serialize(p, PlanningJsonContext.Default.PlanningOutput)).SequenceEqual(workflow.Outputs.Select(p => JsonSerializer.Serialize(p, PlanningJsonContext.Default.PlanningOutput))))
                changes.Add("Input or output contract changed: " + workflow.Purpose);
        }
        changes.AddRange(before.Workflows.Where(w => !after.Workflows.Any(n => n.Key == w.Key)).Select(w => "Removed workflow: " + w.Purpose));
        return changes;
    }

    public static string Diagram(PlanningGraph? graph, PlanningPreparation? preparation = null, PlanningGraph? previous = null)
    {
        if (graph is null) return "";
        var text = new StringBuilder("flowchart TD\n");
        foreach (var workflow in graph.Workflows)
        {
            var prefix = "w" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..12];
            text.Append("subgraph ").Append(prefix).Append("[\"").Append(Label(workflow.Purpose)).AppendLine("\"]");
            text.Append(prefix).Append("_inputs[\"Inputs: ").Append(Label(string.Join(", ", workflow.Inputs.Select(p => p.Name)))).AppendLine("\"]");
            text.Append(prefix).Append("_outputs[\"Outputs: ").Append(Label(string.Join(", ", workflow.Outputs.Select(p => p.Name)))).AppendLine("\"]");
            var lastStep = RenderSteps(text, workflow.Steps, prefix, preparation);
            if (workflow.Steps.Count > 0)
            {
                text.Append(prefix).Append("_inputs --> ").AppendLine(EntryId(prefix, workflow.Steps[0]));
                text.Append(lastStep).Append(" --> ").Append(prefix).AppendLine("_outputs");
            }
            if (workflow.Finally.Count > 0)
            {
                text.Append("subgraph ").Append(prefix).AppendLine("_finally[\"Finalization\"]");
                RenderSteps(text, workflow.Finally, prefix, preparation);
                text.AppendLine("end");
                text.Append(prefix).Append("_outputs -. success, failure, cancellation .-> ").AppendLine(EntryId(prefix, workflow.Finally[0]));
            }
            text.AppendLine("end");
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                var id = prefix + "_" + PlanningGraphCompiler.Fingerprint(node.Key)[..12];
                var reference = node.Type == "workflow.call" ? node.Input.Members.FirstOrDefault(m => m.Name == "ref")?.Value : null;
                if (reference is { Kind: "workflow", Source: { } target })
                    text.Append(id).Append(" -. calls .-> w").Append(PlanningGraphCompiler.Fingerprint(target)[..12]).AppendLine("_inputs");
                var old = previous?.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
                var prior = old is null ? null : PlanningGraphCompiler.Enumerate(old.Steps.Concat(old.Finally)).FirstOrDefault(n => n.Key == node.Key);
                if (previous is not null && (prior is null || JsonSerializer.Serialize(prior, PlanningJsonContext.Default.PlanningNode) != JsonSerializer.Serialize(node, PlanningJsonContext.Default.PlanningNode)))
                    text.Append("style ").Append(id).AppendLine(" stroke:#bf8700,stroke-width:3px");
            }
        }
        return text.ToString();
    }

    public static IReadOnlyList<string> BehaviorDetails(PlanningGraph? graph)
    {
        if (graph is null) return [];
        return graph.Workflows.Select(w => w.Purpose + " — inputs: " + string.Join(", ", w.Inputs.Select(p => p.Name)) +
            "; outputs: " + string.Join(", ", w.Outputs.Select(p => p.Name)) + "; cleanup steps: " + w.Finally.Count).ToArray();
    }

    private static string? RenderSteps(StringBuilder text, List<PlanningNode> steps, string prefix, PlanningPreparation? preparation)
    {
        string? previous = null;
        foreach (var node in steps)
        {
            var id = prefix + "_" + PlanningGraphCompiler.Fingerprint(node.Key)[..12];
            var effect = preparation?.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.EffectKind;
            var marker = node.Type == "mcp.call" ? "External " + (effect ?? "effect") + ": " : node.Type == "human.input" ? "Ask during execution: " : "";
            text.Append(id).Append("[\"").Append(Label(marker + (node.Purpose.Length == 0 ? node.Type : node.Purpose))).AppendLine("\"]");
            if (previous is not null) text.Append(previous).Append(" --> ").AppendLine(EntryId(prefix, node));
            if (node.If is { } condition)
            {
                text.Append(id).Append("_condition{\"If ").Append(Label(ValueLabel(condition))).AppendLine("\"}");
                text.Append(id).Append("_condition -->|Yes| ").AppendLine(id);
            }
            previous = id;
            var groups = new List<(string Label, List<PlanningNode> Steps)>();
            if (node.Steps.Count > 0) groups.Add((node.Type.StartsWith("loop.", StringComparison.Ordinal) ? "Repeat" : "Steps", node.Steps));
            groups.AddRange(node.Branches.Select((b, i) => ("Parallel " + (i + 1), b.Steps)));
            groups.AddRange(node.Cases.Select((c, i) => (c.Value ?? "Condition " + (i + 1), c.Steps)));
            if (node.Default.Count > 0 || node.Type == "switch") groups.Add(("Otherwise", node.Default));
            if (groups.Count > 0) text.Append(id).AppendLine("_join((Continue))");
            foreach (var group in groups)
            {
                if (group.Steps.Count == 0)
                {
                    text.Append(id).Append(" -->|\"").Append(Label(group.Label + ": no action")).Append("\"| ").Append(id).AppendLine("_join");
                    continue;
                }
                var terminal = RenderSteps(text, group.Steps, prefix, preparation);
                text.Append(id).Append(" -->|\"").Append(Label(group.Label)).Append("\"| ").AppendLine(EntryId(prefix, group.Steps[0]));
                text.Append(terminal).Append(" --> ").Append(id).AppendLine("_join");
            }
            if (groups.Count > 0) previous = id + "_join";
            if (node.Type.StartsWith("loop.", StringComparison.Ordinal) && groups.Count > 0)
                text.Append(id).Append(" -->|No iterations| ").AppendLine(previous);
            if (node.If is not null)
            {
                text.Append(id).AppendLine("_after_condition((Continue))");
                text.Append(previous).Append(" --> ").Append(id).AppendLine("_after_condition");
                text.Append(id).Append("_condition -->|No action| ").Append(id).AppendLine("_after_condition");
                previous = id + "_after_condition";
            }
        }
        return previous;
    }
    private static string EntryId(string prefix, PlanningNode node) => prefix + "_" + PlanningGraphCompiler.Fingerprint(node.Key)[..12] + (node.If is null ? "" : "_condition");
    private static string ValueLabel(PlanningValue value) => value.Kind switch
    {
        "input" => "input " + value.Source + (value.Path.Count == 0 ? "" : "." + string.Join('.', value.Path)),
        "output" => value.Source + "." + string.Join('.', value.Path),
        "boolean" => value.Boolean == true ? "true" : "false",
        _ => value.Text ?? "the declared condition holds"
    };
    private static string Label(string value) => HtmlEncoder.Default.Encode(value.Replace('\n', ' ').Replace('\r', ' ')[..Math.Min(value.Length, 160)]).Replace("`", "&#96;", StringComparison.Ordinal);
}
