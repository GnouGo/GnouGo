using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Data addresses are compiler-owned. Model transport selects catalog identifiers only.</summary>
internal static class PlanningDataflow
{
    internal const string WorkflowOutputs = "$outputs";

    internal static Dictionary<string, PlanningBinding> Index(PlanningWorkflow workflow, PlanningCatalog catalog, PlanningGraph graph, string? consumer = null, bool includeUnresolved = false)
    {
        var result = new Dictionary<string, PlanningBinding>(StringComparer.Ordinal);
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
        var locations = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToDictionary(p => p.Node.Key, p => p.Path, StringComparer.Ordinal);
        var consumerIndex = consumer is null or WorkflowOutputs ? nodes.Length : Array.FindIndex(nodes, n => n.Key == consumer);
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, catalog);
        var loopSources = consumer is null or WorkflowOutputs ? Enumerable.Empty<PlanningValue>() : nodes.Where(n => n.Type is "loop.sequential" or "loop.parallel" &&
            locations[consumer].StartsWith(locations[n.Key] + "/steps/", StringComparison.Ordinal))
            .SelectMany(n => new[] { new PlanningValue { Kind = "loop_item", Source = n.Key }, new PlanningValue { Kind = "loop_index", Source = n.Key } });
        var previousSources = consumer is null or WorkflowOutputs ? Enumerable.Empty<PlanningValue>() : nodes.Where(n => n.Type == "loop.sequential" &&
            (n.Key == consumer || locations[consumer].StartsWith(locations[n.Key] + "/steps/", StringComparison.Ordinal)))
            .Select(n => new PlanningValue { Kind = "loop_previous", Source = n.Key });
        previousSources = previousSources.Concat(nodes.Where(n => n.Key == consumer && n.Type == "loop.sequential").Select(n => new PlanningValue { Kind = "loop_index", Source = n.Key }));
        var sources = workflow.Inputs.Select(p => new PlanningValue { Kind = "input", Source = p.Name }).Concat(loopSources).Concat(previousSources).Concat(nodes.Take(Math.Max(0, consumerIndex)).Where(n => Available(n.Key)).SelectMany(n => n.StructuredOutput is null
            ? new[] { new PlanningValue { Kind = "output", Source = n.Key } }
            : new[] { new PlanningValue { Kind = "output", Source = n.Key }, new PlanningValue { Kind = "output", Source = n.Key, ResultChannel = "structured" } }))
            .Concat(nodes.Take(Math.Max(0, consumerIndex)).Where(n => n.Type == "mcp.call" && n.OnError.Any(h => h.Action == "continue") && Available(n.Key))
                .Select(n => new PlanningValue { Kind = "output", Source = n.Key, ResultChannel = "envelope" }));
        foreach (var source in sources)
        {
            JsonObject schema;
            try { schema = resolve(source); }
            catch (InvalidOperationException)
            {
                if (includeUnresolved) result[PlanningBindingIdentity.Id(source)] = new(PlanningBindingIdentity.Id(source), workflow.Key, source, new(), "opaque");
                continue;
            }
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
                    var conditional = consumer is null && source.Kind == "output" && (Guards(source.Source!).Any() ||
                        new[] { "/cases/", "/default/", "/branches/" }.Any(marker => locations[source.Source!].Contains(marker, StringComparison.Ordinal)));
                    var availability = conditional ? "conditional" : contract.Count == 0 ? "opaque" : Nullable(contract) ? "nullable" : "unconditional";
                    if (source.Kind == "input" && workflow.Inputs.Single(p => p.Name == source.Source) is { Required: false } input &&
                        (input.Default is null || !PlanningGraphValidation.IsLiteral(input.Default) || PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(input.Default), PlanningGraphCompiler.ToJsonSchema(input.Schema, catalog)).Count > 0)) availability = "absent";
                    var id = PlanningBindingIdentity.Id(value);
                    result[id] = new(id, workflow.Key, value, contract, availability);
                }
                catch (InvalidOperationException) { /* Optional/conditional fields need explicit narrowing, never a guessed address. */ }
            }
        }
        foreach (var loop in nodes.Take(Math.Max(0, consumerIndex)).Where(n => n.Type is "loop.sequential" or "loop.parallel" && Available(n.Key)))
            foreach (var child in loop.Steps.Where(n => n.Type == "mcp.call"))
                foreach (var artifact in catalog.Capabilities.FirstOrDefault(c => c.Id == child.CapabilityId)?.ArtifactContract?.Produces.Where(p => p.Encoding == "json_array") ?? [])
                {
                    var value = new PlanningValue
                    {
                        Kind = "artifact_collection",
                        Source = loop.Key,
                        Path = new[] { child.Key, "response" }.Concat(artifact.Pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))).ToList()
                    };
                    try { var schema = resolve(value); var id = PlanningBindingIdentity.Id(value); result[id] = new(id, workflow.Key, value, schema, "unconditional"); }
                    catch (InvalidOperationException) { /* A missing original result cannot be replaced by an aggregate. */ }
                }
        return result;

        bool Available(string key)
        {
            if (consumer is null) return true;
            foreach (var condition in Guards(key))
            {
                if (consumer == WorkflowOutputs && workflow.Finally.Any(n => n.Key == key) &&
                    PlanningGraphTopology.FinalizerAvailableOnSuccess(nodes.Single(n => n.Key == key), workflow)) continue;
                if (consumer != WorkflowOutputs && locations[consumer].StartsWith("/finally/", StringComparison.Ordinal) &&
                    PlanningGraphTopology.GuardsFinalizerSource(nodes.Single(n => n.Key == consumer), key)) continue;
                if (consumer == WorkflowOutputs || !Guards(consumer).Any(guard => JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(condition, PlanningJsonContext.Default.PlanningValue),
                    JsonSerializer.SerializeToNode(guard, PlanningJsonContext.Default.PlanningValue)))) return false;
            }
            // Results inside conditional/parallel/loop bodies are addressed through the completed
            // container outside that body. A direct producer is available only in the same body.
            var path = locations[key]; var target = consumer == WorkflowOutputs ? "/outputs" : locations[consumer];
            // Main execution can stop before any producer; finalizers cannot assume those results exist.
            if (target.StartsWith("/finally/", StringComparison.Ordinal) && path.StartsWith("/steps/", StringComparison.Ordinal) &&
                !PlanningGraphTopology.GuardsFinalizerSource(nodes.Single(n => n.Key == consumer), key)) return false;
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

    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningCatalog catalog)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{wi}/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{wi}/finally")))
            {
                foreach (var finding in Check(PlanningGraphTopology.References(node), node.Key, path)) yield return finding;
                var capability = catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                var request = PlanningGraphValidation.Member(node.Input, "request");
                foreach (var artifact in capability?.ArtifactContract?.Consumes ?? [])
                {
                    var value = request;
                    foreach (var part in artifact.Pointer.Split('/').Skip(1))
                        value = value?.Members.FirstOrDefault(m => m.Name == part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))?.Value;
                    if (value is null && !artifact.Required) continue;
                    if (value is null || !PlanningArtifactBindings.Proves(workflow, value, artifact.Kind, catalog, graph, new()))
                        yield return new("ARTIFACT_BINDING_INVALID", path + "/input/request" + artifact.Pointer, "Bind the declared artifact from an available producer of kind '" + artifact.Kind + "'.");
                }
            }
            foreach (var output in workflow.Outputs)
                foreach (var finding in Check(References(output.Value), WorkflowOutputs, $"/workflows/{wi}/outputs/{workflow.Outputs.IndexOf(output)}/value")) yield return finding;
            IEnumerable<PlanningDiagnostic> Check(IEnumerable<PlanningValue> references, string consumer, string path)
            {
                Dictionary<string, PlanningBinding>? available;
                try { available = Index(workflow, catalog, graph, consumer); }
                catch (InvalidOperationException) { yield break; }
                Dictionary<string, PlanningBinding>? unresolved = null;
                foreach (var reference in references)
                    if (!available.TryGetValue(PlanningBindingIdentity.Id(new PlanningValue { Kind = reference.Kind, Source = reference.Source, ResultChannel = reference.ResultChannel }), out var binding) || binding.Availability is "absent" or "conditional")
                    {
                        string? rule = null;
                        if (reference.Kind == "output")
                        {
                            unresolved ??= Index(workflow, catalog, graph, consumer, includeUnresolved: true);
                            var id = PlanningBindingIdentity.Id(new() { Kind = reference.Kind, Source = reference.Source, ResultChannel = reference.ResultChannel });
                            rule = (unresolved.TryGetValue(id, out var source) && source.Availability == "opaque" ? "producer:" : "availability:") + reference.Source;
                        }
                        yield return new("BINDING_UNAVAILABLE", path, "The binding is not available in this scope: " + reference.Kind + ":" + reference.Source + "/" + string.Join("/", reference.Path),
                            Rule: rule);
                    }
            }
        }
    }

    internal static IEnumerable<PlanningValue> References(PlanningValue value)
    {
        if (value.Kind is "input" or "output" or "loop_item" or "loop_index" or "loop_previous" or "artifact_collection") yield return value;
        var members = value.Members.AsEnumerable();
        foreach (var child in members.Select(m => m.Value).Concat(value.Items)) foreach (var reference in References(child)) yield return reference;
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
