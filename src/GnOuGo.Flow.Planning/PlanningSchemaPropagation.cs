using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolve declared or already computed contracts before dependent bindings.</summary>
internal static class PlanningSchemaPropagation
{
    internal static bool Established(JsonObject schema) => Established(schema, schema, 0);
    private static bool Established(JsonObject schema, JsonObject root, int depth)
    {
        if (depth > 32) return false;
        if (schema.ContainsKey("const") || schema["enum"] is JsonArray { Count: > 0 }) return true;
        if (schema["$ref"] is JsonValue reference)
        {
            var pointer = reference.ToString();
            return pointer.StartsWith("#/", StringComparison.Ordinal) && PlanningFieldPaths.ReadOptional(root, pointer[1..]) is JsonObject target && Established(target, root, depth + 1);
        }
        if ((schema["anyOf"] ?? schema["oneOf"]) is JsonArray alternatives)
            return alternatives.Count > 0 && alternatives.All(s => s is JsonObject variant && Established(variant, root, depth + 1));
        if (schema["allOf"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => Established(p, root, depth + 1))) return true;
        var types = schema["type"] is JsonArray union ? union.Select(t => t!.ToString()).Where(t => t != "null").ToArray() : [schema["type"]?.ToString() ?? ""];
        if (types.Length != 1) return false;
        return types[0] switch
        {
            "object" => (schema["properties"] as JsonObject ?? new()).All(p => p.Value is JsonObject field && Established(field, root, depth + 1)) &&
                (schema["properties"] is JsonObject { Count: > 0 } || schema["additionalProperties"]?.ToString() == "false" || schema["additionalProperties"] is JsonObject extra && Established(extra, root, depth + 1)),
            "array" => (schema["prefixItems"] is not JsonArray prefix || prefix.All(p => p is JsonObject item && Established(item, root, depth + 1))) &&
                (schema["items"]?.ToString() == "false" || schema["items"] is JsonObject items && Established(items, root, depth + 1)),
            "string" or "number" or "integer" or "boolean" or "null" => true,
            _ => false
        };
    }
    internal static bool Resolve(PlanningSnapshot state, PlanningWorkflow workflow)
    {
        var changed = false;
        foreach (var hole in state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && h.Kind == "schema").ToArray())
        {
            var json = PlanningFieldPaths.Read(PlanningFieldPaths.Json(state.Graph!), hole.Path);
            var schema = JsonSerializer.Deserialize(json!, PlanningJsonContext.Default.PlanningSchema)!;
            if (schema.Type != PlanningGraphSkeleton.Unresolved && PlanningGraphSkeleton.HasUnresolved(json))
            { Split(state, hole, schema); changed = true; }
        }
        foreach (var hole in state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && h.Kind == "schema" && h.NodeKey is not null).ToArray())
        {
            workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key);
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
            PlanningSchema? schema = null;
            var capability = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId);
            if (hole.Path.EndsWith("/structuredOutput/schema", StringComparison.Ordinal))
                schema = StructuredDecisionSchema(node, state.Preparation);
            else if (!hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal)) continue;
            else if (capability is not null && Established(capability.OutputSchema)) schema = new() { CapabilityId = capability.Id, SchemaPointer = "/output" };
            else if (PlanningBaselineValues.ResultSchema(state, workflow, node) is { } baseline) schema = baseline;
            else if (node.Type == "set" && !PlanningGraphSkeleton.HasUnresolved(JsonSerializer.SerializeToNode(node.Input, PlanningJsonContext.Default.PlanningValue)))
            {
                try
                {
                    var known = PlanningGraphValidation.ValueContractResolver(state.Graph, workflow, state.Preparation)(node.Input);
                    PlanningGraphValidation.RequireTyped(known, 0); schema = PlanningGraphImporter.Schema(known);
                }
                catch (InvalidOperationException) { /* A computation without a declared result contract remains a schema hole. */ }
            }
            if (schema is null) continue;
            PlanningBindingResolution.Assign(state, hole, JsonSerializer.SerializeToNode(schema, PlanningJsonContext.Default.PlanningSchema)); changed = true;
        }
        return changed;
    }

    internal static void Split(PlanningSnapshot state, PlanningHole container, PlanningSchema schema)
    {
        container.Resolved = true; container.Superseded = true;
        var workflow = state.Graph!.Workflows.Single(w => w.Key == container.WorkflowKey);
        var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == container.NodeKey);
        Visit(schema, container.Path);
        void Visit(PlanningSchema current, string path)
        {
            if (current.Type == PlanningGraphSkeleton.Unresolved)
            { PlanningGraphSkeleton.Add(state, workflow, node, path, "schema", container.Purpose); return; }
            if (current.Items is not null) Visit(current.Items, path + "/items");
            for (var i = 0; i < current.Properties.Count; i++) Visit(current.Properties[i].Schema, path + "/properties/" + i + "/schema");
            if (current.AdditionalProperties is not null) Visit(current.AdditionalProperties, path + "/additionalProperties");
        }
    }

    private static PlanningSchema? StructuredDecisionSchema(PlanningNode node, PlanningPreparation preparation)
    {
        var decisions = PlanningProducerContracts.StructuredDecisions(node, preparation).ToArray();
        if (decisions.Length == 0) return null;
        static JsonObject Container() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false };
        var root = Container();
        foreach (var decision in decisions)
        {
            var parts = PlanningProducerContracts.StructuredPointer(decision).Split('/').Skip(1)
                .Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (parts.Length == 0) return null;
            var current = root;
            for (var index = 0; index < parts.Length; index++)
            {
                if (current["properties"] is not JsonObject properties || current["required"] is not JsonArray required) return null;
                var name = parts[index];
                if (!required.Any(p => p?.ToString() == name)) required.Add((JsonNode)JsonValue.Create(name)!);
                if (index == parts.Length - 1)
                {
                    if (properties[name] is { } established && !JsonNode.DeepEquals(established, decision.ResponseSchema)) return null;
                    properties[name] = decision.ResponseSchema.DeepClone();
                }
                else
                {
                    properties[name] ??= Container();
                    if (properties[name] is not JsonObject child) return null;
                    current = child;
                }
            }
        }
        return PlanningGraphImporter.Schema(root);
    }
}
