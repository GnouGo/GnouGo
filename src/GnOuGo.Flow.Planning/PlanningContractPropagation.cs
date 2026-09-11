using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Equality edges refine unresolved schema coordinates in either direction.</summary>
internal static class PlanningContractPropagation
{
    internal static bool Resolve(PlanningSnapshot state)
    {
        if (state.Construction.PendingCalls.Count > 0 || state.Construction.Candidates.Count > 0) return false;
        var changed = false;
        var proposals = new Dictionary<string, List<PlanningSchemaRefinement.Requirement>>(StringComparer.Ordinal);
        foreach (var workflow in state.Graph!.Workflows.ToArray())
        {
            var wi = state.Graph.Workflows.IndexOf(workflow); var root = "/workflows/" + wi;
            foreach (var output in workflow.Outputs)
            {
                var path = root + "/outputs/" + workflow.Outputs.IndexOf(output) + "/schema";
                Forward(workflow, output.Value, path);
                if (Known(output.Schema)) Backward(workflow, output.Value, output.Schema);
                // One returned value and one pure producer establish one possible local boundary.
                if (output.Value.Kind == PlanningGraphSkeleton.Unresolved && workflow.Outputs.Count == 1 && workflow.Steps is [{ Type: "set", InternalRole: null } producer] &&
                    Known(output.Schema) && PlanningGraphCompiler.ToJsonSchema(output.Schema, state.Preparation!)["type"]?.ToString() == "object")
                    Backward(workflow, new() { Kind = "output", Source = producer.Key }, output.Schema);
            }
            foreach (var (node, nodePath) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                if (node.Type == "workflow.call" && PlanningWorkflowProvenance.Target(node) is { } key)
                {
                    var target = state.Graph.Workflows.Single(w => w.Key == key);
                    foreach (var argument in PlanningGraphValidation.Member(node.Input, "args")?.Members ?? [])
                    {
                        var port = target.Inputs.Single(p => p.Name == argument.Name);
                        if (Known(port.Schema)) Backward(workflow, argument.Value, port.Schema);
                        Forward(workflow, argument.Value, "/workflows/" + state.Graph.Workflows.IndexOf(target) + "/inputs/" + target.Inputs.IndexOf(port) + "/schema");
                    }
                }
                else if (node.Type == "set")
                {
                    Forward(workflow, node.Input, nodePath + "/outputSchema");
                    if (node.OutputSchema is { } schema && Known(schema)) Backward(workflow, node.Input, schema);
                }
                else if (state.Preparation!.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { } capability)
                {
                    var input = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request") : node.Input;
                    foreach (var member in input?.Members ?? [])
                        if (capability.InputSchema["properties"]?[member.Name] is not null)
                            Backward(workflow, member.Value, new() { CapabilityId = capability.Id, SchemaPointer = "/input/properties/" + PlanningFieldPaths.Escape(member.Name) });
                }
            }
        }
        var resolved = new Dictionary<string, PlanningSchema>(StringComparer.Ordinal);
        foreach (var (path, requirements) in proposals)
        {
            var prior = JsonSerializer.Deserialize(PlanningFieldPaths.Read(PlanningFieldPaths.Json(state.Graph!), path)!, PlanningJsonContext.Default.PlanningSchema)!;
            try { resolved[path] = PlanningSchemaRefinement.Resolve(prior, requirements, state.Preparation!); }
            catch (InvalidOperationException error) { throw new PlanningHoleUnavailableException(PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(state.Graph!), path), error.Message); }
        }
        foreach (var (path, schema) in resolved)
        {
            var hole = state.Construction.Holes.Single(h => !h.Resolved && h.Kind == "schema" && h.Path == path);
            var json = JsonSerializer.SerializeToNode(schema, PlanningJsonContext.Default.PlanningSchema);
            PlanningBindingResolution.Assign(state, hole, json);
            if (PlanningGraphSkeleton.HasUnresolved(json)) PlanningSchemaPropagation.Split(state, hole, schema);
            changed = true;
        }
        return changed;

