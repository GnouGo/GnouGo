using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Data addresses are compiler-owned. Model transport selects catalog identifiers only.</summary>
internal static class PlanningDataflow
{
    internal const int BindingVersion = 2;
    internal const int ContractVersion = 46;
    internal const string WorkflowOutputs = "$outputs";

    internal static Dictionary<string, PlanningBinding> Index(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningGraph graph, string? consumer = null)
    {
        var result = new Dictionary<string, PlanningBinding>(StringComparer.Ordinal);
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
        var locations = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToDictionary(p => p.Node.Key, p => p.Path, StringComparer.Ordinal);
        var consumerIndex = consumer is null or WorkflowOutputs ? nodes.Length : Array.FindIndex(nodes, n => n.Key == consumer);
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
        var loopSources = consumer is null or WorkflowOutputs ? Enumerable.Empty<PlanningValue>() : nodes.Where(n => n.Type is "loop.sequential" or "loop.parallel" &&
            locations[consumer].StartsWith(locations[n.Key] + "/steps/", StringComparison.Ordinal))
            .SelectMany(n => new[] { new PlanningValue { Kind = "loop_item", Source = n.Key }, new PlanningValue { Kind = "loop_index", Source = n.Key } });
        var previousSources = consumer is null or WorkflowOutputs ? Enumerable.Empty<PlanningValue>() : nodes.Where(n => n.Type == "loop.sequential" &&
            (n.Key == consumer || locations[consumer].StartsWith(locations[n.Key] + "/steps/", StringComparison.Ordinal)))
            .Select(n => new PlanningValue { Kind = "loop_previous", Source = n.Key });
        var sources = workflow.Inputs.Select(p => new PlanningValue { Kind = "input", Source = p.Name }).Concat(loopSources).Concat(previousSources).Concat(nodes.Take(Math.Max(0, consumerIndex)).Where(n => Available(n.Key)).SelectMany(n => n.StructuredOutput is null
            ? new[] { new PlanningValue { Kind = "output", Source = n.Key } }
            : new[] { new PlanningValue { Kind = "output", Source = n.Key }, new PlanningValue { Kind = "output", Source = n.Key, ResultChannel = "structured" } }))
            .Concat(nodes.Take(Math.Max(0, consumerIndex)).Where(n => n.Type == "mcp.call" && n.OnError.Any(h => h.Action == "continue") && Available(n.Key))
                .Select(n => new PlanningValue { Kind = "output", Source = n.Key, ResultChannel = "envelope" }));
        foreach (var source in sources)
        {
            JsonObject schema;
            try { schema = resolve(source); }
            catch (InvalidOperationException) { continue; }
            var paths = Paths(schema, [], 0);
            if (source.Kind == "output" && nodes.FirstOrDefault(n => n.Key == source.Source) is { Type: "parallel" } parallel)
                paths = paths.Concat(Enumerable.Range(0, parallel.Branches.Count).SelectMany(i =>
                {
                    var prefix = new List<string> { "branches", i.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    return Paths(resolve(new() { Kind = "output", Source = parallel.Key, Path = prefix }), prefix, 0);
                }));
            foreach (var path in paths)
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
        foreach (var loop in nodes.Take(Math.Max(0, consumerIndex)).Where(n => n.Type is "loop.sequential" or "loop.parallel" && Available(n.Key)))
            foreach (var child in loop.Steps.Where(n => n.Type == "mcp.call"))
                foreach (var artifact in preparation.Capabilities.FirstOrDefault(c => c.Id == child.CapabilityId)?.ArtifactContract?.Produces.Where(p => p.Encoding == "json_array") ?? [])
                {
                    var value = new PlanningValue { Kind = "artifact_collection", Source = loop.Key,
                        Path = new[] { child.Key, "response" }.Concat(artifact.Pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))).ToList() };
                    try { var schema = resolve(value); var id = PlanningOutputBindings.Id(value); result[id] = new(id, workflow.Key, value, schema, "unconditional"); }
                    catch (InvalidOperationException) { /* A missing original result cannot be replaced by an aggregate. */ }
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
        return Index(workflow, preparation, graph, consumer).Where(p =>
                !(p.Value.Value.Kind == "output" && p.Value.Value.ResultChannel is null or "default" && p.Value.Schema.Count == 0 &&
                    PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Any(n => n.Key == p.Value.Value.Source && n.StructuredOutput is not null)) &&
                (p.Value.Value.Path.Count == 0 || !sequences.Contains(p.Value.Value.Source ?? "")))
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
        if (obj["kind"]?.GetValue<string>() == "binding" && obj["reference"] is JsonValue referenceValue && referenceValue.TryGetValue<string>(out var identifier))
        {
            var normalized = PlanningOutputBindings.NormalizeIdentifier(identifier);
            if (bindings.ContainsKey(normalized))
            {
                var retained = obj.DeepClone().AsObject(); retained["reference"] = normalized; return retained;
            }
        }
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
        if (obj["kind"]?.GetValue<string>() is "input" or "output" or "loop_item" or "loop_index" or "loop_previous" or "artifact_collection")
        {
            var reference = JsonSerializer.Deserialize(obj, PlanningJsonContext.Default.PlanningValue)!;
            var id = PlanningOutputBindings.Id(reference);
            if (bindings.ContainsKey(id)) return new JsonObject { ["kind"] = "binding", ["reference"] = id };
        }
        return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Transport(p.Value, bindings))));
    }

    internal sealed class BindingException(string location, string reference, string? reason = null) : InvalidOperationException(reason ?? "Binding '" + reference + "' is unavailable in this operation's current execution scope. Select an eligible binding or repair its producer's failure path.")
    {
        internal string Location { get; } = location;
        internal string Reference { get; } = reference;
    }

    internal static JsonNode? Expand(JsonNode? value, IReadOnlyDictionary<string, PlanningBinding> bindings, string location = "")
    {
        if (value is JsonArray array) return new JsonArray(array.Select((v, index) => Expand(v, bindings, location + "/" + index)).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        if (obj["kind"]?.GetValue<string>() == "binding")
        {
            if (!bindings.TryGetValue(PlanningOutputBindings.NormalizeIdentifier(obj["reference"]!.GetValue<string>()), out var binding)) throw new BindingException(location, obj["reference"]!.GetValue<string>());
            return JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue);
        }
        return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Expand(p.Value, bindings, location + "/" + PlanningSchemaReferences.Escape(p.Key)))));
    }

    internal static PlanningDataflowContract Describe(PlanningGraph graph, PlanningPreparation preparation)
    {
        var contract = new PlanningDataflowContract();
        foreach (var workflow in graph.Workflows)
        {
            var index = Index(workflow, preparation, graph); contract.Bindings.AddRange(index.Values);
            var loopBodyKeys = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => n.Type is "loop.sequential" or "loop.parallel")
                .SelectMany(n => PlanningGraphCompiler.Enumerate(n.Steps)).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                if (loopBodyKeys.Contains(node.Key))
                    foreach (var binding in Index(workflow, preparation, graph, node.Key).Values.Where(b => b.Value.Kind is "loop_item" or "loop_index" or "loop_previous"))
                        if (!contract.Bindings.Any(b => b.Id == binding.Id && b.WorkflowKey == binding.WorkflowKey)) contract.Bindings.Add(binding);
                var consumed = References(node.Input).Concat(node.Expr is null ? [] : References(node.Expr)).DistinctBy(PlanningOutputBindings.Id).ToArray();
                contract.Operations.Add(new(workflow.Key, node.Key, consumed.Select(PlanningOutputBindings.Id).ToList(), consumed.Where(v => v.Kind == "input").Select(v => v.Source!).Distinct(StringComparer.Ordinal).ToList()));
            }
        }
        contract.Fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningDataflowContract));
        return contract;
    }

    internal static IEnumerable<PlanningValue> References(PlanningValue value)
    {
        if (value.Kind is "input" or "output" or "loop_item" or "loop_index" or "loop_previous" or "artifact_collection") yield return value;
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

    internal static IReadOnlyList<PlanningDiagnostic> OperationInputFindings(PlanningGraph graph, PlanningPreparation preparation)
    {
        var findings = new List<PlanningDiagnostic>();
        foreach (var workflow in graph.Workflows)
        {
            var root = "/workflows/" + graph.Workflows.IndexOf(workflow);
            var located = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")).ToArray();
            foreach (var (node, path) in located)
            {
                var required = PlanningOperationCompositions.RequiredInputs(workflow, node, preparation);
                var composition = PlanningOperationCompositions.Owner(workflow, node, preparation);
                var terminal = composition?.Steps[^1] == node;
                if (required.Count == 0 && !terminal) continue;
                var (dependencies, visited) = OperationDependencies(workflow, node, preparation, graph);
                if (terminal)
                    foreach (var missing in composition!.Steps.SkipLast(1).Where(n => !visited.Contains(n.Key)))
                        findings.Add(new("COMPOSITION_INPUT_BINDING_MISSING", path + "/input", "The final result of this owned operation must consume intermediate producer '" + missing.Key + "'. Preserve its original result or a validated dependency; an unused sibling cannot establish completion."));
                foreach (var missing in required.Except(dependencies, StringComparer.Ordinal))
                    findings.Add(new("OPERATION_INPUT_BINDING_MISSING", path + "/input", "This operation must consume the result of locked upstream operation '" + missing + "', directly or through a validated dependency. Eligible producer nodes: " +
                        string.Join(", ", located.Where(p => p.Node.OperationIds.Contains(missing) || preparation.Capabilities.FirstOrDefault(c => c.Id == p.Node.CapabilityId)?.OperationIds.Contains(missing) == true).Select(p => p.Node.Key)) + ". An unrelated result cannot substitute for this dependency."));
            }
        }
        return findings;
    }

    internal static (HashSet<string> Operations, HashSet<string> Nodes) OperationDependencies(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation, PlanningGraph? graph = null)
    {
        var located = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToArray();
        var path = located.Single(p => p.Node == node).Path;
        var dependencies = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        var resolve = PlanningGraphValidation.OptionalValueContractResolver(graph ?? new() { Workflows = [workflow] }, workflow, preparation);
        IEnumerable<PlanningValue> ValidInputReferences(PlanningNode current)
        {
            if (current.Type != "mcp.call" || current.Input.Kind != "object" ||
                preparation.Capabilities.FirstOrDefault(c => c.Id == current.CapabilityId)?.InputSchema["properties"] is not JsonObject contracts)
                return References(current.Input);
            var values = new List<PlanningValue>();
            foreach (var member in current.Input.Members)
            {
                if (member.Name != "request" || member.Value.Kind != "object") { values.AddRange(References(member.Value)); continue; }
                foreach (var argument in member.Value.Members)
                {
                    if (contracts[argument.Name] is not JsonObject expected) continue;
                    try
                    {
                        var valid = PlanningGraphValidation.IsLiteral(argument.Value)
                            ? PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(argument.Value), expected).Count == 0
                            : resolve(argument.Value) is not { } actual || PlanningGraphValidation.TypesFit(actual, expected);
                        if (valid) values.AddRange(References(argument.Value));
                    }
                    catch (InvalidOperationException) { /* An unresolved or mistyped argument cannot establish a consumed operation. */ }
                }
            }
            return values;
        }
        void Visit(PlanningNode current, bool source)
        {
            if (!visited.Add(current.Key)) return;
            if (source) dependencies.UnionWith(current.OperationIds.Concat(preparation.Capabilities.FirstOrDefault(c => c.Id == current.CapabilityId)?.OperationIds ?? []));
            foreach (var value in ValidInputReferences(current).Concat(current.Expr is null ? [] : References(current.Expr)))
                if (value.Kind is "output" or "loop_item" or "loop_index" or "loop_previous" or "artifact_collection" && located.FirstOrDefault(p => p.Node.Key == value.Source).Node is { } producer) Visit(producer, true);
            foreach (var child in current.Steps.Concat(current.Default).Concat(current.Cases.SelectMany(c => c.Steps)).Concat(current.Branches.SelectMany(b => b.Steps))) Visit(child, true);
        }
        Visit(node, false);
        // An accepted enclosing decision is a control dependency, not a
        // fabricated argument to the conditional external operation.
        foreach (var parent in located.Where(p => path.StartsWith(p.Path + "/", StringComparison.Ordinal) && p.Node.Type == "switch"))
            if (parent.Node.Expr is { } selector)
                foreach (var reference in References(selector))
                    if (located.FirstOrDefault(p => p.Node.Key == reference.Source).Node is { } producer) Visit(producer, true);
        return (dependencies, visited);
    }

    private static bool Nullable(JsonObject schema) => schema["type"] is JsonArray types && types.Any(t => t?.ToString() == "null") ||
        (schema["anyOf"] ?? schema["oneOf"]) is JsonArray variants && variants.Any(v => v?["type"]?.ToString() == "null");

    private static IEnumerable<List<string>> Paths(JsonObject schema, List<string> path, int depth)
    {
        yield return path;
        if (depth >= 16) yield break;
        if ((schema["anyOf"] ?? schema["oneOf"]) is JsonArray alternatives)
            foreach (var alternative in alternatives.OfType<JsonObject>())
                foreach (var value in Paths(alternative, path, depth + 1).Skip(1)) yield return value;
        // These are candidates only. ResolveValueContract must prove the selected
        // field exists in every alternative before Index exposes a binding.
        if (schema["properties"] is not JsonObject properties) yield break;
        var required = (schema["required"] as JsonArray ?? []).Select(p => p?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, child) in properties)
            if (required.Contains(name) && child is JsonObject nested)
                foreach (var value in Paths(nested, path.Append(name).ToList(), depth + 1)) yield return value;
    }
}
