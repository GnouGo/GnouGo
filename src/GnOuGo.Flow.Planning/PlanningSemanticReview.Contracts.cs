using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningSemanticReview
{
    internal static JsonObject SemanticValueContracts(PlanningGraph graph, PlanningPreparation preparation)
    {
        var schemas = new JsonObject(); var references = new JsonArray(); var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var workflow in graph.Workflows)
        {
            var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
            var roots = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SelectMany(Values)
                .Concat(workflow.Outputs.Select(o => o.Value));
            foreach (var value in roots.SelectMany(PlanningDataflow.References).DistinctBy(PlanningBindingIdentity.Id))
            {
                var item = new JsonObject
                {
                    ["workflow"] = workflow.Key,
                    ["kind"] = value.Kind,
                    ["source"] = value.Source,
                    ["resultChannel"] = value.ResultChannel,
                    ["path"] = new JsonArray(value.Path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray())
                };
                try
                {
                    var schema = resolve(value); var fingerprint = schema.ToJsonString();
                    if (!identities.TryGetValue(fingerprint, out var id))
                    { id = "v" + identities.Count; identities[fingerprint] = id; schemas[id] = schema.DeepClone(); }
                    item["schema"] = new JsonObject { ["$ref"] = "#/schemas/" + id };
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
                { item["unresolved"] = ex.Message; }
                references.Add((JsonNode)item);
            }
        }
        return new() { ["references"] = references, ["schemas"] = schemas };
    }

    private static IEnumerable<PlanningValue> Values(PlanningNode node)
    {
        yield return node.Input;
        if (node.Expr is { } expr) yield return expr;
        if (node.If is { } guard) yield return guard;
        foreach (var error in node.OnError)
        {
            if (error.If is { } condition) yield return condition;
            if (error.SetOutput is { } fallback) yield return fallback;
        }
        foreach (var branch in node.Cases)
            if (branch.When is { } when) yield return when;
    }
}
