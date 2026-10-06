using System.Text;
using System.Net;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
public static class PlanningReviewFormatter
{
    internal static IEnumerable<PlanningValidationResult> Operations(PlanningSession state) =>
        PlanningGraphCompiler.Enumerate(state.Graph!.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)))
            .Where(n => n.CapabilityId is not null).Select(n =>
            {
                var contract = state.Catalog!.Capabilities.Single(c => c.Id == n.CapabilityId);
                return new PlanningValidationResult("operation:" + n.Key, "declared", (n.Purpose ?? n.Key) +
                    " — operation: " + contract.Id + (contract.Server is null ? "" : " (" + contract.Server + "/" + contract.Method + ")") +
                    "; contract effect: " + contract.EffectKind + ". " + (contract.ArtifactContract?.Locations is { Count: > 0 } ? "Declared resource locations checked; dynamic relationships still require execution evidence. " : "Resource lifecycle is not established by this contract; review cleanup and retained artifacts. ") + "Execution has not been observed; review the business requirements separately.", []);
            }).Concat(PlanningGraphCompiler.Enumerate(state.Graph!.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)))
                .Where(n => n.Input.Kind == "dynamic_mapping").Select(n => new PlanningValidationResult("mapping:" + n.Key, "declared",
                    n.Purpose + " — runtime extraction, at most two model attempts per invocation; cache hits still validate the complete result. " +
                    (PlanningGraphValidation.Member(n.Input, "each") is null ? "" : "Independent items preserve order and nesting; bounded examples generate one mapping, all items are validated, and the two attempts are shared. ") + "No execution evidence yet.", [])));

    public static string TaskDiagram(TaskPlan? plan)
    {
        var result = new StringBuilder("flowchart TD\n");
        if (plan is null) return result.ToString();
        Draw(plan.Root, "main");
        foreach (var group in plan.Groups) Draw(group.Body, group.Id);
        return result.ToString();
        void Draw(TaskScope scope, string name)
        {
            result.AppendLine("subgraph g" + PlanningGraphCompiler.Fingerprint(name)[..12] + "[\"" + Label(name) + "\"]");
            string? previous = null;
            foreach (var task in scope.Tasks.Concat(scope.Always))
            {
                var id = "t" + PlanningGraphCompiler.Fingerprint(task.Id)[..12];
                var businessResult = (task.Each is null ? "" : " · each " + task.Each.Input + " → " + task.Each.Output) +
                    (task.ResultType is null ? "" : " → " + string.Join(", ", task.ResultType.Fields.Select(f => f.Name + ": " + DescribeType(f.Type))));
                var transform = task.Kind == "transform" ? ", " + (task.Mode ?? "interpret") : "";
                var inputs = task.Kind == "transform" ? "; inputs: " + string.Join(", ", task.Inputs.Select(i => i.Name + " ← " + Describe(i.Value))) : "";
                result.AppendLine(id + "[\"" + Label(task.Id + ": " + task.Objective + " (" + task.Kind + transform + (task.Requires is null ? "" : ", required precondition") + (scope.Always.Contains(task) ? ", always" : "") + ")" + businessResult + inputs + (task.Requires is null ? "" : "; requires " + Describe(task.Requires))) + "\"]");
                if (previous is not null) result.AppendLine(previous + " --> " + id);
                previous = id;
                if (task.Body is not null) Draw(task.Body, task.Id + " body");
                if (task.Otherwise is not null) Draw(task.Otherwise, task.Id + " otherwise");
                for (var i = 0; i < task.Branches.Count; i++) Draw(task.Branches[i], task.Id + " branch " + i);
            }
            if (scope.Outputs.Count > 0)
            {
                var exports = "e" + PlanningGraphCompiler.Fingerprint(name)[..12];
                result.AppendLine(exports + "[\"" + Label("Exports: " + string.Join(", ", scope.Outputs.Select(o => o.Name + " ← " + Describe(o.Value)))) + "\"]");
                if (previous is not null) result.AppendLine(previous + " --> " + exports);
            }
            result.AppendLine("end");
        }
    }
    private static string DescribeType(TaskType type) => (type.Kind switch
    {
        "array" => "array<" + (type.Items is null ? "unknown" : DescribeType(type.Items)) + ">",
        "object" => "{" + string.Join(", ", type.Fields.Select(f => f.Name + ": " + DescribeType(f.Type))) + "}",
        _ => type.Kind
    }) + (type.Nullable ? "?" : "");

    private static string Describe(TaskValue value) => value.Kind switch
    {
        "input" => "input " + value.Source,
        "output" => value.Source + (value.Port is null ? "" : "." + value.Port),
        "present" => "present(" + value.Source + ")",
        "field" when value.Items.Count == 1 => Describe(value.Items[0]) + "." + value.Port,
        "object" => "{" + string.Join(", ", value.Members.Select(m => m.Name + ": " + Describe(m.Value))) + "}",
        "array" => "[" + string.Join(", ", value.Items.Select(Describe)) + "]",
        "json" => "json(" + string.Join(", ", value.Items.Select(Describe)) + ")",
        "boolean" => value.Boolean == true ? "true" : "false",
        "null" => "null", "string" => "\"" + value.Text + "\"",
        "predicate" => value.Predicate + "(" + string.Join(", ", value.Items.Select(Describe)) + ")",
        "arithmetic" => value.Text + "(" + string.Join(", ", value.Items.Select(Describe)) + ")",
        "number" => value.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "invalid number",
        _ => value.Kind
    };

    public static string Diagram(PlanningGraph? graph)
    {
        var result = new StringBuilder("flowchart TD\n");
        if (graph is null) return result.ToString();
        foreach (var workflow in graph.Workflows)
        {
            var prefix = "w" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..12];
            result.AppendLine("subgraph " + prefix + "[\"" + Label(workflow.Key) + "\"]");
            var start = prefix + "start"; result.AppendLine(start + "([\"Start\"])");
            Draw(workflow.Steps, [start]);
            if (workflow.Finally.Count > 0)
            {
                var cleanup = prefix + "finally";
                result.AppendLine(cleanup + "[\"Finally: success, failure or cancellation\"]");
                result.AppendLine(start + " -.-> " + cleanup); Draw(workflow.Finally, [cleanup]);
            }
            result.AppendLine("end");
            List<string> Draw(IEnumerable<PlanningNode> nodes, List<string> previous)
            {
                foreach (var node in nodes)
                {
                    var id = prefix + "n" + PlanningGraphCompiler.Fingerprint(node.Key)[..12];
                    var label = node.Key + ": " + (node.Input.Kind == "dynamic_mapping" ? "runtime extraction · max 2 model attempts" : node.Type) + (node.If is null ? "" : " (conditional)");
                    if (node.Type == "agent.run")
                    {
                        var calls = PlanningGraphValidation.Member(PlanningGraphValidation.Member(node.Input, "budget") ?? new(), "max_model_calls")?.Number;
                        var evidence = PlanningGraphValidation.Member(node.Input, "verification")?.Items.Count ?? 0;
                        label += $" · max {calls} calls · {evidence} evidence requirements";
                        label += " · workspace: " + PlanningGraphValidation.Member(node.Input, "workspace")?.Text;
                    }
                    result.AppendLine(id + (node.Type == "agent.run" ? "{{\"" : "[\"") + Label(label) + (node.Type == "agent.run" ? "\"}}" : "\"]"));
                    foreach (var source in previous) result.AppendLine(source + " --> " + id);
                    var tails = new List<string>();
                    if (node.Branches.Count > 0) foreach (var branch in node.Branches) tails.AddRange(Draw(branch.Steps, [id]));
                    else if (node.Cases.Count > 0)
                    {
                        foreach (var branch in node.Cases) tails.AddRange(Draw(branch.Steps, [id]));
                        tails.AddRange(Draw(node.Default, [id]));
                    }
                    else if (node.Steps.Count > 0) tails.AddRange(Draw(node.Steps, [id]));
                    if (node.Type.StartsWith("loop.", StringComparison.Ordinal))
                    { foreach (var tail in tails) result.AppendLine(tail + " -. repeat .-> " + id); tails = [id]; }
                    previous = tails.Count == 0 ? [id] : tails;
                }
                return previous;
            }
        }
        return result.ToString();
    }
    private static string Label(string value) => WebUtility.HtmlEncode(value).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal);
}
