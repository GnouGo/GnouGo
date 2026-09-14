using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

internal static class PlanningFixtures
{
    // Explicit synthetic admission for downstream fixtures. Test-authored operation
    // hints are the answers; production always uses bounded decision pages.
    internal static void AdmitHints(PlanningSnapshot state)
    {
        var hints = state.Obligations.Where(o => o.OperationAdmission is null && PlanningSourceGroundingRules.OperationKinds.Contains(o.Kind)).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var admitted = state.Obligations.Where(o => o.OperationAdmission is not null).ToList();
        foreach (var hint in hints)
        {
            var clause = PlanningChoiceEvidence.Parent(state, hint.EvidenceReferences[0]);
            var assignment = new PlanningOperationAssignment("operation_" + clause.Id, clause.Id, hint.EvidenceReferences[0], hint.Kind,
                hint.Required, null, hint.Grounding?.BaselineReference);
            var operation = PlanningOperations.Create(state, assignment);
            if (!admitted.Any(o => o.Id == operation.Id)) admitted.Add(operation);
            map.Add(hint.Id, operation.Id); state.Obligations.Remove(hint);
        }
        // Remap fixture relationships, capability boundaries and assertions' retained
        // objects in place. Evidence and proof objects never participate in remapping.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Remap(object? value)
        {
            if (value is null or string or PlanningReference or PlanningSourceGrounding or PlanningOperationAdmission || !seen.Add(value)) return;
            if (value is IList<string> names) { for (var i = 0; i < names.Count; i++) if (map.TryGetValue(names[i], out var id)) names[i] = id; return; }
            if (value is System.Collections.IEnumerable list) { foreach (var item in list) Remap(item); return; }
            if (value.GetType().Namespace != typeof(PlanningSnapshot).Namespace) return;
            foreach (var property in value.GetType().GetProperties())
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                var current = property.GetValue(value);
                if (current is string text && property.CanWrite && map.TryGetValue(text, out var replacement)) property.SetValue(value, replacement);
                else Remap(current);
            }
        }
        if (map.Count != 0) Remap(state);
        PlanningOperations.Commit(state, admitted);
    }
    internal static PlanningSnapshot PreparedRequest(PlanningRequest request, PlanningPreparationCheckpoint? checkpoint = null)
    {
        var state = new PlanningSnapshot { Request = request, PreparationCheckpoint = checkpoint };
        var scope = PlanningOperations.Scopes(state)[0];
        var decision = PlanningOperations.Decision(state, scope, []); var staged = new List<PlanningObligation>();
        PlanningOperations.Apply(state, scope, decision, OperationAdmissionTests.Actions(OperationAdmissionTests.Action(scope, "external_read")), staged);
        PlanningOperations.Commit(state, staged); return state;
    }
    internal static string OperationId(PlanningSnapshot state, string evidenceId) => state.Obligations.FirstOrDefault(o =>
        o.OperationAdmission is { } proof && proof.AnchorReference.EndsWith("_" + evidenceId, StringComparison.Ordinal))?.Id ?? evidenceId;

    internal static System.Text.Json.Nodes.JsonObject Workflow(PlanningWorkflow workflow) => PlanningModelValues.Compact(System.Text.Json.JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow))!.AsObject();
    // Fixture setup explicitly models prior human acceptance; production never infers approval from a graph.
    internal static void Accept(PlanningSnapshot state)
    {
        AdmitHints(state);
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
