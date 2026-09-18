using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolves an untrusted intent into catalog-owned executable structure.</summary>
public static class PlanningGraphBuilder
{
    public static PlanningGraph Build(WorkflowIntentPlan intent, PlanningCatalog catalog)
    {
        var graph = new PlanningGraph { Summary = intent.Summary, Entrypoint = intent.Entrypoint, Functions = intent.Functions };
        foreach (var item in intent.Workflows)
        {
            CheckKey(item.Key);
            var workflow = new PlanningWorkflow
            {
                Key = item.Key, Purpose = item.Purpose, Inputs = item.Inputs, Outputs = item.Outputs, Functions = item.Functions,
                Steps = item.Steps.Select(Node).ToList(), Finally = item.Finally.Select(Node).ToList()
            };
            graph.Workflows.Add(workflow);
        }
        // Copy reusable values so resolving a graph never changes the model's interpretation.
        graph = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        foreach (var workflow in graph.Workflows)
        {
            Normalize(workflow.Steps); Normalize(workflow.Finally);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Finally))
            {
                var sources = References(node).Where(v => v.Kind == "output").Select(v => v.Source!).Distinct(StringComparer.Ordinal).ToArray();
                var main = PlanningGraphCompiler.Enumerate(workflow.Steps).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
                var required = sources.Where(main.Contains).ToArray();
                if (required.Length == 0) continue;
                var guard = AvailabilityGuard(required);
                node.If = node.If is null ? guard : new PlanningValue { Kind = "compute", Text = "available && condition", Members = [new("available", guard), new("condition", node.If)] };
            }
        }
        return graph;

        PlanningNode Node(WorkflowIntentStep item)
        {
            CheckKey(item.Key);
            var capability = catalog.Capabilities.SingleOrDefault(c => c.Id == item.CapabilityId);
            if (item.Kind != "invoke" && item.CapabilityId is not null && capability?.StepType != item.Kind)
                throw new InvalidOperationException("The native step kind conflicts with its capability: " + item.Key);
            var type = item.Kind == "invoke" ? capability?.StepType ?? PlanningValues.Hole : item.Kind;
            var input = item.Input;
            if (type == "mcp.call")
                input = new() { Kind = "object", Members = [new("request", input)] };
            return new PlanningNode
            {
                Key = item.Key, Type = type, Purpose = item.Purpose, CapabilityId = item.CapabilityId,
                Dependencies = item.Dependencies, Input = input, If = item.If, Expr = item.Expr,
                OutputSchema = item.OutputSchema, StructuredOutput = item.StructuredOutput, Output = item.Output,
                ItemVar = item.ItemVar, IndexVar = item.IndexVar, Retry = item.Retry, OnError = item.OnError,
                Steps = item.Steps.Select(Node).ToList(),
                Branches = item.Branches.Select(b => new PlanningBranch(b.Steps.Select(Node).ToList())).ToList(),
                Cases = item.Cases.Select(c => new PlanningCase(c.Value, c.When, c.Steps.Select(Node).ToList())).ToList(),
                Default = item.Default.Select(Node).ToList()
            };
        }
        void Normalize(List<PlanningNode> nodes)
        {
            foreach (var node in nodes)
            {
                var capability = catalog.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId);
                var schema = capability?.InputSchema ?? catalog.StepContracts[node.Type]?["input"] as JsonObject;
                var input = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request")! : node.Input;
                if (capability is not null) ApplyBindings(input, capability);
                if (schema is not null) FillRequired(input, schema);
                Normalize(node.Steps); Normalize(node.Default);
                foreach (var branch in node.Branches) Normalize(branch.Steps);
                foreach (var branch in node.Cases) Normalize(branch.Steps);
            }
            var pending = nodes.ToList(); var ordered = new List<PlanningNode>();
            var local = pending.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var ready = pending.FirstOrDefault(n => n.Dependencies.Concat(References(n).Where(v => v.Kind == "output").Select(v => v.Source!))
                    .Where(local.Contains).All(d => ordered.Any(p => p.Key == d)));
                if (ready is null) break; // The validator reports cycles with graph locations.
                pending.Remove(ready); ordered.Add(ready);
            }
            if (pending.Count == 0) { nodes.Clear(); nodes.AddRange(ordered); }
        }
    }

    internal static void ApplyBindings(PlanningValue arguments, PlanningCapability capability)
    {
        foreach (var binding in capability.RequestBindings)
        {
            var parts = binding.Path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (parts.Length == 0) throw new InvalidOperationException("Catalog argument bindings require a property path.");
            var parent = arguments;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parent.Kind != "object") throw new InvalidOperationException("Catalog argument bindings require object arguments.");
                var index = parent.Members.FindIndex(m => m.Name == parts[i]);
                if (i == parts.Length - 1)
                {
                    var member = new PlanningMember(parts[i], PlanningJsonTransport.Literal(binding.Value));
                    if (index < 0) parent.Members.Add(member); else parent.Members[index] = member;
                }
                else
                {
                    if (index < 0) { parent.Members.Add(new(parts[i], new() { Kind = "object" })); index = parent.Members.Count - 1; }
                    parent = parent.Members[index].Value;
                }
            }
        }
    }

    internal static void FillRequired(PlanningValue value, JsonObject schema)
    {
        if (value.Kind != "object" || schema["properties"] is not JsonObject fields) return;
        foreach (var required in (schema["required"] as JsonArray ?? []).Select(v => v!.GetValue<string>()))
            if (!value.Members.Any(m => m.Name == required)) value.Members.Add(new(required, new() { Kind = PlanningValues.Hole }));
        foreach (var member in value.Members)
            if (fields[member.Name] is JsonObject child) FillRequired(member.Value, child);
    }
    internal static IEnumerable<PlanningValue> References(PlanningNode node) => PlanningDataflow.References(node.Input)
        .Concat(node.If is null ? [] : PlanningDataflow.References(node.If)).Concat(node.Expr is null ? [] : PlanningDataflow.References(node.Expr))
        .Concat(node.Cases.Where(c => c.When is not null).SelectMany(c => PlanningDataflow.References(c.When!)))
        .Concat(node.OnError.SelectMany(e => (e.If is null ? Enumerable.Empty<PlanningValue>() : PlanningDataflow.References(e.If)).Concat(e.SetOutput is null ? [] : PlanningDataflow.References(e.SetOutput))));
    private static void CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("__planning_", StringComparison.Ordinal))
            throw new InvalidOperationException("Graph identifiers must be nonempty and cannot use the reserved __planning_ prefix.");
    }
    private static PlanningValue AvailabilityGuard(IEnumerable<string> sources) => new()
    {
        Kind = "expression", Text = string.Join(" && ", sources.Order(StringComparer.Ordinal).Select(s => "data.steps[" + JsonSerializer.Serialize(s, PlanningJsonContext.Default.String) + "] != null"))
    };
    internal static bool GuardsFinalizerSource(PlanningNode node, string source)
    {
        var guard = node.If?.Kind == "compute" ? node.If.Members.FirstOrDefault(m => m.Name == "available")?.Value : node.If;
        return guard?.Kind == "expression" && guard.Text?.Split(" && ", StringSplitOptions.None)
            .Contains(AvailabilityGuard([source]).Text, StringComparer.Ordinal) == true;
    }
}
