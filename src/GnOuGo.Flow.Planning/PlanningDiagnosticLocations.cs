using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningDiagnosticLocations
{
    internal static List<PlanningDiagnostic> ForIntent(PlanningSession state)
    {
        if (state.IntentPlan is null || state.Graph is null) return state.Diagnostics.ToList();
        var operations = IntentTraversal.Located(state.IntentPlan).ToArray();
        var mapped = state.Diagnostics.Select(Map).Distinct().ToList();
        // Keep every decision in session diagnostics; repair context folds only known producer consequences into their root.
        foreach (var root in mapped.Where(d => d.Code is "SCHEMA_INVALID" or "STRUCTURED_OUTPUT_INVALID").ToArray())
        {
            var operation = operations.Where(o => root.Location == o.Path || root.Location.StartsWith(o.Path + "/", StringComparison.Ordinal)).OrderByDescending(o => o.Path.Length).FirstOrDefault().Operation;
            if (operation is null) continue;
            var dependent = mapped.Where(d => d.Rule == "producer:" + operation.Id && d != root).ToArray();
            if (dependent.Length == 0) continue;
            mapped.RemoveAll(dependent.Contains);
            var index = mapped.IndexOf(root);
            mapped[index] = root with { Message = root.Message + " Dependent intent locations: " + string.Join(", ", dependent.Select(d => d.Location).Distinct()) };
        }
        return mapped;
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
                var owned = operations.Where(o => IntentTraversal.GraphOwner(state.IntentPlan, o.Path) == (workflow.Key == PlanningConfirmationGuards.Body ? "main" : workflow.Key)).ToArray();
                var match = owned.FirstOrDefault(o => o.Operation.Id == node.Key);
                if (match.Operation is null) match = owned.Where(o => node.Key.EndsWith("_" + o.Operation.Id, StringComparison.Ordinal)).OrderByDescending(o => o.Operation.Id.Length).FirstOrDefault();
                if (match.Operation is null || finding.Code == "CONFIRMATION_REQUIRED") return Host(finding);
                var path = match.Path;
                var normalized = TypedInput(finding, state.Graph);
                var suffix = normalized.Location[located.Path.Length..];
                if (suffix.StartsWith("/input", StringComparison.Ordinal))
                {
                    if (match.Operation is InvokeIntentOperation invoke)
                    {
                        var request = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request")! : node.Input;
                        var requestPath = node.Type == "mcp.call" ? "/input/members/" + node.Input.Members.FindIndex(m => m.Name == "request") + "/value" : "/input";
                        if (!suffix.StartsWith(requestPath, StringComparison.Ordinal)) return Host(finding);
                        var tail = suffix[requestPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                        path += "/arguments";
                        if (tail.Length >= 3 && tail[0] == "members" && int.TryParse(tail[1], out var mi) && mi < request.Members.Count)
                        {
                            var member = request.Members[mi]; var capability = state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability);
                            if (capability?.RequestBindings.Any(b => b.Path == "/" + PlanningFieldPaths.Escape(member.Name)) == true) return Host(finding);
                            var ii = invoke.Arguments.FindIndex(m => m.Name == member.Name);
                            if (ii < 0) return Edit(path, " Missing argument '" + member.Name + "'.");
                            path += "/" + ii + "/value";
                            path = ValuePath(member.Value, invoke.Arguments[ii].Value, tail.Skip(3).ToArray(), path);
                        }
                    }
                    else if (match.Operation is CalculateIntentOperation calculation && suffix.StartsWith("/input/members/0/value", StringComparison.Ordinal))
                        path = ValuePath(node.Input.Members[0].Value, calculation.Value, suffix["/input/members/0/value".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries), path + "/value");
                    else if (match.Operation is TransformIntentOperation) return Edit(path);
                    else if (match.Operation is EachIntentOperation && node.Type is "loop.parallel" or "loop.sequential") path += "/items";
                    else if (node.Type == "set" && finding.Code.StartsWith("COMPUTATION_", StringComparison.Ordinal)) return Host(finding);
                }
                else if (suffix.StartsWith("/if", StringComparison.Ordinal) && match.Operation.When is not null) path += "/when";
                else if (suffix.StartsWith("/expr", StringComparison.Ordinal) && match.Operation is ChooseIntentOperation) path += "/condition";
                else if (suffix.Contains("Schema", StringComparison.Ordinal) || suffix.StartsWith("/structuredOutput", StringComparison.Ordinal))
                    path += match.Operation switch { CalculateIntentOperation { ResultType: not null } or TransformIntentOperation { ResultType: not null } => "/resultType", CalculateIntentOperation => "/value", _ => "" };
                return Edit(path);
                PlanningDiagnostic Edit(string location, string extra = "") => finding with { Location = location, Message = "Operation '" + match.Operation.Id + "': " + finding.Message + extra, ValidationStage = "intent" };
            }
            var block = IntentTraversal.Blocks(state.IntentPlan).FirstOrDefault(b => b.Workflow == workflow.Key);
            if (block.Block is not null) return finding with { Location = block.Path + "/result", ValidationStage = "intent" };
            var intentRoot = workflow.Key is "main" or PlanningConfirmationGuards.Body ? "" : "/subflows/" + state.IntentPlan.Subflows.FindIndex(s => s.Name == workflow.Key);
            if (parts.Length >= 5 && parts[3] is "inputs" or "outputs")
                return finding with { Location = intentRoot + "/" + parts[3] + "/" + parts[4], ValidationStage = "intent" };
            return Host(finding);
        }
    }
    private static string ValuePath(PlanningValue graph, IntentValue intent, string[] segments, string path)
    {
        while (segments.Length >= 3 && segments[0] == "members" && int.TryParse(segments[1], out var index) && index < graph.Members.Count)
        {
            var member = graph.Members[index]; var target = intent.Members.FindIndex(m => m.Name == member.Name);
            if (target < 0) return path;
            graph = member.Value; intent = intent.Members[target].Value; path += "/members/" + target + "/value"; segments = segments[3..];
        }
        if (segments.Length >= 2 && segments[0] == "items" && int.TryParse(segments[1], out var item) && item < graph.Items.Count && item < intent.Items.Count)
            return ValuePath(graph.Items[item], intent.Items[item], segments[2..], path + "/items/" + item);
        return path;
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
