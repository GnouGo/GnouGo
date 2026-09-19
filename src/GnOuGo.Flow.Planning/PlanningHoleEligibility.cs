using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal sealed record PlanningChoice(string Id, JsonNode? Value);
/// <summary>The same finite domain drives automatic resolution and model selections.</summary>
public static class PlanningHoleEligibility
{
    public static IReadOnlyList<PlanningHole> Find(PlanningGraph graph, PlanningCatalog catalog)
    {
        var holes = new List<PlanningHole>();
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                if (node.Type == PlanningValues.Hole && node.CapabilityId is null) Add(path + "/capabilityId", "capability", null, node.Key);
                var capability = catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                JsonObject? schema = capability?.InputSchema ?? catalog.StepContracts[node.Type]?["input"] as JsonObject;
                if (node.Type == "mcp.call") schema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["request"] = schema?.DeepClone() } };
                Value(node.Input, path + "/input", schema, node.Key);
                if (node.If is not null) Value(node.If, path + "/if", new() { ["type"] = "boolean" }, node.Key);
                if (node.Expr is not null) Value(node.Expr, path + "/expr", null, node.Key);
                if (node.OutputSchema is not null) Schema(node.OutputSchema, path + "/outputSchema", node.Key);
                if (node.StructuredOutput is not null) Schema(node.StructuredOutput.Schema, path + "/structuredOutput/schema", node.Key);
                for (var i = 0; i < node.Cases.Count; i++) if (node.Cases[i].When is { } when) Value(when, path + "/cases/" + i + "/when", new() { ["type"] = "boolean" }, node.Key);
                for (var i = 0; i < node.OnError.Count; i++)
                {
                    if (node.OnError[i].If is { } condition) Value(condition, path + "/onError/" + i + "/if", new() { ["type"] = "boolean" }, node.Key);
                    if (node.OnError[i].SetOutput is { } fallback) Value(fallback, path + "/onError/" + i + "/setOutput", null, node.Key);
                }
            }
            for (var i = 0; i < workflow.Inputs.Count; i++)
            {
                Schema(workflow.Inputs[i].Schema, root + "/inputs/" + i + "/schema", null);
                if (workflow.Inputs[i].Default is { } value) Value(value, root + "/inputs/" + i + "/default", Contract(workflow.Inputs[i].Schema), null);
            }
            for (var i = 0; i < workflow.Outputs.Count; i++)
            {
                Schema(workflow.Outputs[i].Schema, root + "/outputs/" + i + "/schema", null);
                Value(workflow.Outputs[i].Value, root + "/outputs/" + i + "/value", Contract(workflow.Outputs[i].Schema), null);
            }
            void Add(string path, string kind, JsonObject? schema, string? node, bool optional = false) => holes.Add(new(path, workflow.Key, node, path, kind, schema, optional));
            JsonObject? Contract(PlanningSchema schema) { try { return PlanningGraphCompiler.ToJsonSchema(schema, catalog); } catch (InvalidOperationException) { return null; } }
            void Schema(PlanningSchema schema, string path, string? node)
            {
                if (schema.Type == PlanningValues.Hole) Add(path, "schema", null, node);
                if (schema.Items is not null) Schema(schema.Items, path + "/items", node);
                for (var i = 0; i < schema.Properties.Count; i++) Schema(schema.Properties[i].Schema, path + "/properties/" + i + "/schema", node);
                if (schema.AdditionalProperties is not null) Schema(schema.AdditionalProperties, path + "/additionalProperties", node);
            }
            void Value(PlanningValue value, string path, JsonObject? expected, string? node, bool optional = false)
            {
                if (value.Kind == PlanningValues.Hole) Add(path, "value", expected, node, optional);
                var required = (expected?["required"] as JsonArray ?? []).Select(v => v!.ToString()).ToHashSet(StringComparer.Ordinal);
                for (var i = 0; i < value.Members.Count; i++)
                {
                    var member = value.Members[i];
                    Value(member.Value, path + "/members/" + i + "/value", expected?["properties"]?[member.Name] as JsonObject, node,
                        expected?["properties"] is JsonObject fields && fields.ContainsKey(member.Name) && !required.Contains(member.Name));
                }
                for (var i = 0; i < value.Items.Count; i++) Value(value.Items[i], path + "/items/" + i, expected?["items"] as JsonObject, node);
            }
        }
        return holes;
    }

    internal static IReadOnlyList<PlanningChoice> Choices(PlanningGraph graph, PlanningCatalog catalog, PlanningHole hole)
    {
        var workflow = graph.Workflows.Single(w => w.Key == hole.WorkflowKey);
        var choices = new List<JsonNode?>();
        if (hole.Kind == "capability")
        {
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
            foreach (var capability in catalog.Capabilities.Where(c => c.StepType == "mcp.call" && !catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal))
                if (node.Input.Kind == "object" && node.Input.Members.All(m => capability.InputSchema["properties"]?[m.Name] is JsonObject expected &&
                    (!PlanningGraphValidation.IsLiteral(m.Value) || PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(m.Value), expected).Count == 0))) choices.Add(JsonValue.Create(capability.Id));
        }
        else if (hole.Kind == "schema")
        {
            try
            {
                var json = PlanningFieldPaths.Json(graph);
                var valuePath = hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) ? hole.Path[..^"outputSchema".Length] + "input" : hole.Path[..^"schema".Length] + "value";
                if (PlanningFieldPaths.ReadOptional(json, valuePath) is JsonObject valueJson)
                {
                    var value = JsonSerializer.Deserialize(valueJson, PlanningJsonContext.Default.PlanningValue)!;
                    var schema = PlanningGraphValidation.ValueContractResolver(graph, workflow, catalog)(value);
                    if (PlanningValues.Established(schema)) choices.Add(JsonSerializer.SerializeToNode(PlanningGraphImporter.Schema(schema), PlanningJsonContext.Default.PlanningSchema));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException) { }
        }
        else if (hole.ExpectedSchema is { } expected)
        {
            if (expected.TryGetPropertyValue("const", out var constant)) Literal(constant);
            else if (expected["enum"] is JsonArray values) foreach (var value in values) Literal(value);
            else if (expected.TryGetPropertyValue("default", out var defaultValue)) Literal(defaultValue);
            // Defaults are evaluated before runtime data exists, including nested members/items.
            if (!IsInputDefault(hole)) try
            {
                foreach (var binding in PlanningDataflow.Index(workflow, catalog, graph, hole.NodeKey ?? PlanningDataflow.WorkflowOutputs).Values)
                    if (binding.Availability is "unconditional" or "nullable" && PlanningContractCompatibility.Fits(binding.Schema, expected) && ArtifactFits(binding.Value))
                        choices.Add(JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue));
            }
            catch (InvalidOperationException) { }
            if (hole.Optional && !IsInputDefault(hole)) choices.Add(JsonSerializer.SerializeToNode(new PlanningValue { Kind = PlanningValues.Omitted }, PlanningJsonContext.Default.PlanningValue));
            void Literal(JsonNode? value)
            {
                if (PlanningContractValidation.ValidateInstance(value, expected).Count == 0 && ArtifactFits(PlanningJsonTransport.Literal(value)))
                    choices.Add(JsonSerializer.SerializeToNode(PlanningJsonTransport.Literal(value), PlanningJsonContext.Default.PlanningValue));
            }
        }
        return choices.DistinctBy(c => c?.ToJsonString()).Select((c, i) => new PlanningChoice("choice_" + i, c)).ToArray();
        bool ArtifactFits(PlanningValue value)
        {
            if (hole.NodeKey is null) return true;
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
            var capability = catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
            if (capability?.ArtifactContract is null || !hole.Path.Contains("/input/", StringComparison.Ordinal)) return true;
            var nodePath = hole.Path[..hole.Path.IndexOf("/input", StringComparison.Ordinal)];
            foreach (var consumed in capability.ArtifactContract.Consumes)
            {
                var request = PlanningGraphValidation.Member(node.Input, "request");
                if (request is null) continue;
                var requestPath = nodePath + "/input/members/" + node.Input.Members.FindIndex(m => m.Name == "request") + "/value";
                if (PlanningValues.LiteralLocation(request, requestPath, consumed.Pointer) == hole.Path &&
                    !PlanningArtifactBindings.Proves(workflow, value, consumed.Kind, catalog, graph, new())) return false;
            }
            return true;
        }
    }

    internal static bool IsInputDefault(PlanningHole hole)
    {
        var parts = hole.Path.Split('/');
        return parts.Length >= 6 && parts[1] == "workflows" && parts[3] == "inputs" && parts[5] == "default";
    }

    internal static PlanningGraph Assign(PlanningGraph graph, PlanningCatalog catalog, PlanningHole hole, JsonNode? value)
    {
        var json = PlanningFieldPaths.Json(graph);
        PlanningFieldPaths.Replace(json, hole.Path, value);
        var updated = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!;
        if (hole.Kind == "capability")
        {
            var node = PlanningGraphCompiler.Enumerate(updated.Workflows.Single(w => w.Key == hole.WorkflowKey).Steps.Concat(updated.Workflows.Single(w => w.Key == hole.WorkflowKey).Finally)).Single(n => n.Key == hole.NodeKey);
            var capability = catalog.Capabilities.Single(c => c.Id == node.CapabilityId);
            node.Type = capability.StepType;
            PlanningGraphBuilder.ApplyBindings(node.Input, capability);
            PlanningGraphBuilder.FillRequired(node.Input, capability.InputSchema);
            if (node.Type == "mcp.call") node.Input = new() { Kind = "object", Members = [new("request", node.Input)] };
        }
        return updated;
    }
}
