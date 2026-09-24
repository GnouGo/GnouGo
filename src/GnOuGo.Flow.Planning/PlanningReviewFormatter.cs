using System.Text;
using System.Net;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
public static class PlanningReviewFormatter
{
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
                    var label = node.Key + ": " + node.Type + (node.If is null ? "" : " (conditional)");
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
