using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Data addresses are compiler-owned. Model transport selects catalog identifiers only.</summary>
internal static class PlanningDataflow
{
    internal const int BindingVersion = 2;
    internal const int ContractVersion = 5;
    internal const string WorkflowOutputs = "$outputs";

    internal static Dictionary<string, PlanningBinding> Index(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningGraph graph, string? consumer = null)
    {
        var result = new Dictionary<string, PlanningBinding>(StringComparer.Ordinal);
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
        var locations = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToDictionary(p => p.Node.Key, p => p.Path, StringComparer.Ordinal);
        var consumerIndex = consumer is null or WorkflowOutputs ? nodes.Length : Array.FindIndex(nodes, n => n.Key == consumer);
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
        var sources = workflow.Inputs.Select(p => new PlanningValue { Kind = "input", Source = p.Name }).Concat(nodes.Take(Math.Max(0, consumerIndex)).Where(n => Available(n.Key)).SelectMany(n => n.StructuredOutput is null
            ? new[] { new PlanningValue { Kind = "output", Source = n.Key } }
            : new[] { new PlanningValue { Kind = "output", Source = n.Key }, new PlanningValue { Kind = "output", Source = n.Key, ResultChannel = "structured" } }));
        foreach (var source in sources)
        {
            JsonObject schema;
            try { schema = resolve(source); }
            catch (InvalidOperationException) { continue; }
            foreach (var path in Paths(schema, [], 0))
            {
                var value = new PlanningValue { Kind = source.Kind, Source = source.Source, ResultChannel = source.ResultChannel, Path = path };
                try
                {
                    var contract = resolve(value);
                    var conditional = source.Kind == "output" && (Guards(source.Source!).Any() ||
                        new[] { "/cases/", "/default/", "/branches/" }.Any(marker => locations[source.Source!].Contains(marker, StringComparison.Ordinal)));
                    var availability = conditional ? "conditional" : contract.Count == 0 ? "opaque" : Nullable(contract) ? "nullable" : "unconditional";
                    var id = PlanningOutputBindings.Id(value);
                    result[id] = new(id, workflow.Key, value, contract, availability);
                }
                catch (InvalidOperationException) { /* Optional/conditional fields need explicit narrowing, never a guessed address. */ }
            }
        }
        return result;

        bool Available(string key)
        {
            if (consumer is null) return true;
            foreach (var condition in Guards(key))
            {
                if (consumer == WorkflowOutputs || !Guards(consumer).Any(guard => JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(condition, PlanningJsonContext.Default.PlanningValue),
                    JsonSerializer.SerializeToNode(guard, PlanningJsonContext.Default.PlanningValue)))) return false;
            }
            // Results inside conditional/parallel/loop bodies are addressed through the completed
            // container outside that body. A direct producer is available only in the same body.
            var path = locations[key]; var target = consumer == WorkflowOutputs ? "/outputs" : locations[consumer];
            if (target.StartsWith(path + "/", StringComparison.Ordinal)) return false; // An executing ancestor has no completed result yet.
            foreach (var marker in new[] { "/cases/", "/default/", "/branches/" })
            {
                var start = 0;
                while ((start = path.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
                {
                    var end = marker == "/default/" ? start + marker.Length - 1 : path.IndexOf('/', start + marker.Length);
                    if (end < 0 || !target.StartsWith(path[..(end + 1)], StringComparison.Ordinal)) return false;
                    start += marker.Length;
                }
            }
            foreach (var node in nodes.Where(n => n.Type is "loop.sequential" or "loop.parallel"))
                if (path.StartsWith(locations[node.Key] + "/steps/", StringComparison.Ordinal) && !target.StartsWith(locations[node.Key] + "/steps/", StringComparison.Ordinal)) return false;
            return true;
        }

        IEnumerable<PlanningValue> Guards(string key) => nodes.Where(n => n.If is not null &&
            (n.Key == key || locations[key].StartsWith(locations[n.Key] + "/", StringComparison.Ordinal))).Select(n => n.If!);
    }

    internal static Dictionary<string, PlanningBinding> CompactIndex(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningGraph graph, string consumer)
    {
        var sequences = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => n.Type == "sequence").Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        // sequence shares its execution context with its children. Enumerating every
        // ancestor's aliases expands the same producer contract repeatedly. Keep its
        // whole result and the directly addressable child contracts instead.
        return Index(workflow, preparation, graph, consumer).Where(p => p.Value.Value.Path.Count == 0 || !sequences.Contains(p.Value.Value.Source ?? ""))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    internal static JsonObject BindingSchema(IEnumerable<string> identifiers) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JsonArray("kind", "reference"),
        ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("binding") },
            ["reference"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(identifiers.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()) } }
    };

    internal static JsonNode? Transport(JsonNode? value, IReadOnlyDictionary<string, PlanningBinding> bindings)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(v => Transport(v, bindings)).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        if (obj["kind"]?.GetValue<string>() == "expression")
        {
            var expression = obj["text"]?.GetValue<string>() ?? "";
            if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}')) expression = expression[2..^1];
            try
            {
                var literal = JsonNode.Parse(expression);
                if (literal is null or JsonValue) return PlanningModelValues.Compact(JsonSerializer.SerializeToNode(PlanningConstruction.Literal(literal), PlanningJsonContext.Default.PlanningValue));
            }
            catch (JsonException) { }
            var computation = new PlanningValue { Kind = "compute", Text = expression };
            try
            {
                PlanningComputations.Validate(computation);
                return PlanningModelValues.Compact(JsonSerializer.SerializeToNode(computation, PlanningJsonContext.Default.PlanningValue));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Acornima.ParseErrorException) { /* Context-dependent legacy code needs a binding repair. */ }
        }
        if (obj["kind"]?.GetValue<string>() is "input" or "output")
        {
            var reference = JsonSerializer.Deserialize(obj, PlanningJsonContext.Default.PlanningValue)!;
            var id = PlanningOutputBindings.Id(reference);
            if (bindings.ContainsKey(id)) return new JsonObject { ["kind"] = "binding", ["reference"] = id };
        }
        return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Transport(p.Value, bindings))));
    }

    internal static JsonNode? Expand(JsonNode? value, IReadOnlyDictionary<string, PlanningBinding> bindings)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(v => Expand(v, bindings)).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        if (obj["kind"]?.GetValue<string>() == "binding")
        {
            if (!bindings.TryGetValue(obj["reference"]!.GetValue<string>(), out var binding)) throw new InvalidOperationException("The selected binding is unavailable in this operation's execution scope.");
            return JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue);
        }
        return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Expand(p.Value, bindings))));
    }

    internal static PlanningDataflowContract Describe(PlanningGraph graph, PlanningPreparation preparation)
    {
        var contract = new PlanningDataflowContract();
        foreach (var workflow in graph.Workflows)
        {
            var index = Index(workflow, preparation, graph); contract.Bindings.AddRange(index.Values);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                var consumed = References(node.Input).Concat(node.Expr is null ? [] : References(node.Expr)).DistinctBy(PlanningOutputBindings.Id).ToArray();
                contract.Operations.Add(new(workflow.Key, node.Key, consumed.Select(PlanningOutputBindings.Id).ToList(), consumed.Where(v => v.Kind == "input").Select(v => v.Source!).Distinct(StringComparer.Ordinal).ToList()));
            }
        }
        contract.Fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningDataflowContract));
        return contract;
    }

    internal static IEnumerable<PlanningValue> References(PlanningValue value)
    {
        if (value.Kind is "input" or "output") yield return value;
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var reference in References(child)) yield return reference;
    }

    internal static HashSet<string> BusinessInputs(PlanningWorkflow workflow, PlanningNode node)
    {
        var found = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(PlanningNode current)
        {
            if (!visited.Add(current.Key)) return;
            foreach (var child in current.Steps.Concat(current.Default).Concat(current.Cases.SelectMany(c => c.Steps)).Concat(current.Branches.SelectMany(b => b.Steps))) Visit(child);
            foreach (var value in References(current.Input).Concat(current.Expr is null ? [] : References(current.Expr)))
                if (value.Kind == "input") found.Add(value.Source!);
                else if (PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source) is { } producer) Visit(producer);
        }
        Visit(node); return found;
    }

    private static bool Nullable(JsonObject schema) => schema["type"] is JsonArray types && types.Any(t => t?.ToString() == "null") ||
        (schema["anyOf"] ?? schema["oneOf"]) is JsonArray variants && variants.Any(v => v?["type"]?.ToString() == "null");

    private static IEnumerable<List<string>> Paths(JsonObject schema, List<string> path, int depth)
    {
        yield return path;
        if (depth >= 16 || schema["properties"] is not JsonObject properties) yield break;
        var required = (schema["required"] as JsonArray ?? []).Select(p => p?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, child) in properties)
            if (required.Contains(name) && child is JsonObject nested)
                foreach (var value in Paths(nested, path.Append(name).ToList(), depth + 1)) yield return value;
    }
}