        bool Known(PlanningSchema schema)
        {
            try { return PlanningSchemaPropagation.Established(PlanningGraphCompiler.ToJsonSchema(schema, state.Preparation!)); }
            catch (InvalidOperationException) { return false; }
        }
        void Forward(PlanningWorkflow workflow, PlanningValue value, string path)
        {
            if (!state.Construction.Holes.Any(h => !h.Resolved && h.Kind == "schema" && (h.Path == path || h.Path.StartsWith(path + "/", StringComparison.Ordinal)))) return;
            try
            {
                var contract = PlanningGraphValidation.ValueContractResolver(state.Graph!, workflow, state.Preparation!)(value);
                if (PlanningSchemaPropagation.Established(contract))
                {
                    // Refer to authoritative contracts before importing a structural subset.
                    var reference = PlanningSchemaReferences.Index(state.Preparation!).OfType<JsonObject>().Select(r => new PlanningSchema
                        { CapabilityId = r["capabilityId"]!.ToString(), SchemaPointer = r["schemaPointer"]!.ToString() })
                        .FirstOrDefault(r => JsonNode.DeepEquals(PlanningSchemaReferences.Resolve(r, state.Preparation!), contract));
                    Commit(path, reference ?? PlanningGraphImporter.Schema(contract));
                }
            }
            catch (InvalidOperationException) { /* A missing or unrepresentable contract grants no inference. */ }
        }
        void Backward(PlanningWorkflow workflow, PlanningValue value, PlanningSchema schema, int depth = 0)
        {
            if (depth > 32 || !Known(schema)) return;
            if (value.Kind == "array")
            {
                var item = schema.CapabilityId is null ? schema.Items : new PlanningSchema { CapabilityId = schema.CapabilityId, SchemaPointer = schema.SchemaPointer + "/items" };
                if (item is not null) foreach (var element in value.Items) Backward(workflow, element, item, depth + 1);
                return;
            }
            if (value.Kind == "object")
            {
                foreach (var member in value.Members)
                {
                    PlanningSchema? field = schema.CapabilityId is null ? schema.Properties.SingleOrDefault(p => p.Name == member.Name)?.Schema : new() { CapabilityId = schema.CapabilityId, SchemaPointer = schema.SchemaPointer + "/properties/" + PlanningFieldPaths.Escape(member.Name) };
                    if (field is not null) Backward(workflow, member.Value, field, depth + 1);
                }
                return;
            }
            if (value.Kind == "loop_item")
            {
                var loop = PlanningGraphCompiler.Enumerate(workflow.Steps).SingleOrDefault(n => n.Key == value.Source);
                if (loop is not null && PlanningGraphValidation.Member(loop.Input, "items") is { } source)
                    Backward(workflow, source, new() { Type = "array", Items = Project(schema, value.Path) }, depth + 1);
                return;
            }
            var root = "/workflows/" + state.Graph!.Workflows.IndexOf(workflow);
            if (value.Kind == "input")
            {
                var index = workflow.Inputs.FindIndex(p => p.Name == value.Source);
                if (index >= 0) Commit(root + "/inputs/" + index + "/schema", schema, value.Path);
            }
            else if (value.Kind == "output" && value.ResultChannel is null or "default")
            {
                var producer = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")).SingleOrDefault(p => p.Node.Key == value.Source);
                if (producer.Node is { Type: "set", InternalRole: null } node && (node.CapabilityId is null || state.Preparation!.Capabilities.Single(c => c.Id == node.CapabilityId).Resolution == "local"))
                    Commit(producer.Path + "/outputSchema", schema, value.Path);
                else if (producer.Node is { Type: "workflow.call" } call && PlanningWorkflowProvenance.Target(call) is { } key && value.Path.Count > 0)
                {
                    var callee = state.Graph.Workflows.Single(w => w.Key == key);
                    var output = callee.Outputs.FindIndex(p => p.Name == value.Path[0]);
                    if (output >= 0) Commit("/workflows/" + state.Graph.Workflows.IndexOf(callee) + "/outputs/" + output + "/schema", schema, value.Path.Skip(1).ToArray());
                }
            }
        }
        void Commit(string path, PlanningSchema proposed, IReadOnlyList<string>? members = null)
        {
            var hole = state.Construction.Holes.SingleOrDefault(h => !h.Resolved && h.Kind == "schema" && h.Path == path);
            if (hole is null)
            {
                if (!state.Construction.Holes.Any(h => !h.Resolved && h.Kind == "schema" && h.Path.StartsWith(path + "/", StringComparison.Ordinal))) return;
                var current = JsonSerializer.Deserialize(PlanningFieldPaths.Read(PlanningFieldPaths.Json(state.Graph!), path)!, PlanningJsonContext.Default.PlanningSchema)!;
                if (members is { Count: > 0 })
                {
                    var index = current.Properties.FindIndex(p => p.Name == members[0]);
                    if (index >= 0) Commit(path + "/properties/" + index + "/schema", proposed, members.Skip(1).ToArray());
                }
                else
                {
                    for (var index = 0; index < current.Properties.Count; index++)
                    {
                        var name = current.Properties[index].Name;
                        var field = proposed.CapabilityId is null ? proposed.Properties.SingleOrDefault(p => p.Name == name)?.Schema : new PlanningSchema
                            { CapabilityId = proposed.CapabilityId, SchemaPointer = proposed.SchemaPointer + "/properties/" + PlanningFieldPaths.Escape(name) };
                        if (field is not null) Commit(path + "/properties/" + index + "/schema", field);
                    }
                    var item = proposed.CapabilityId is null ? proposed.Items : new PlanningSchema { CapabilityId = proposed.CapabilityId, SchemaPointer = proposed.SchemaPointer + "/items" };
                    if (current.Items is not null && item is not null) Commit(path + "/items", item);
                }
                return;
            }
            if (!proposals.TryGetValue(path, out var candidates)) proposals[path] = candidates = [];
            candidates.Add(new(proposed, members ?? []));
        }
    }
    private static PlanningSchema Project(PlanningSchema schema, IReadOnlyList<string> path)
    {
        for (var i = path.Count - 1; i >= 0; i--)
            schema = new() { Type = "object", Properties = [new() { Name = path[i], Required = true, Schema = schema }] };
        return schema;
    }
}
