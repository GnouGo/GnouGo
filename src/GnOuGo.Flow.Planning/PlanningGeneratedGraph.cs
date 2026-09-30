using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The generated language is smaller than the authored YAML runtime.</summary>
internal static class PlanningGeneratedGraph
{
    internal static bool IsDirectMcpInput(string name) => name is "request" or "preserve_optional_nulls" or "timeout_ms" or "raise_on_error" or "detect_result_errors";

    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningCatalog catalog)
    {
        if (!string.IsNullOrWhiteSpace(graph.Functions) || graph.Workflows.Any(w => !string.IsNullOrWhiteSpace(w.Functions)))
            yield return new("GENERATED_SCRIPT_DENIED", "/functions", "Use a declared typed operation or bounded agent task.");
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
                if (node.Type == "mcp.call" && (node.StructuredOutput is not null || PlanningGraphValidation.Member(node.Input, "structured_output") is not null))
                    yield return new("GENERATED_INFERENCE_DENIED", location, "Use the exact selected MCP output contract. If it is opaque, validate the complete value explicitly with value.validate; do not add implicit model interpretation to a deterministic stage.");
                if (node.Type == "mcp.call" && (node.Input.Kind != "object" ||
                    node.Input.Members.Any(m => !IsDirectMcpInput(m.Name))))
                    yield return new("GENERATED_MCP_MODE_DENIED", location + "/input", "A generated MCP stage invokes its exact selected contract. Supply request and optional deterministic timeout/error/null-handling flags; target selection, templates and model-assisted modes belong outside this stage.");
                if (node.Type == "agent.run")
                    foreach (var name in new[] { "objective", "workspace", "capabilities", "budget", "verification", "output_schema" })
                        if (PlanningGraphValidation.Member(node.Input, name) is not { } field || !PlanningGraphValidation.IsLiteral(field) ||
                            name == "workspace" && (field.Kind != "string" || string.IsNullOrWhiteSpace(field.Text) || field.Text.Contains("${", StringComparison.Ordinal)))
                            yield return new("AGENT_SCOPE_DYNAMIC", location + "/input/" + name, "Agent objectives, workspace, permissions, budgets and verification contracts must be explicit literals covered by approval. Workspace must be a nonempty literal path.");
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
            if (!valid) yield return new("GENERATED_EXPRESSION_DENIED", location, "Generated expressions allow references, simple boolean conditions and JSON encoding of a direct value reference.");
        }
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items))
            foreach (var diagnostic in Value(child, location)) yield return diagnostic;
    }
    private static bool Simple(Node node) => node switch
    {
        Literal => true,
        ConditionalExpression conditional => Simple(conditional.Test) && Simple(conditional.Consequent) && Simple(conditional.Alternate),
        Identifier { Name: "data" } => true,
        CallExpression { Callee: Identifier { Name: "json" }, Arguments.Count: 1 } call when call.Arguments[0] is MemberExpression member => Simple(member),
        MemberExpression member => Simple(member.Object) && (member.Computed ? member.Property is StringLiteral or NumericLiteral : member.Property is Identifier),
        UnaryExpression { Operator: Acornima.Operator.LogicalNot } unary => Simple(unary.Argument),
        BinaryExpression binary when binary.Operator is Acornima.Operator.Equality or Acornima.Operator.Inequality or
            Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or Acornima.Operator.LessThan or
            Acornima.Operator.LessThanOrEqual or Acornima.Operator.GreaterThan or Acornima.Operator.GreaterThanOrEqual or
            Acornima.Operator.LogicalAnd or Acornima.Operator.LogicalOr => Simple(binary.Left) && Simple(binary.Right),
        _ => false
    };
}
