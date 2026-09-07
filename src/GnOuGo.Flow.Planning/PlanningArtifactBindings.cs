using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Artifact identity comes from declared producer provenance, not matching scalar types.</summary>
internal static class PlanningArtifactBindings
{
    internal static IEnumerable<PlanningDiagnostic> PrerequisiteFindings(PlanningGraph graph, PlanningPreparation preparation)
    {
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            var located = PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{wi}/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{wi}/finally")).ToArray();
            foreach (var (producer, path) in located)
            {
                if (producer.Type != "mcp.call" || !producer.OnError.Any(h => h.Action == "continue")) continue;
                var produced = preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.ArtifactContract?.Produces;
                if (produced is null) continue;
                var consumers = located.Where(p => p.Node != producer && preparation.Capabilities.FirstOrDefault(c => c.Id == p.Node.CapabilityId)?.ArtifactContract?.Consumes
                    .Any(c => c.Required && produced.Any(a => a.Kind == c.Kind)) == true).ToArray();
                if (consumers.Length == 0) continue;
                yield return new("ARTIFACT_FAILURE_PATH_UNPROVEN", path + "/onError",
                    "A required downstream artifact comes from this original producer. Continuing after its failure cannot manufacture that artifact in a fallback or structured result. " +
                    "Fail closed at this producer while retaining workflow cleanup, or return to behavior review to establish a guarded consumer and an explicit failure route. A copied value does not prove artifact identity.",
                    ValidationStage: "dataflow");
            }
        }
    }

    internal static JsonObject? ArgumentSchema(PlanningWorkflow workflow, PlanningNode node, string argument, PlanningPreparation preparation, PlanningGraph graph)
    {
        var requirements = preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.ArtifactContract?.Consumes
            .Where(c => c.Required && c.Pointer == "/" + PlanningSchemaReferences.Escape(argument)).ToArray();
        if (requirements is not { Length: > 0 }) return null;
        var eligible = PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values.Where(binding =>
            requirements.All(required => Proves(workflow, binding.Value, required.Kind, preparation, graph, new(StringComparer.Ordinal)))).Select(b => b.Id).ToArray();
        if (eligible.Length == 0) throw new UnresolvedArtifactException("No available original producer binding proves required artifact " +
            string.Join(", ", requirements.Select(r => r.Kind)) + " for argument '" + argument + "'. A transformed result or matching string type cannot establish artifact identity.");
        return PlanningDataflow.BindingSchema(eligible);
    }

    internal sealed class UnresolvedArtifactException(string message) : InvalidOperationException(message);

    internal static bool Proves(PlanningWorkflow workflow, PlanningValue value, string kind, PlanningPreparation preparation, PlanningGraph graph, HashSet<string> visited)
    {
        return PlanningValueProvenance.Proves(workflow, value, graph, (producer, reference) => producer.Type == "mcp.call" &&
            preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.ArtifactContract?.Produces.Any(p =>
                p.Kind == kind && p.Pointer == "/" + string.Join("/", reference.Path.Select(PlanningSchemaReferences.Escape))) == true);
    }
}
