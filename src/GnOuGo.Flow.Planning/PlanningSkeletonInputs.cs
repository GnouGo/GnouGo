using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static class PlanningSkeletonInputs
{
    internal static void Build(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode node, string path, PlanningCapability? capability)
    {
        if (node.Type == "human.input" && state.Preparation!.Interactions.Any(i => i.CapabilityId == node.CapabilityId && node.OperationIds.Contains(i.OperationId)))
        {
            node.Input = Literal(HumanInputContract.ConfirmationInput(node.Purpose));
            var behavior = PlanningBehaviorPlans.Enumerate(state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key).Steps.Concat(state.BehaviorPlan.Workflows.Single(w => w.Key == workflow.Key).Finally)).Single(n => n.Key == node.Key);
            if (behavior.InputDependencies is { Count: > 0 } || capability?.InputOperationIds.Count > 0)
            {
                var prompt = node.Input.Members.FindIndex(m => m.Name == "prompt");
                node.Input.Members[prompt] = new("prompt", new() { Kind = PlanningGraphSkeleton.Unresolved });
                PlanningGraphSkeleton.Add(state, workflow, node, path + "/input/members/" + prompt + "/value", "value", node.Purpose, new() { ["type"] = "string" });
            }
            return;
        }
        var contract = capability?.InputSchema;
        if (node.Type == "set" && capability?.FixedInput is { Count: > 0 } fixedResult)
        { node.Input = Literal(fixedResult); return; }
        if (contract?["properties"] is not JsonObject fields || node.Type == "set")
        {
            node.Input = new() { Kind = PlanningGraphSkeleton.Unresolved };
            PlanningGraphSkeleton.Add(state, workflow, node, path + "/input", "value", node.Purpose);
            return;
        }
        var input = new PlanningValue { Kind = "object" };
        node.Input = node.Type == "mcp.call" ? new() { Kind = "object", Members = [new("request", input)] } : input;
        var inputPath = path + "/input" + (node.Type == "mcp.call" ? "/members/0/value" : "");
        var fixedValues = node.Type == "mcp.call" ? new JsonObject() : capability!.FixedInput.DeepClone().AsObject();
        foreach (var binding in capability!.RequestBindings)
            SetLiteral(fixedValues, binding.Path, binding.Value);
        Fill(input, fields, contract!, fixedValues, inputPath);

        void Fill(PlanningValue destination, JsonObject properties, JsonObject schema, JsonObject locked, string root)
        {
            var required = (schema["required"] as JsonArray ?? []).Select(v => v!.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, field) in properties)
            {
                if (!required.Contains(name) && !locked.ContainsKey(name)) continue;
                var memberPath = root + "/members/" + destination.Members.Count + "/value";
                PlanningValue value;
                if (field is JsonObject child && child["properties"] is JsonObject children && locked[name] is null or JsonObject)
                { value = new() { Kind = "object" }; Fill(value, children, child, locked[name] as JsonObject ?? new(), memberPath); }
                else if (locked.TryGetPropertyValue(name, out var literal)) value = Literal(literal);
                else
                {
                    value = new() { Kind = PlanningGraphSkeleton.Unresolved };
                    PlanningGraphSkeleton.Add(state, workflow, node, memberPath, "value", node.Purpose + "; argument " + name, field as JsonObject);
                }
                destination.Members.Add(new(name, value));
            }
            foreach (var (name, value) in locked.Where(p => !properties.ContainsKey(p.Key))) destination.Members.Add(new(name, Literal(value)));
        }
    }
    internal static PlanningValue Literal(JsonNode? value)
    {
        var literal = PlanningGraphImporter.Value(value);
        void Data(PlanningValue current)
        {
            if (current.Kind == "template") current.Kind = "string";
            foreach (var child in current.Members.Select(m => m.Value).Concat(current.Items)) Data(child);
        }
        Data(literal); return literal;
    }
    private static void SetLiteral(JsonObject target, string pointer, JsonNode? value)
    {
        var parts = pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("A fixed argument requires a property pointer.");
        for (var i = 0; i < parts.Length - 1; i++)
        { target[parts[i]] ??= new JsonObject(); target = target[parts[i]]!.AsObject(); }
        target[parts[^1]] = value?.DeepClone();
    }
}
