using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static class PlanningSkeletonInputs
{
    // Explicit absence keeps the canonical member identity stable across staged
    // assignments. Only deterministic lowering removes it from the request object.
    internal const string Omitted = "omitted";
    internal static void GuardFinalizers(PlanningWorkflow workflow, PlanningPreparation preparation)
    {
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Finally).Where(n => n.If is null))
            if (FinalizerGuard(workflow, preparation, node) is { } guard) node.If = guard;
    }

    internal static bool GuardedFinalizerSource(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningNode consumer, string source)
        => FinalizerSources(workflow, preparation, consumer).Any(n => n.Key == source) &&
            consumer.If is { Kind: "expression" } condition && condition.Text == FinalizerGuard(workflow, preparation, consumer)?.Text;

    internal static bool FinalizerCompletesOnSuccess(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningNode node)
        => workflow.Finally.Contains(node) && node.OnError.Count == 0 && node.If is { Kind: "expression" } condition &&
            condition.Text == FinalizerGuard(workflow, preparation, node)?.Text;

    private static PlanningValue? FinalizerGuard(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningNode node)
    {
        var sources = FinalizerSources(workflow, preparation, node);
        return sources.Count == 0 ? null : new() { Kind = "expression", Text = string.Join(" && ", sources.Select(n =>
            "data.steps[" + JsonSerializer.Serialize(n.Key, PlanningJsonContext.Default.String) + "] != null")) };
    }

    private static IReadOnlyList<PlanningNode> FinalizerSources(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningNode node)
    {
        if (!PlanningGraphCompiler.Enumerate(workflow.Finally).Contains(node)) return [];
        var required = PlanningOperationCompositions.RequiredInputs(workflow, node, preparation);
        if (required.Count == 0) return [];
        var result = new List<PlanningNode>();
        foreach (var operation in required)
        {
            // An unconditional materializer publishes its original resource only after
            // successful execution. Failed setup grants no cleanup path or ownership.
            var producers = Unconditional(workflow.Steps).Where(n => n.Type == "mcp.call" && n.If is null && n.OnError.Count == 0 &&
                n.OperationIds.Contains(operation) && preparation.Capabilities.SingleOrDefault(c => c.Id == n.CapabilityId)?.ArtifactContract?.Produces.Any(a => a.Mode == "materialize") == true).ToArray();
            if (producers.Length != 1) return [];
            result.Add(producers[0]);
        }
        return result.DistinctBy(n => n.Key).OrderBy(n => n.Key, StringComparer.Ordinal).ToArray();

        static IEnumerable<PlanningNode> Unconditional(IEnumerable<PlanningNode> nodes)
        {
            foreach (var node in nodes.Where(n => n.If is null && n.OnError.Count == 0))
            {
                yield return node;
                // SequenceExecutor shares the parent's step data even when a later
                // child fails. Branches and loop iterations have different scopes.
                if (node.Type == "sequence")
                    foreach (var child in Unconditional(node.Steps)) yield return child;
            }
        }
    }
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
                var memberPath = root + "/members/" + destination.Members.Count + "/value";
                PlanningValue value;
                if (locked.ContainsKey(name) && locked[name] is null) value = Literal(null);
                else if ((required.Contains(name) || locked.ContainsKey(name)) && field is JsonObject child && child["properties"] is JsonObject children && locked[name] is null or JsonObject)
                { value = new() { Kind = "object" }; Fill(value, children, child, locked[name] as JsonObject ?? new(), memberPath); }
                else if (locked.TryGetPropertyValue(name, out var literal)) value = Literal(literal);
                else
                {
                    value = new() { Kind = PlanningGraphSkeleton.Unresolved };
                    PlanningGraphSkeleton.Add(state, workflow, node, memberPath, "value", node.Purpose + "; argument " + name, field as JsonObject);
                    state.Construction.Holes[^1].Optional = !required.Contains(name);
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
