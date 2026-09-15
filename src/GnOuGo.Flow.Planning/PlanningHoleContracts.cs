using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolve a contract at the exact typed coordinate, including nested members.</summary>
internal static class PlanningHoleContracts
{
    internal static (PlanningNode? Node, string[] Members) Target(PlanningWorkflow workflow, PlanningHole hole)
    {
        var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
        if (node is null) return (null, []);
        var start = hole.Path.IndexOf("/input", StringComparison.Ordinal);
        if (start < 0) return (node, []);
        var value = node.Input; var names = new List<string>(); var parts = hole.Path[(start + 6)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "members" && i + 2 < parts.Length && int.TryParse(parts[i + 1], out var index) && index < value.Members.Count)
            { names.Add(value.Members[index].Name); value = value.Members[index].Value; i += 2; }
            else if (parts[i] == "items" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out index) && index < value.Items.Count)
            { names.Add("[]"); value = value.Items[index]; i++; }
        }
        return (node, names.ToArray());
    }

    internal static JsonObject? Expected(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        if (hole.ExpectedSchema is not null) return hole.ExpectedSchema;
        var (node, members) = Target(workflow, hole);
        if (hole.Kind == "schema" && node is not null)
        {
            var declared = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId)?.OutputSchema;
            return hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) && declared is not null && PlanningSchemaPropagation.Established(declared) ? declared : null;
        }
        try
        {
            if (node is null)
            {
                var parts = hole.Path.Split('/');
                var port = int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
                return PlanningGraphCompiler.ToJsonSchema(parts[3] == "outputs" ? workflow.Outputs[port].Schema : workflow.Inputs[port].Schema, state.Preparation!);
            }
            JsonObject? schema;
            if (node.Type == "workflow.call" && members.Length >= 2 && members[0] == "args")
            {
                var target = state.Graph!.Workflows.Single(w => w.Key == PlanningWorkflowProvenance.Target(node));
                schema = PlanningGraphCompiler.ToJsonSchema(target.Inputs.Single(p => p.Name == members[1]).Schema, state.Preparation!);
                members = members[2..];
            }
            else if (node.Type == "set") schema = node.OutputSchema is null ? null : PlanningGraphCompiler.ToJsonSchema(node.OutputSchema, state.Preparation!);
            else
            {
                schema = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId)?.InputSchema;
                if (node.Type == "mcp.call" && members.FirstOrDefault() == "request") members = members[1..];
            }
            foreach (var member in members) schema = member == "[]" ? schema?["items"] as JsonObject : schema?["properties"]?[member] as JsonObject;
            return schema;
        }
        catch (InvalidOperationException) { return null; }
    }
}
