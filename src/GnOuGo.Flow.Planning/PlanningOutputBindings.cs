using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Public exports can select only deterministically resolved, exportable producers.</summary>
internal static class PlanningOutputBindings
{
    internal sealed record Binding(PlanningValue Value, PlanningSchema Schema);
    internal static string Id(PlanningValue value) => "ref_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(
        new[] { value.Kind, value.Source ?? "", value.ResultChannel ?? "default" }.Concat(value.Path).ToArray(), PlanningJsonContext.Default.StringArray))[..20];

    internal static Dictionary<string, Binding> Index(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningGraph? graph = null)
    {
        graph ??= new() { Workflows = [workflow] };
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
        var sources = workflow.Inputs.Select(p => new PlanningValue { Kind = "input", Source = p.Name }).Concat(
            PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SelectMany(n => n.StructuredOutput is null
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
                    var resolved = resolve(value);
                    PlanningGraphValidation.RequireTyped(resolved, 0);
                    PlanningGraphCompiler.ToFlowSchema(resolved);
                    bindings[Id(value)] = new(value, PlanningGraphImporter.Schema(resolved));
                }
                catch (InvalidOperationException) { /* Missing, optional or unsupported paths are not executable exports. */ }
            }
        }
        return bindings;
    }

    private static IEnumerable<List<string>> Paths(JsonObject schema, List<string> path, int depth)
    {
        yield return path;
        if (depth >= 16 || schema["properties"] is not JsonObject properties) yield break;
        var required = (schema["required"] as JsonArray ?? []).Select(p => p?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in properties)
            if (required.Contains(name) && value is JsonObject child)
                foreach (var nested in Paths(child, path.Append(name).ToList(), depth + 1)) yield return nested;
    }
}
