using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private static IEnumerable<StepDef> ArtifactChildren(StepDef node) => (node.Steps ?? []).Concat(node.Default ?? [])
        .Concat((node.Cases ?? []).SelectMany(c => c.Steps)).Concat((node.Branches ?? []).SelectMany(b => b.Steps));

    private static bool TryResolveLoopArtifact(WorkflowDocument document, string workflow, string? consumer, string path,
        Func<JsonNode?, string?, ArtifactResolution> resolve, out ArtifactResolution resolution)
    {
        resolution = ArtifactResolution.Unproven;
        if (consumer is null || !document.Workflows.TryGetValue(workflow, out var definition)) return false;
        var ancestors = new List<StepDef>();
        bool Find(IEnumerable<StepDef> steps)
        {
            foreach (var step in steps)
            {
                if (step.Id == consumer) return true;
                ancestors.Add(step);
                if (Find(ArtifactChildren(step))) return true;
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            return false;
        }
        if (!Find(definition.Steps.Concat(definition.Finally))) return false;
        foreach (var loop in ancestors.AsEnumerable().Reverse().Where(s => s.Type is "loop.sequential" or "loop.parallel"))
        {
            var prefix = "data." + (loop.ItemVar ?? "item");
            if (path == "data._loop.item" || path.StartsWith("data._loop.item.", StringComparison.Ordinal)) prefix = "data._loop.item";
            if (path != prefix && !path.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
            var projection = path == prefix ? [] : path[(prefix.Length + 1)..].Split('.');
            if (projection.Any(p => !IsExactArtifactPathSegment(p))) return true;
            var items = loop.Input?["items"];
            if (items is JsonArray array && array.Count > 0)
            {
                var producers = new HashSet<PlannedArtifactProducer>(); var callerInput = false;
                foreach (var item in array)
                {
                    var result = resolve(ResolveArtifactCallerArgument(item, projection), loop.Id);
                    if (!result.Proven) return true;
                    producers.UnionWith(result.Producers); callerInput |= result.UsesCallerInput;
                }
                resolution = new(true, producers, callerInput);
                return true;
            }
            if (items is not JsonValue scalar || !scalar.TryGetValue<string>(out var expression)) return true;
            var source = TrimWorkflowExpression(expression).Split('.');
            if (source is not ["data", "steps", var sourceId, "results"] || projection.Length == 0) return true;
            var producerLoop = EnumerateSteps(definition.Steps.Concat(definition.Finally)).FirstOrDefault(s => s.Id == sourceId);
            if (producerLoop?.Type is not ("loop.sequential" or "loop.parallel")) return true;
            // Each iteration preserves its child result envelopes. Trace the same declared
            // child producer for every item, without treating an arbitrary projection as evidence.
            var producer = producerLoop.Steps?.FirstOrDefault(s => s.Id == projection[0]);
            if (producer is null) return true;
            resolution = resolve(AppendExactArtifactExpressionPath("${data.steps." + producer.Id + "}", projection.Skip(1).ToArray()), producer.Id);
            return true;
        }
        return false;
    }
}
