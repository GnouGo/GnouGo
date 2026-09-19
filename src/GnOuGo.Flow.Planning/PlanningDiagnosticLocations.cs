using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningDiagnosticLocations
{
    internal static List<PlanningDiagnostic> ForIntent(PlanningSession state)
    {
        if (state.IntentPlan is null || state.Graph is null) return state.Diagnostics.ToList();
        var operations = IntentTraversal.Located(state.IntentPlan).ToArray();
        return state.Diagnostics.Select(Map).Distinct().ToList();
        PlanningDiagnostic Map(PlanningDiagnostic finding)
        {
            if (finding.ValidationStage is "intent" or "fixtures" || !finding.Location.StartsWith("/workflows/", StringComparison.Ordinal)) return finding;
            var parts = finding.Location.Split('/');
            if (parts.Length < 3 || !int.TryParse(parts[2], out var index) || index >= state.Graph.Workflows.Count) return Host(finding);
            var workflow = state.Graph.Workflows[index]; var root = "/workflows/" + index;
            var located = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally"))
                .Where(n => finding.Location == n.Path || finding.Location.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault();
            if (located.Node is { } node)
            {
                var match = operations.FirstOrDefault(o => o.Operation.Id == node.Key);
                if (match.Operation is null) match = operations.Where(o => node.Key.EndsWith("_" + o.Operation.Id, StringComparison.Ordinal)).OrderByDescending(o => o.Operation.Id.Length).FirstOrDefault();
                if (match.Operation is null) return Host(finding);
                if (finding.Code == "CONFIRMATION_REQUIRED") return Host(finding);
                return finding with { Location = match.Path, Message = "Operation '" + match.Operation.Id + "': " + finding.Message, ValidationStage = "intent" };
            }
            var intentRoot = workflow.Key is "main" or PlanningConfirmationGuards.Body ? "" : "/subflows/" + state.IntentPlan.Subflows.FindIndex(s => s.Name == workflow.Key);
            if (parts.Length >= 5 && parts[3] is "inputs" or "outputs")
                return finding with { Location = intentRoot + "/" + parts[3] + "/" + parts[4], ValidationStage = "intent" };
            return Host(finding);
        }
    }
    private static PlanningDiagnostic Host(PlanningDiagnostic finding) => finding with { Code = "PLANNING_HOST_CONTRACT", Message = "Generated host field: " + finding.Code + ". " + finding.Message };
    internal static PlanningDiagnostic TypedInput(PlanningDiagnostic finding, PlanningGraph graph)
    {
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{i}/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{i}/finally")))
            {
                var root = path + "/input/";
                if (!finding.Location.StartsWith(root, StringComparison.Ordinal)) continue;
                var suffix = finding.Location[root.Length..];
                if (suffix.StartsWith("members/", StringComparison.Ordinal) || suffix.StartsWith("items/", StringComparison.Ordinal)) return finding;
                var value = node.Input; var location = path + "/input";
                foreach (var part in suffix.Split('/'))
                {
                    if (value.Kind == "object")
                    {
                        var index = value.Members.FindIndex(m => m.Name == part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                        if (index < 0)
                        {
                            finding = finding with { Message = finding.Message + " Missing field '" + part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) + "' in this container." };
                            break;
                        }
                        location += "/members/" + index + "/value"; value = value.Members[index].Value;
                    }
                    else if (value.Kind == "array" && int.TryParse(part, out var index) && index >= 0 && index < value.Items.Count)
                    { location += "/items/" + index; value = value.Items[index]; }
                    else break;
                }
                return finding with { Location = location };
            }
        }
        return finding;
    }
}
