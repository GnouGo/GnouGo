using GnOuGo.Flow.Core.Planning;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Derives editable intent coordinates from graph and runtime diagnostics.</summary>
internal static class PlanningDiagnosticLocations
{
    internal static List<PlanningDiagnostic> ForIntent(PlanningSession state)
    {
        if (state.IntentPlan is null || state.Graph is null) return state.Diagnostics.ToList();
        var graph = PlanningFieldPaths.Json(state.Graph); var intent = PlanningJsonTransport.Intent(state.IntentPlan);
        var diagnostics = state.Diagnostics.ToList();
        // Only group known consequences of an invalid producer contract. The session
        // retains every finding; unrelated binding/scope failures remain independent.
        foreach (var root in state.Diagnostics.Where(d => d.Code is "SCHEMA_INVALID" or "SCHEMA_REFERENCE_INVALID" or "STRUCTURED_OUTPUT_INVALID"))
        {
            var parts = root.Location.Split('/');
            if (parts.Length >= 6 && parts[3] == "outputs" && parts[5] == "schema" &&
                int.TryParse(parts[2], out var wi) && wi >= 0 && wi < state.Graph.Workflows.Count &&
                state.Graph.Workflows[wi].Key == PlanningConfirmationGuards.Body)
            {
                // The wrapper forwards the body's declared outputs. An invalid user
                // output schema is a repairable cause, not a broken host binding.
                var wrapper = state.Graph.Workflows.FindIndex(w => w.Key == state.Graph.Entrypoint);
                diagnostics.RemoveAll(d => d.Location.StartsWith("/workflows/" + wrapper + "/outputs/", StringComparison.Ordinal) &&
                    d.Rule == "producer:" + PlanningConfirmationGuards.Call &&
                    (d.Code == "BINDING_UNAVAILABLE" || d.Code == "OUTPUT_REFERENCE_INVALID" && d.Message == root.Message));
            }
            var split = root.Location.IndexOf("/outputSchema", StringComparison.Ordinal);
            if (split < 0) split = root.Location.IndexOf("/structuredOutput/schema", StringComparison.Ordinal);
            if (split < 0 || PlanningFieldPaths.ReadOptional(graph, root.Location[..split]) is not JsonObject producer) continue;
            var workflowRoot = string.Join('/', root.Location.Split('/').Take(3));
            var dependents = diagnostics.Where(d => d.Location.StartsWith(workflowRoot + "/", StringComparison.Ordinal) &&
                d.Rule == "producer:" + producer["key"]?.ToString() &&
                (d.Code == "BINDING_UNAVAILABLE" || d.Code == "OUTPUT_REFERENCE_INVALID" && d.Message == root.Message)).ToArray();
            if (dependents.Length == 0) continue;
            var names = dependents.Select(d => Map(d).Message.Split(": ", 2)[0]).Distinct(StringComparer.Ordinal);
            diagnostics.RemoveAll(dependents.Contains);
            var index = diagnostics.IndexOf(root);
            if (index >= 0) diagnostics[index] = root with { Message = root.Message + " Dependent locations: " + string.Join("; ", names) + "." };
        }
        return diagnostics.Select(Map).Distinct().ToList();

        PlanningDiagnostic Map(PlanningDiagnostic finding)
        {
            if (finding.Code == "CONFIRMATION_REQUIRED") return Host(finding);
            if (finding.ValidationStage == "intent" || !finding.Location.StartsWith("/workflows/", StringComparison.Ordinal)) return finding;
            finding = TypedInput(finding, state.Graph);
            var parts = finding.Location.Split('/');
            if (!int.TryParse(parts[2], out var wi) || wi < 0 || wi >= state.Graph.Workflows.Count) return Host(finding);
            var workflow = state.Graph.Workflows[wi];
            if (workflow.Key == state.Graph.Entrypoint && state.Graph.Workflows.Any(w => w.Key == PlanningConfirmationGuards.Body) &&
                parts.Length > 5 && parts[3] == "outputs" && parts[5] == "value") return Host(finding);
            var key = workflow.Key == PlanningConfirmationGuards.Body ? state.IntentPlan.Entrypoint : workflow.Key;
            var ii = state.IntentPlan.Workflows.FindIndex(w => w.Key == key);
            if (ii < 0) return Host(finding);
            var from = "/workflows/" + wi; var to = "/workflows/" + ii;
            var context = "Workflow '" + key + "'";
            JsonNode? source = graph["workflows"]![wi]; JsonNode? target = intent["workflows"]![ii];
            var located = PlanningGraphValidation.Located(workflow.Steps, from + "/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, from + "/finally"))
                .Where(n => finding.Location == n.Path || finding.Location.StartsWith(n.Path + "/", StringComparison.Ordinal))
                .OrderByDescending(n => n.Path.Length).FirstOrDefault();
            if (located.Node is { } node)
            {
                if (node.InternalRole is not null) return Host(finding);
                var match = Nodes(target!, to).FirstOrDefault(n => n.Node["key"]?.ToString() == node.Key);
                if (match.Node is null) return Host(finding);
                source = PlanningFieldPaths.Read(graph, located.Path); target = match.Node;
                from = located.Path; to = match.Path; context += ", step '" + node.Key + "'";
                if (node.Type == "mcp.call" && finding.Location.StartsWith(from + "/input", StringComparison.Ordinal))
                {
                    var request = node.Input.Members.FindIndex(m => m.Name == "request");
                    var wrapper = from + "/input/members/" + request + "/value";
                    if (request >= 0)
                        foreach (var binding in state.Catalog?.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.RequestBindings ?? [])
                        {
                            var fixedPath = PlanningValues.LiteralLocation(node.Input.Members[request].Value, wrapper, binding.Path);
                            if (finding.Location == fixedPath || finding.Location.StartsWith(fixedPath + "/", StringComparison.Ordinal)) return Host(finding);
                        }
                    if (request >= 0 && (finding.Location == wrapper || finding.Location.StartsWith(wrapper + "/", StringComparison.Ordinal)))
                    { source = PlanningFieldPaths.Read(graph, wrapper); target = target["input"]; from = wrapper; to += "/input"; }
                }
                if (located.Path.Contains("/finally/", StringComparison.Ordinal) && finding.Location.StartsWith(from + "/if", StringComparison.Ordinal) && node.If is not null)
                {
                    var graphCondition = source?["if"]?.DeepClone(); PlanningJsonTransport.Compact(graphCondition);
                    if (!JsonNode.DeepEquals(graphCondition, target?["if"]))
                    {
                        if (target?["if"] is null) return Host(finding);
                        var condition = node.If.Members.FindIndex(m => m.Name == "condition");
                        var wrapper = from + "/if/members/" + condition + "/value";
                        if (condition >= 0 && (finding.Location == wrapper || finding.Location.StartsWith(wrapper + "/", StringComparison.Ordinal)))
                        { source = PlanningFieldPaths.Read(graph, wrapper); target = target["if"]; from = wrapper; to += "/if"; }
                        else if (finding.Location != from + "/if") return Host(finding);
                    }
                }
            }
            var note = "";
            foreach (var escaped in finding.Location[from.Length..].Split('/').Skip(1))
            {
                var token = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (source is JsonObject obj && target is JsonObject other)
                {
                    var name = token == "type" && obj.ContainsKey("key") ? "kind" : token;
                    if (!other.ContainsKey(name)) { note = " Missing field '" + name + "' in this container."; break; }
                    source = obj[token]; target = other[name]; to += "/" + PlanningFieldPaths.Escape(name);
                }
                else if (source is JsonArray array && target is JsonArray targets && int.TryParse(token, out var index) && index >= 0 && index < array.Count)
                {
                    var child = array[index]; var identity = child is JsonObject item ? item["name"] ?? item["key"] : null;
                    var mapped = identity is null ? index : Enumerable.Range(0, targets.Count).FirstOrDefault(i =>
                        targets[i] is JsonObject candidate && JsonNode.DeepEquals(candidate["name"] ?? candidate["key"], identity), -1);
                    if (mapped < 0 || mapped >= targets.Count) { note = " Missing field '" + (identity?.ToString() ?? token) + "' in this container."; break; }
                    source = child; target = targets[mapped]; to += "/" + mapped;
                }
                else { note = " Resolve the missing field below this container."; break; }
            }
            return finding with { Location = to, Message = context + ": " + finding.Message + note };
        }
    }
    private static PlanningDiagnostic Host(PlanningDiagnostic finding) => finding with
    { Code = "PLANNING_HOST_CONTRACT", Message = "A generated host field has no editable intent location: " + finding.Code + ". " + finding.Message };

    private static IEnumerable<(JsonObject Node, string Path)> Nodes(JsonNode container, string path)
    {
        foreach (var field in new[] { "steps", "finally", "default", "branches", "cases" })
            if (container[field] is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonObject item)
                    {
                        var location = path + "/" + field + "/" + i;
                        if (item.ContainsKey("key")) yield return (item, location);
                        foreach (var child in Nodes(item, location)) yield return child;
                    }
    }

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
