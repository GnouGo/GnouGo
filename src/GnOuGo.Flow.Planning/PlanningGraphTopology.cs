using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
internal static class PlanningGraphTopology
{
    internal static IEnumerable<PlanningValue> References(PlanningNode node) => PlanningDataflow.References(node.Input)
        .Concat(node.If is null ? [] : PlanningDataflow.References(node.If)).Concat(node.Expr is null ? [] : PlanningDataflow.References(node.Expr))
        .Concat(node.Cases.Where(c => c.When is not null).SelectMany(c => PlanningDataflow.References(c.When!)))
        .Concat(node.OnError.SelectMany(e => (e.If is null ? Enumerable.Empty<PlanningValue>() : PlanningDataflow.References(e.If)).Concat(e.SetOutput is null ? [] : PlanningDataflow.References(e.SetOutput))));
    internal static IEnumerable<string> ReferencedStages(PlanningValue value)
    {
        foreach (var reference in PlanningDataflow.References(value).Where(v => v.Kind == "output" && v.Source is not null)) yield return reference.Source!;
        if (value.Kind == "expression")
        {
            Acornima.Ast.Node? expression = null;
            try { expression = new Acornima.Parser().ParseExpression(value.Text ?? ""); }
            catch (Acornima.ParseErrorException) { }
            if (expression is not null) foreach (var key in Names(expression)) yield return key;
        }
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items))
            foreach (var key in ReferencedStages(child)) yield return key;
        static IEnumerable<string> Names(Acornima.Ast.Node node)
        {
            if (node is Acornima.Ast.MemberExpression { Object: Acornima.Ast.MemberExpression { Object: Acornima.Ast.Identifier { Name: "data" }, Property: Acornima.Ast.Identifier { Name: "steps" }, Computed: false } } member)
            {
                if (member.Property is Acornima.Ast.Identifier id && !member.Computed) yield return id.Name;
                if (member.Property is Acornima.Ast.StringLiteral text && member.Computed) yield return text.Value;
            }
            foreach (var child in node.ChildNodes) foreach (var key in Names(child)) yield return key;
        }
    }
    internal static IEnumerable<PlanningValue> Values(PlanningNode node) => new[] { node.Input, node.If, node.Expr }
        .Concat(node.Cases.Select(c => c.When)).Concat(node.OnError.SelectMany(e => new[] { e.If, e.SetOutput }))
        .OfType<PlanningValue>();
    private static PlanningValue AvailabilityGuard(IEnumerable<string> sources) => new()
    {
        Kind = "expression", Text = string.Join(" && ", sources.Order(StringComparer.Ordinal).Select(s => "data.steps[" + JsonSerializer.Serialize(s, PlanningJsonContext.Default.String) + "] != null"))
    };
    internal static bool GuardsFinalizerSource(PlanningNode node, string source)
    {
        var guard = node.If;
        return guard?.Kind == "expression" && guard.Text?.Split(" && ", StringSplitOptions.None)
            .Contains(AvailabilityGuard([source]).Text, StringComparer.Ordinal) == true;
    }
    internal static bool FinalizerAvailableOnSuccess(PlanningNode node, PlanningWorkflow workflow)
    {
        return Available(node, new(StringComparer.Ordinal));
        bool Available(PlanningNode current, HashSet<string> visiting)
        {
            if (!visiting.Add(current.Key)) return false;
            try
            {
                if (current.If is null) return true;
                var required = References(current).Where(v => v.Kind == "output").Select(v => v.Source!).Distinct().ToArray();
                if (required.Length == 0 || current.If.Kind != "expression" ||
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(current.If, PlanningJsonContext.Default.PlanningValue),
                        JsonSerializer.SerializeToNode(AvailabilityGuard(required), PlanningJsonContext.Default.PlanningValue))) return false;
                foreach (var id in required)
                {
                    var producer = workflow.Steps.Concat(workflow.Finally).SingleOrDefault(n => n.Key == id);
                    if (producer is null || producer.OnError.Any(h => h.Action == "continue") ||
                        !(producer.Type is "mcp.call" or "llm.call" or "workflow.call" or "value.validate" || producer.Type == "set" && producer.Input.Kind == "object") ||
                        (workflow.Finally.Contains(producer) ? !Available(producer, visiting) : producer.If is not null)) return false;
                }
                return true;
            }
            finally { visiting.Remove(current.Key); }
        }
    }
}
