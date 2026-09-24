using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The generated language is smaller than the authored YAML runtime.</summary>
internal static class PlanningGeneratedGraph
{
    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningRequirements requirements, PlanningCatalog catalog)
    {
        if (!string.IsNullOrWhiteSpace(graph.Functions) || graph.Workflows.Any(w => !string.IsNullOrWhiteSpace(w.Functions)))
            yield return new("GENERATED_SCRIPT_DENIED", "/functions", "Use a declared typed operation or bounded agent task.");
        var identities = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)).Select(n => (Qualified: w.Key + "/" + n.Key, n.Key))).ToArray();
        foreach (var outcome in requirements.Outcomes)
            if (outcome.StageIds.Count == 0 && !graph.Workflows.SelectMany(w => w.Outputs).Any(o => o.Name == outcome.Id) ||
                outcome.StageIds.Any(id => identities.Count(n => n.Qualified == id || n.Key == id) != 1))
                yield return new("REQUIREMENT_UNBOUND", "/requirements/" + outcome.Id, "Update this outcome stageIds to actual implementing stages. Available qualified stage IDs: " + string.Join(", ", identities.Select(n => n.Qualified)) + ". Only outcome IDs and descriptions are immutable; stageIds must match the graph.");
        foreach (var workflow in graph.Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.Key) || workflow.Key.StartsWith("__planning_", StringComparison.Ordinal))
                yield return new("RESERVED_IDENTITY", "/workflows", "Workflow identities cannot use the host namespace.");
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                var location = "/workflows/" + graph.Workflows.IndexOf(workflow) + "/stages/" + node.Key;
                if (string.IsNullOrWhiteSpace(node.Key) || node.Key.StartsWith("__planning_", StringComparison.Ordinal) || node.InternalRole is not null)
                    yield return new("RESERVED_IDENTITY", location, "Stage identities and roles cannot impersonate host controls.");
                if (node.Type is "mcp.call" or "agent.run" && !catalog.Capabilities.Any(c => c.Id == node.CapabilityId && c.StepType == node.Type))
                    yield return new("CAPABILITY_UNKNOWN", location, "Resolve an authorized contract before using this stage.");
                if (node.OutputSchema is not null && node.Type is not ("set" or "value.validate" or "value.project" or "array.project"))
                    yield return new("GENERATED_SCHEMA_OVERRIDE_DENIED", location + "/outputSchema", "Set outputSchema to null. This stage derives its output contract from its operation or child stages; a model declaration cannot override it.");
                if (node.Type == "agent.run")
                    foreach (var name in new[] { "objective", "capabilities", "budget", "verification", "output_schema" })
                        if (PlanningGraphValidation.Member(node.Input, name) is not { } field || !PlanningGraphValidation.IsLiteral(field))
                            yield return new("AGENT_SCOPE_DYNAMIC", location + "/input/" + name, "Agent objectives, permissions, budgets and verification contracts must be explicit literals covered by approval.");
                foreach (var value in PlanningGraphTopology.Values(node))
                    foreach (var diagnostic in Value(value, location)) yield return diagnostic;
            }
            foreach (var port in workflow.Inputs.Where(p => p.Default is not null))
                foreach (var diagnostic in Value(port.Default!, "/workflows/" + graph.Workflows.IndexOf(workflow) + "/inputs")) yield return diagnostic;
            foreach (var output in workflow.Outputs)
                foreach (var diagnostic in Value(output.Value, "/workflows/" + graph.Workflows.IndexOf(workflow) + "/outputs")) yield return diagnostic;
        }
    }
    private static IEnumerable<PlanningDiagnostic> Value(PlanningValue value, string location)
    {
        if (value.Kind is "compute" or "template")
            yield return new("GENERATED_COMPUTATION_DENIED", location, "Move computation to a typed operation or agent stage.");
        if (value.Kind == "expression")
        {
            var valid = false;
            try { valid = Simple(new Acornima.Parser().ParseExpression(value.Text ?? "")); }
            catch (Acornima.ParseErrorException) { }
            if (!valid) yield return new("GENERATED_EXPRESSION_DENIED", location, "Generated expressions allow only references and simple boolean conditions.");
        }
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items))
            foreach (var diagnostic in Value(child, location)) yield return diagnostic;
    }
    private static bool Simple(Node node) => node switch
    {
        Literal => true,
        ConditionalExpression conditional => Simple(conditional.Test) && Simple(conditional.Consequent) && Simple(conditional.Alternate),
        Identifier { Name: "data" } => true,
        MemberExpression member => Simple(member.Object) && (member.Computed ? member.Property is StringLiteral or NumericLiteral : member.Property is Identifier),
        UnaryExpression { Operator: Acornima.Operator.LogicalNot } unary => Simple(unary.Argument),
        BinaryExpression binary when binary.Operator is Acornima.Operator.Equality or Acornima.Operator.Inequality or
            Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or Acornima.Operator.LessThan or
            Acornima.Operator.LessThanOrEqual or Acornima.Operator.GreaterThan or Acornima.Operator.GreaterThanOrEqual or
            Acornima.Operator.LogicalAnd or Acornima.Operator.LogicalOr => Simple(binary.Left) && Simple(binary.Right),
        _ => false
    };
}
