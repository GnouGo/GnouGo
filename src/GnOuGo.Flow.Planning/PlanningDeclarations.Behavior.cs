using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningDeclarations
{
    internal static IEnumerable<PlanningDiagnostic> ValidateDefaults(PlanningSnapshot state, PlanningGraph graph)
    {
        RequireCurrent(state);
        foreach (var declaration in state.Declarations.Where(d => d.Direction == "input"))
        {
            var workflow = graph.Workflows.SingleOrDefault(w => (w.Key == graph.Entrypoint ? "main" : w.Key) == declaration.WorkflowScope);
            var port = workflow?.Inputs.SingleOrDefault(p => p.Name == Name(state, declaration));
            if (port is null) continue; // Existing behavior-implementation validation locates missing ports.
            var expected = Default(state, declaration);
            if ((expected is null) != (port.Default is null) || expected is not null && port.Default is not null &&
                (!PlanningGraphValidation.IsLiteral(port.Default) || !System.Text.Json.Nodes.JsonNode.DeepEquals(PlanningGraphValidation.Literal(expected), PlanningGraphValidation.Literal(port.Default))))
                yield return new("DECLARATION_DEFAULT_CHANGED", "/workflows/" + graph.Workflows.IndexOf(workflow!) + "/inputs/" + workflow!.Inputs.IndexOf(port) + "/default",
                    "Preserve the declared omission default. Governing changes require renewed review.");
        }
    }

    internal static List<PlanningDiagnostic> ValidateBehavior(PlanningSnapshot state, PlanningBehaviorPlan plan)
    {
        RequireCurrent(state);
        var findings = new List<PlanningDiagnostic>();
        if (state.DeclarationFingerprint is null)
        {
            if (plan.Workflows.Any(w => w.Inputs.Count > 0 || w.Outputs.Count > 0))
                findings.Add(new("BEHAVIOR_DECLARATION_MISMATCH", "/workflows", "Public ports require declaration proof before review or acceptance.", Rule: "missing_declaration_proof"));
            return findings;
        }
        var boundaries = state.Preparation!.Capabilities.ToDictionary(PlanningBehaviorDecisions.ResultPort, StringComparer.Ordinal);
        foreach (var workflow in plan.Workflows)
        foreach (var direction in new[] { "input", "output" })
        {
            var ports = direction == "input" ? workflow.Inputs : workflow.Outputs;
            var scope = workflow.Key == plan.Entrypoint ? "main" : workflow.Key;
            var expected = state.Declarations.Where(d => d.Direction == direction && d.WorkflowScope == scope).ToArray();
            var root = "/workflows/" + plan.Workflows.IndexOf(workflow) + "/" + direction + "s";
            foreach (var declaration in expected)
                if (ports.Count(p => p.DeclarationId == declaration.Id) != 1)
                    findings.Add(new("BEHAVIOR_DECLARATION_MISMATCH", root, "Each canonical public declaration must occur exactly once.", Rule: declaration.Id));
            foreach (var port in ports)
            {
                var declaration = state.Declarations.SingleOrDefault(d => d.Id == port.DeclarationId && d.Direction == direction);
                var internalPort = scope != "main" && port.DeclarationId is null && boundaries.TryGetValue(port.Name, out var producer) &&
                    (direction == "output" ? producer.OperationIds.Any(workflow.OperationIds.Contains) :
                        state.Preparation.Capabilities.Any(c => c.OperationIds.Any(workflow.OperationIds.Contains) && c.InputOperationIds.Any(producer.OperationIds.Contains)));
                // Reusable callees may receive an existing business input without
                // establishing another public declaration.
                var inherited = direction == "input" && scope != "main" && declaration?.WorkflowScope == "main" &&
                    PlanningBehaviorPlans.Enumerate(workflow.Steps.Concat(workflow.Finally)).Any(n => n.InputDependencies?.Contains(port.Name) == true);
                if (internalPort) continue;
                if (declaration is null || !(declaration.WorkflowScope == scope || inherited) ||
                    port.Name != Name(state, declaration) || port.Required != declaration.Required ||
                    port.Description != Port(state, declaration).Description || ports.Count(p => p.DeclarationId == port.DeclarationId) != 1)
                    findings.Add(new("BEHAVIOR_DECLARATION_MISMATCH", root + "/" + ports.IndexOf(port), "The behavior port differs from its canonical declaration.", Rule: port.DeclarationId ?? "unbacked_port"));
            }
        }
        foreach (var declaration in state.Declarations)
            if (!plan.Workflows.Any(w => (w.Key == plan.Entrypoint ? "main" : w.Key) == declaration.WorkflowScope))
                findings.Add(new("BEHAVIOR_DECLARATION_MISMATCH", "/workflows", "A declared workflow scope is missing.", Rule: declaration.Id));
        return findings;
    }
}
