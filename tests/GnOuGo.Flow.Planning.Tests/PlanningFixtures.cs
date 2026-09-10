using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

internal static class PlanningFixtures
{
    internal static System.Text.Json.Nodes.JsonObject Workflow(PlanningWorkflow workflow) => PlanningModelValues.Compact(System.Text.Json.JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow))!.AsObject();
    // Fixture setup explicitly models prior human acceptance; production never infers approval from a graph.
    internal static void Accept(PlanningSnapshot state)
    {
        var graph = state.Graph!;
        state.BehaviorPlan = new()
        {
            Summary = graph.Summary,
            Entrypoint = graph.Entrypoint,
            Workflows = graph.Workflows.Select(w => new PlanningBehaviorWorkflow
            {
                Key = w.Key,
                Purpose = w.Purpose,
                OperationIds = w.OperationIds.ToList(),
                Inputs = w.Inputs.Select(p => new PlanningBehaviorPort(p.Name, p.Name, p.Required)).ToList(),
                Outputs = w.Outputs.Select(p => new PlanningBehaviorPort(p.Name, p.Name, true)).ToList(),
                Steps = w.Steps.Select(Node).ToList(),
                Finally = w.Finally.Select(Node).ToList()
            }).ToList()
        };
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningDataflowResolver.Resolve(state);
        foreach (var progress in state.Construction.Workflows) progress.Status = "validated";
        return;

        PlanningBehaviorNode Node(PlanningNode node) => new()
        {
            Key = node.Key,
            Purpose = node.Purpose,
            CapabilityId = node.CapabilityId,
            OperationIds = node.OperationIds.ToList(),
            InputDependencies = [],
            Kind = node.Type switch { "loop.sequential" => "loop", "switch" => "decision", "workflow.call" => "workflow", "human.input" => "confirmation", "sequence" or "parallel" => node.Type, _ => "operation" },
            WorkflowKey = node.Type == "workflow.call" ? node.Input.Members.Single(m => m.Name == "ref").Value.Source : null,
            Steps = node.Type == "parallel" ? node.Branches.Select(b => Node(b.Steps.Single())).ToList() : node.Steps.Select(Node).ToList(),
            Outcomes = node.Type == "switch" ? node.Cases.Select(c => new PlanningBehaviorOutcome(c.Value!, c.Value!, false, c.Steps.Select(Node).ToList()))
                .Append(new PlanningBehaviorOutcome("default", "Default", true, node.Default.Select(Node).ToList())).ToList() : []
        };
    }
}
