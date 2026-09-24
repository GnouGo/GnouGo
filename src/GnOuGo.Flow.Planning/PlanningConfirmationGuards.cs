using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

/// <summary>One entrypoint gate encloses the entire execution, including nested calls and cleanup.</summary>
internal static class PlanningConfirmationGuards
{
    internal const string Body = "__planning_body", Confirm = "__planning_confirm", Assert = "__planning_permission", Call = "__planning_run";
    internal static bool Required(PlanningGraph graph, PlanningCatalog catalog) => catalog.Policy.RequireExternalConfirmation &&
        graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Any(n => n.Type is "mcp.call" or "agent.run" && catalog.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId)?.EffectKind is not ("read" or "none"));
    internal static void Apply(PlanningGraph graph, PlanningCatalog catalog)
    {
        if (!Required(graph, catalog)) return;
        if (graph.Workflows.Any(w => w.Key == Body)) return;
        var main = graph.Workflows.Single(w => w.Key == graph.Entrypoint);
        main.Key = Body;
        graph.Workflows.Insert(0, Wrapper(main, graph.Entrypoint, graph.Summary));
    }
    internal static PlanningGraph UserGraph(PlanningGraph executable)
    {
        var graph = JsonSerializer.Deserialize(JsonSerializer.Serialize(executable, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        if (graph.Workflows.SingleOrDefault(w => w.Key == Body) is { } body)
        {
            graph.Workflows.RemoveAll(w => w.Key == graph.Entrypoint);
            body.Key = graph.Entrypoint;
        }
        return graph;
    }
    private static PlanningWorkflow Wrapper(PlanningWorkflow body, string entrypoint, string summary) => new()
    {
        Key = entrypoint, Purpose = "Confirm external effects before executing the workflow.", Inputs = body.Inputs,
        Outputs = body.Outputs.Select(o => new PlanningOutput { Name = o.Name, Schema = o.Schema,
            Value = new() { Kind = "output", Source = Call, Path = [o.Name] } }).ToList(),
        Steps =
        [
            new() { Key = Confirm, Type = "human.input", InternalRole = "confirmation", Input = PlanningJsonTransport.Literal(HumanInputContract.ConfirmationInput(summary)) },
            new() { Key = Assert, Type = "assert.non_null", InternalRole = "permission", Input = new() { Kind = "object", Members =
                [new("value", new() { Kind = "compute", Text = "permission === true ? true : null", Members =
                    [new("permission", new() { Kind = "output", Source = Confirm, Path = ["response"] })] })] } },
            new() { Key = Call, Type = "workflow.call", InternalRole = "approved_body", Input = new() { Kind = "object", Members =
                [new("ref", new() { Kind = "workflow", Source = Body }), new("args", new() { Kind = "expression", Text = "data.inputs" })] } }
        ]
    };
    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningCatalog catalog)
    {
        if (!Required(graph, catalog)) yield break;
        var body = graph.Workflows.SingleOrDefault(w => w.Key == Body);
        var main = graph.Workflows.SingleOrDefault(w => w.Key == graph.Entrypoint);
        if (body is null || main is null ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(main, PlanningJsonContext.Default.PlanningWorkflow),
                JsonSerializer.SerializeToNode(Wrapper(body, graph.Entrypoint, graph.Summary), PlanningJsonContext.Default.PlanningWorkflow)))
            yield return new("CONFIRMATION_REQUIRED", "/entrypoint", "The workflow must enter through its unmodified host-owned confirmation gate.");
    }
}
