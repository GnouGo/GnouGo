using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningDiagnosticLocations
{
    internal static List<PlanningDiagnostic> ForIntent(PlanningSession state)
    {
        if (state.GroundedPlan is null || state.Graph is null) return state.Diagnostics.ToList();
        var operations = GroundedTraversal.Located(state.GroundedPlan).ToArray();
        var mapped = state.Diagnostics.Select(d => Map(d)).Distinct().ToList();
        // Keep every decision in session diagnostics; replanning context folds only known producer consequences into their root.
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
        PlanningDiagnostic Map(PlanningDiagnostic finding, HashSet<string>? captures = null)
        {
            if (finding.ValidationStage is "intent" or "fixtures" || !finding.Location.StartsWith("/workflows/", StringComparison.Ordinal)) return finding;
            var parts = finding.Location.Split('/');
            if (parts.Length < 3 || !int.TryParse(parts[2], out var index) || index >= state.Graph.Workflows.Count) return Host(finding);
            var workflow = state.Graph.Workflows[index]; var root = "/workflows/" + index;
            var located = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally"))
                .Where(n => finding.Location == n.Path || finding.Location.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault();
            if (located.Node is { } node)
            {
                var owned = operations.Where(o => GroundedTraversal.GraphOwner(state.GroundedPlan, o.Path) == (workflow.Key == PlanningConfirmationGuards.Body ? "main" : workflow.Key)).ToArray();
                var match = owned.FirstOrDefault(o => o.Operation.Id == node.Key);
                if (match.Operation is null) match = owned.Where(o => node.Key.EndsWith("_" + o.Operation.Id, StringComparison.Ordinal)).OrderByDescending(o => o.Operation.Id.Length).FirstOrDefault();
                if (match.Operation is null || finding.Code == "CONFIRMATION_REQUIRED") return Host(finding);
                var path = match.Path;
                var normalized = TypedInput(finding, state.Graph);
                var suffix = normalized.Location[located.Path.Length..];
                if (suffix.StartsWith("/input", StringComparison.Ordinal))
                {
                    if (match.Operation is InvokeGroundedOperation invoke)
                    {
                        var request = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request")! : node.Input;
                        var requestPath = node.Type == "mcp.call" ? "/input/members/" + node.Input.Members.FindIndex(m => m.Name == "request") + "/value" : "/input";
                        if (!suffix.StartsWith(requestPath, StringComparison.Ordinal)) return Host(finding);
                        var tail = suffix[requestPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                        path += "/arguments";
                        if (tail.Length >= 3 && tail[0] == "members" && int.TryParse(tail[1], out var mi) && mi < request.Members.Count)
                        {
                            var member = request.Members[mi]; var capability = state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability);
                            var pointer = ValuePointer(request, tail);
                            if (capability?.RequestBindings.Any(b => pointer == b.Path || pointer.StartsWith(b.Path + "/", StringComparison.Ordinal)) == true) return Host(finding);
                            var ii = invoke.Arguments.FindIndex(m => m.Name == member.Name);
                            if (ii < 0) return Edit(path, " Missing argument '" + member.Name + "'.");
                            path += "/" + ii + "/value";
                            path = ValuePath(member.Value, invoke.Arguments[ii].Value, tail.Skip(3).ToArray(), path);
                        }
                    }
                    else if (match.Operation is CalculateGroundedOperation calculation && suffix.StartsWith("/input/members/0/value", StringComparison.Ordinal))
                        path = ValuePath(node.Input.Members[0].Value, calculation.Value, suffix["/input/members/0/value".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries), path + "/value");
                    else if (match.Operation is TransformGroundedOperation) return Edit(path);
                    else if (match.Operation is EachGroundedOperation && node.Type is "loop.parallel" or "loop.sequential") path += "/items";
                    else if (node.Type == "set" && finding.Code.StartsWith("COMPUTATION_", StringComparison.Ordinal)) return Host(finding);
                }
                else if (suffix.StartsWith("/if", StringComparison.Ordinal) && match.Operation.When is not null) path += "/when";
                else if (suffix.StartsWith("/expr", StringComparison.Ordinal) && match.Operation is ChooseGroundedOperation) path += "/condition";
                else if (suffix.Contains("Schema", StringComparison.Ordinal) || suffix.StartsWith("/structuredOutput", StringComparison.Ordinal))
                    path += match.Operation switch { TransformGroundedOperation { ResultType: not null } => "/resultType", CalculateGroundedOperation => "/value", _ => "" };
                return Edit(path);
                PlanningDiagnostic Edit(string location, string extra = "") => finding with { Location = location, Message = "Operation '" + match.Operation.Id + "': " + finding.Message + extra, ValidationStage = "intent" };
            }
            var block = GroundedTraversal.Blocks(state.GroundedPlan).FirstOrDefault(b => b.Workflow == workflow.Key);
            if (parts.Length < 5 || !int.TryParse(parts[4], out var portIndex) || portIndex < 0) return Host(finding);
            if (block.Block is not null)
            {
                if (parts[3] == "inputs" && parts.ElementAtOrDefault(5) == "schema" && portIndex < workflow.Inputs.Count)
                    return CaptureSource(finding, workflow, workflow.Inputs[portIndex].Name, captures ?? new(StringComparer.Ordinal));
                if (parts[3] != "outputs" || portIndex >= workflow.Outputs.Count) return Host(finding);
                var path = block.Path + "/result";
                if (parts.ElementAtOrDefault(5) == "value") path = ValuePath(workflow.Outputs[portIndex].Value, block.Block.Result, parts[6..], path);
                return finding with { Location = path, ValidationStage = "intent" };
            }
            var main = workflow.Key is "main" or PlanningConfirmationGuards.Body;
            var subflow = state.GroundedPlan.Subflows.FindIndex(s => s.Name == workflow.Key);
            if (!main && subflow < 0) return Host(finding);
            var intentRoot = main ? "" : "/subflows/" + subflow;
            if (parts[3] == "inputs" && portIndex < workflow.Inputs.Count)
            {
                var port = workflow.Inputs[portIndex]; var inputs = main ? state.GroundedPlan.Inputs : state.GroundedPlan.Subflows[subflow].Inputs;
                var target = inputs.FindIndex(p => p.Name == port.Name);
                if (target < 0) return Host(finding);
                var path = intentRoot + "/inputs/" + target;
                if (parts.ElementAtOrDefault(5) == "default")
                {
                    if (port.Default is null || inputs[target].Default is not { } value) return Host(finding);
                    path = ValuePath(port.Default, value, parts[6..], path + "/default");
                }
                return finding with { Location = path, Message = "Input '" + port.Name + "': " + finding.Message, ValidationStage = "intent" };
            }
            if (parts[3] == "outputs" && portIndex < workflow.Outputs.Count)
            {
                var port = workflow.Outputs[portIndex]; var outputs = main ? state.GroundedPlan.Outputs : state.GroundedPlan.Subflows[subflow].Outputs;
                var target = outputs.FindIndex(p => p.Name == port.Name);
                if (target < 0) return Host(finding);
                var path = intentRoot + "/outputs/" + target;
                if (parts.ElementAtOrDefault(5) == "value") path = ValuePath(port.Value, outputs[target].Value, parts[6..], path + "/value");
                return finding with { Location = path, Message = "Output '" + port.Name + "': " + finding.Message, ValidationStage = "intent" };
            }
            return Host(finding);
        }
        PlanningDiagnostic CaptureSource(PlanningDiagnostic finding, PlanningWorkflow block, string input, HashSet<string> visited)
        {
            if (!visited.Add(block.Key + "/" + input)) return Host(finding);
            var callers = state.Graph.Workflows.SelectMany((w, wi) => PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps")
                .Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally"))
                .Where(n => n.Node.Type == "workflow.call" && PlanningGraphValidation.Member(n.Node.Input, "ref") is { Kind: "workflow" } reference && reference.Source == block.Key)
                .Select(n => (Workflow: w, Index: wi, n.Node))).ToArray();
            if (callers.Length != 1) return Host(finding);
            var caller = callers[0];
            var value = PlanningGraphValidation.Member(caller.Node.Input, "args")?.Members.FirstOrDefault(m => m.Name == input)?.Value;
            if (value is null) return Host(finding);
            try
            {
                var contract = PlanningGraphValidation.ResolveValueContract(state.Graph, caller.Workflow, value, state.Catalog!);
                // A valid incoming contract cannot explain a broken generated port.
                if (PlanningValues.Established(contract) && PlanningContractValidation.ValidateSchema(contract, false).Count == 0) return Host(finding);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { }
            var root = "/workflows/" + caller.Index;
            if (value.Kind == "input")
            {
                var source = caller.Workflow.Inputs.FindIndex(p => p.Name == value.Source);
                return source < 0 ? Host(finding) : Map(finding with { Location = root + "/inputs/" + source + "/schema" }, visited);
            }
            var producer = PlanningGraphValidation.Located(caller.Workflow.Steps, root + "/steps")
                .Concat(PlanningGraphValidation.Located(caller.Workflow.Finally, root + "/finally")).FirstOrDefault(n => n.Node.Key == value.Source);
            if (producer.Node is null) return Host(finding);
            if (value.Kind == "output") return Map(finding with { Location = producer.Path + "/outputSchema" }, visited);
            if (value.Kind == "loop_item")
            {
                var items = producer.Node.Input.Members.FindIndex(m => m.Name == "items");
                if (items >= 0) return Map(finding with { Location = producer.Path + "/input/members/" + items + "/value" }, visited);
            }
            return Host(finding);
        }
    }
    private static string ValuePointer(PlanningValue value, string[] segments)
    {
        var path = "";
        while (segments.Length >= 2)
        {
            if (segments.Length >= 3 && segments[0] == "members" && int.TryParse(segments[1], out var member) && member >= 0 && member < value.Members.Count)
            {
                path += "/" + PlanningFieldPaths.Escape(value.Members[member].Name); value = value.Members[member].Value; segments = segments[3..];
            }
            else if (segments[0] == "items" && int.TryParse(segments[1], out var item) && item >= 0 && item < value.Items.Count)
            { path += "/" + item; value = value.Items[item]; segments = segments[2..]; }
            else break;
        }
        return path;
    }
    private static string ValuePath(PlanningValue graph, GroundedValue intent, string[] segments, string path)
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
