using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static class PlanningRepairInvariants
{
    internal static void PreserveBehavior(PlanningGraph before, PlanningGraph after, PlanningPreparation preparation, List<PlanningDiagnostic> priorDiagnostics, List<PlanningDiagnostic> diagnostics)
    {
        foreach (var workflow in before.Workflows)
        {
            var replacement = after.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
            if (replacement is null || !workflow.Inputs.Select(p => (p.Name, p.Required)).SequenceEqual(replacement.Inputs.Select(p => (p.Name, p.Required))) ||
                !workflow.Outputs.Select(p => p.Name).SequenceEqual(replacement.Outputs.Select(p => p.Name)) ||
                !workflow.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(replacement.OperationIds.Order(StringComparer.Ordinal)))
            {
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key, "Preserve workflow identity, ownership and every input/output obligation while repairing their invalid contracts."));
                continue;
            }
            var originalNodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
            if (originalNodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != originalNodes.Length) continue;
            var nodes = PlanningGraphCompiler.Enumerate(replacement.Steps.Concat(replacement.Finally)).ToLookup(n => n.Key, StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
                if (nodes[node.Key].Count() != 1 || (preparation.AllowedStepTypes.Contains(node.Type, StringComparer.Ordinal) && nodes[node.Key].First().Type != node.Type) || (node.CapabilityId is null || preparation.Capabilities.Any(c => c.Id == node.CapabilityId)) && nodes[node.Key].First().CapabilityId != node.CapabilityId)
                    diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key + "/" + node.Key, "Preserve the frozen actions and their selected capabilities."));
            var originalLocations = PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + before.Workflows.IndexOf(workflow) + "/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + before.Workflows.IndexOf(workflow) + "/finally")).ToDictionary(n => n.Node.Key, n => n.Path, StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                if (nodes[node.Key].Count() != 1) continue;
                var next = nodes[node.Key].First();
                var location = originalLocations[node.Key];
                bool Invalid(string field) => priorDiagnostics.Any(d => d.Location == "/workflows" || d.Location.StartsWith(location + "/" + field, StringComparison.Ordinal));
                bool Same(PlanningValue? left, PlanningValue? right) => JsonSerializer.Serialize(left, PlanningJsonContext.Default.PlanningValue) == JsonSerializer.Serialize(right, PlanningJsonContext.Default.PlanningValue);
                if ((!Invalid("if") && !Same(node.If, next.If)) || (!Invalid("expr") && !Same(node.Expr, next.Expr)) ||
                    node.Cases.Count != next.Cases.Count || !node.Cases.Select(c => c.Value).SequenceEqual(next.Cases.Select(c => c.Value)) ||
                    node.Branches.Count != next.Branches.Count)
                    diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", location, "Preserve validated conditions and every declared branch outcome."));
                for (var i = 0; i < Math.Min(node.Cases.Count, next.Cases.Count); i++)
                    if (!Invalid("cases/" + i + "/when") && !Same(node.Cases[i].When, next.Cases[i].When))
                        diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", location + "/cases/" + i, "Preserve the validated branch condition."));
            }
            var placements = Placements(replacement);
            if (Placements(workflow).Any(p => !placements.TryGetValue(p.Key, out var parent) || parent != p.Value))
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key, "Keep existing actions inside their original branches, loops and finalizers."));
            var finalizers = PlanningGraphCompiler.Enumerate(replacement.Finally).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            if (PlanningGraphCompiler.Enumerate(workflow.Finally).Any(n => !finalizers.Contains(n.Key)))
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key + "/finally", "Every existing finalizer must remain in finally."));
        }
    }

    internal static Dictionary<string, string> Placements(PlanningWorkflow workflow)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(workflow.Steps, "steps"); Visit(workflow.Finally, "finally");
        return result;
        void Visit(List<PlanningNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                result[node.Key] = parent;
                Visit(node.Steps, node.Key + "/steps"); Visit(node.Default, node.Key + "/default");
                for (var i = 0; i < node.Branches.Count; i++) Visit(node.Branches[i].Steps, node.Key + "/branches/" + i);
                for (var i = 0; i < node.Cases.Count; i++) Visit(node.Cases[i].Steps, node.Key + "/cases/" + i);
            }
        }
    }

    internal static bool PreservesUndiagnosedFields(PlanningGraph before, PlanningGraph after, IReadOnlyList<PlanningDiagnostic> diagnostics)
    {
        var required = diagnostics.Where(d => d.Required && d.Location.StartsWith('/')).ToArray();
        var paths = required.Where(d => d.Code != "NATIVE_INPUT_INVALID").Select(d => d.Location).ToArray();
        var nativePaths = required.Where(d => d.Code == "NATIVE_INPUT_INVALID").Select(d => d.Location).ToArray();
        return SameOutside(JsonSerializer.SerializeToNode(before, PlanningJsonContext.Default.PlanningGraph),
            JsonSerializer.SerializeToNode(after, PlanningJsonContext.Default.PlanningGraph), "", "");

        bool SameOutside(JsonNode? left, JsonNode? right, string path, string? logical = null)
        {
            if (JsonNode.DeepEquals(left, right) || Allowed(paths, path) || logical is not null && Allowed(nativePaths, logical)) return true;
            if (left is JsonObject a && right is JsonObject b)
            {
                // Native argument paths name object members; typed graph paths address
                // their array representation. Keep the two namespaces separate.
                if (a["kind"]?.ToString() == "object" && b["kind"]?.ToString() == "object" && a["members"] is JsonArray leftMembers && b["members"] is JsonArray rightMembers)
                {
                    var oldNames = leftMembers.Select(m => m!["name"]!.GetValue<string>()).ToArray();
                    var newNames = rightMembers.Select(m => m!["name"]!.GetValue<string>()).ToArray();
                    if (oldNames.Distinct(StringComparer.Ordinal).Count() != oldNames.Length || newNames.Distinct(StringComparer.Ordinal).Count() != newNames.Length ||
                        !oldNames.Where(newNames.Contains).SequenceEqual(newNames.Where(oldNames.Contains))) return false;
                    foreach (var name in oldNames.Union(newNames, StringComparer.Ordinal))
                    {
                        var oldIndex = Array.IndexOf(oldNames, name); var newIndex = Array.IndexOf(newNames, name);
                        var oldMember = oldIndex < 0 ? null : leftMembers[oldIndex]; var newMember = newIndex < 0 ? null : rightMembers[newIndex];
                        var memberPath = path + "/members/" + (oldIndex < 0 ? newIndex : oldIndex);
                        var memberLogical = logical is null ? null : logical + "/" + Escape(name);
                        if (oldMember is null || newMember is null)
                        { if (!SameOutside(oldMember, newMember, memberPath, memberLogical)) return false; }
                        else if (!SameOutside(oldMember["value"], newMember["value"], memberPath + "/value", memberLogical)) return false;
                    }
                    return a.Select(p => p.Key).Union(b.Select(p => p.Key)).Where(key => key != "members")
                        .All(key => SameOutside(a[key], b[key], path + "/" + Escape(key)));
                }
                if (a["kind"]?.ToString() == "array" && b["kind"]?.ToString() == "array" && a["items"] is JsonArray oldItems && b["items"] is JsonArray newItems)
                {
                    if (oldItems.Count != newItems.Count || !Enumerable.Range(0, oldItems.Count).All(i => SameOutside(oldItems[i], newItems[i], path + "/items/" + i, logical is null ? null : logical + "/" + i))) return false;
                    return a.Select(p => p.Key).Union(b.Select(p => p.Key)).Where(key => key != "items")
                        .All(key => SameOutside(a[key], b[key], path + "/" + Escape(key)));
                }
                return a.Select(p => p.Key).Union(b.Select(p => p.Key)).All(key => SameOutside(a[key], b[key], path + "/" + Escape(key),
                    logical is null || a.ContainsKey("kind") || b.ContainsKey("kind") ? null : logical + "/" + Escape(key)));
            }
            if (left is JsonArray x && right is JsonArray y && x.Count == y.Count)
                return Enumerable.Range(0, x.Count).All(i => SameOutside(x[i], y[i], path + "/" + i, logical is null ? null : logical + "/" + i));
            return false;
        }
        static bool Allowed(IEnumerable<string> locations, string path) => locations.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
        static string Escape(string text) => text.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
}
