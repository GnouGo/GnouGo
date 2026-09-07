using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Artifact identity comes from declared producer provenance, not matching scalar types.</summary>
internal static class PlanningArtifactBindings
{
    internal static JsonObject? ArgumentSchema(PlanningWorkflow workflow, PlanningNode node, string argument, PlanningPreparation preparation, PlanningGraph graph)
    {
        var requirements = preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.ArtifactContract?.Consumes
            .Where(c => c.Required && c.Pointer == "/" + PlanningSchemaReferences.Escape(argument)).ToArray();
        if (requirements is not { Length: > 0 }) return null;
        var eligible = PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values.Where(binding =>
            requirements.All(required => Proves(workflow, binding.Value, required.Kind, preparation, graph, new(StringComparer.Ordinal)))).Select(b => b.Id).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("No available original producer binding proves required artifact " +
            string.Join(", ", requirements.Select(r => r.Kind)) + " for argument '" + argument + "'. A transformed result or matching string type cannot establish artifact identity.");
        return PlanningDataflow.BindingSchema(eligible);
    }

    internal static bool Proves(PlanningWorkflow workflow, PlanningValue value, string kind, PlanningPreparation preparation, PlanningGraph graph, HashSet<string> visited)
    {
        var key = workflow.Key + ":" + PlanningOutputBindings.Id(value) + ":" + kind;
        if (!visited.Add(key)) return false;
        try
        {
            if (value.Kind != "output" || value.ResultChannel == "structured") return false;
            var producer = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == value.Source);
            if (producer is null) return false;
            if (producer.Type == "mcp.call")
                return preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.ArtifactContract?.Produces.Any(p =>
                    p.Kind == kind && p.Pointer == "/" + string.Join("/", value.Path.Select(PlanningSchemaReferences.Escape))) == true;
            if (producer.Type is "set" or "assert.non_null")
            {
                var source = producer.Type == "set" ? producer.Input : PlanningGraphValidation.Member(producer.Input, "value");
                var selected = Select(source, value.Path);
                return selected is not null && Proves(workflow, selected, kind, preparation, graph, visited);
            }
            if (producer.Type == "workflow.call" && value.Path.Count > 0)
            {
                var target = graph.Workflows.FirstOrDefault(w => w.Key == PlanningGraphValidation.Member(producer.Input, "ref")?.Source);
                var output = target?.Outputs.FirstOrDefault(p => p.Name == value.Path[0]);
                var selected = Select(output?.Value, value.Path.Skip(1));
                return target is not null && selected is not null && Proves(target, selected, kind, preparation, graph, visited);
            }
            return false;
        }
        finally { visited.Remove(key); }
    }

    private static PlanningValue? Select(PlanningValue? source, IEnumerable<string> path)
    {
        var remaining = path.ToArray();
        for (var i = 0; i < remaining.Length && source is not null; i++)
        {
            if (source.Kind is "output" or "input") return new() { Kind = source.Kind, Source = source.Source, ResultChannel = source.ResultChannel, Path = source.Path.Concat(remaining.Skip(i)).ToList() };
            if (source.Kind == "object") source = PlanningGraphValidation.Member(source, remaining[i]);
            else return null;
        }
        return source;
    }
}
