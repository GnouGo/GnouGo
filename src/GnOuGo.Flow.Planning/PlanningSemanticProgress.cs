using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningSemanticProgress
{
    internal static void RestoreBaseline(PlanningSnapshot state)
    {
        if (state.Graph is null || state.ConstructionUnits.Count == 0 || state.ConstructionUnits.Any(u => u.Status != "superseded" && u.ContractVersion != PlanningDataflow.ContractVersion)) return;
        var hash = PlanningGraphCompiler.Fingerprint(state.Graph);
        var baseline = state.Attempts.LastOrDefault(a => a.Retained && a.Phase == PlanningStatus.Validating && a.Stage == 9 && a.CandidateHash == hash);
        if (baseline is null || !baseline.Diagnostics.Any(d => d.Required)) return;
        state.BestGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph);
        state.BestDiagnostics = baseline.Diagnostics.ToList(); state.BestScenarios = state.Scenarios.ToList();
        state.BestFragments = new(state.Fragments, StringComparer.Ordinal);
    }

    // Model-written finding codes are not stable check identifiers. An issue in
    // unchanged code is still outstanding even if a later review omits/renames it,
    // and discovering another such issue does not undo an unrelated valid repair.
    internal static bool Preserve(PlanningGraph before, PlanningGraph after, IReadOnlyList<PlanningDiagnostic> previous, List<PlanningDiagnostic> current)
    {
        var priorIds = previous.Select(Id).ToHashSet(StringComparer.Ordinal);
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Unchanged(string location) => cache.TryGetValue(location, out var same) ? same : cache[location] = UnchangedInput(location, before, after);
        var progress = previous.Any(p => p.Required && InputWasEdited(p.Location, before, after) &&
            !current.Any(d => d.Required && Overlaps(p.Location, d.Location))) &&
            current.Where(d => d.Required && !priorIds.Contains(Id(d))).All(d => Unchanged(d.Location));
        foreach (var retained in previous.Where(d => d.Required && Unchanged(d.Location)))
            if (!current.Any(d => Id(d) == Id(retained))) current.Add(retained);
        return progress;
    }

    private static string Id(PlanningDiagnostic diagnostic) => diagnostic.Code + "|" + diagnostic.Location;
    private static bool Overlaps(string a, string b) => a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);

    private static bool InputWasEdited(string location, PlanningGraph before, PlanningGraph after)
    {
        static IEnumerable<(PlanningNode Node, string Path)> Nodes(PlanningGraph graph) => graph.Workflows.SelectMany((w, wi) =>
            PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps").Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally")));
        var owner = Nodes(before).Where(n => location == n.Path + "/input" || location.StartsWith(n.Path + "/input/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault();
        var replacement = Nodes(after).SingleOrDefault(n => n.Path == owner.Path).Node;
        return owner.Node is not null && replacement is not null && owner.Node.Key == replacement.Key &&
            !JsonNode.DeepEquals(Serialize(owner.Node)["input"], Serialize(replacement)["input"]);
    }

    internal static bool UnchangedInput(string location, PlanningGraph before, PlanningGraph after)
    {
        var original = JsonSerializer.SerializeToNode(before, PlanningJsonContext.Default.PlanningGraph)!;
        var replacement = JsonSerializer.SerializeToNode(after, PlanningJsonContext.Default.PlanningGraph)!;
        if (!JsonNode.DeepEquals(original["functions"], replacement["functions"]) || before.Workflows.Count != after.Workflows.Count) return false;
        for (var wi = 0; wi < before.Workflows.Count; wi++)
        {
            var root = "/workflows/" + wi; if (!location.StartsWith(root + "/", StringComparison.Ordinal)) continue;
            var workflow = before.Workflows[wi]; var next = after.Workflows[wi];
            if (workflow.Key != next.Key || workflow.Functions != next.Functions ||
                !JsonNode.DeepEquals(original["workflows"]![wi]!["inputs"], replacement["workflows"]![wi]!["inputs"])) return false;
            // Cross-workflow or shared-helper changes require a new assessment.
            for (var other = 0; other < before.Workflows.Count; other++)
                if (other != wi && !JsonNode.DeepEquals(original["workflows"]![other], replacement["workflows"]![other])) return false;
            var nodes = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")).ToArray();
            var replacements = PlanningGraphValidation.Located(next.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(next.Finally, root + "/finally")).ToArray();
            var owner = nodes.Where(n => location == n.Path + "/input" || location.StartsWith(n.Path + "/input/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault();
            if (owner.Node is null || replacements.SingleOrDefault(n => n.Path == owner.Path).Node is not { } current || owner.Node.Key != current.Key) return false;
            if (!JsonNode.DeepEquals(Control(owner.Node), Control(current))) return false;
            foreach (var parent in nodes.Where(n => owner.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)))
                if (replacements.SingleOrDefault(n => n.Path == parent.Path).Node is not { } newParent || !JsonNode.DeepEquals(Control(parent.Node), Control(newParent))) return false;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Dependencies(PlanningNode node)
            {
                var values = new[] { node.Input, node.If, node.Expr }.OfType<PlanningValue>()
                    .Concat(node.OnError.SelectMany(e => new[] { e.If, e.SetOutput }.OfType<PlanningValue>()));
                foreach (var value in values.SelectMany(Values))
                {
                    if (value.Kind is "expression" or "workflow") return false;
                    if (value.Kind == "input" || value.Source is null) continue;
                    if (value.Source == owner.Node.Key) return false;
                    if (!visited.Add(value.Source)) continue;
                    var producer = nodes.SingleOrDefault(n => n.Node.Key == value.Source).Node;
                    var revisedProducer = replacements.SingleOrDefault(n => n.Node.Key == value.Source).Node;
                    if (producer is null || revisedProducer is null || !JsonNode.DeepEquals(Serialize(producer), Serialize(revisedProducer)) || !Dependencies(producer)) return false;
                }
                return true;
            }
            return Dependencies(owner.Node) && nodes.Where(n => owner.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)).All(n => Dependencies(n.Node));
        }
        return false;
    }

    private static IEnumerable<PlanningValue> Values(PlanningValue value)
    {
        yield return value;
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var nested in Values(child)) yield return nested;
    }

    private static JsonObject Serialize(PlanningNode node) => JsonSerializer.SerializeToNode(node, PlanningJsonContext.Default.PlanningNode)!.AsObject();
    private static JsonObject Control(PlanningNode node)
    {
        var result = Serialize(node); result.Remove("steps"); result.Remove("default"); result.Remove("branches");
        if (result["cases"] is JsonArray cases) foreach (var item in cases.OfType<JsonObject>()) item.Remove("steps");
        return result;
    }
}
