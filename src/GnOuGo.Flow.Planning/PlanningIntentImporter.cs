using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Projects supported executable workflows into business revision context. Unsupported constructs fail explicitly.</summary>
public static partial class PlanningIntentImporter
{
    public static WorkflowIntentPlan Import(PlanningGraph graph)
    {
        if (!string.IsNullOrWhiteSpace(graph.Functions) || graph.Workflows.Any(w => !string.IsNullOrWhiteSpace(w.Functions)))
            throw Unsupported("workflow helper functions");
        var main = graph.Workflows.Single(w => w.Key == graph.Entrypoint);
        var plan = new WorkflowIntentPlan { Summary = graph.Summary, Inputs = Inputs(main), Operations = Operations(main), Outputs = Outputs(main) };
        plan.Subflows = graph.Workflows.Where(w => w != main).Select(w => new IntentSubflow(w.Key, Inputs(w), Operations(w), Outputs(w))).ToList();
        return plan;
    }
    private static List<IntentInput> Inputs(PlanningWorkflow flow) => flow.Inputs.Select(p => new IntentInput(p.Name, p.Schema.CapabilityId is null ? Type(p.Schema) : null, !p.Required, p.Default is null ? null : Value(p.Default))).ToList();
    private static List<IntentOutput> Outputs(PlanningWorkflow flow) => flow.Outputs.Select(p => new IntentOutput(p.Name, Value(p.Value))).ToList();
    private static List<IntentOperation> Operations(PlanningWorkflow flow)
    {
        var operations = flow.Steps.Select(Operation).ToList();
        if (flow.Finally.Count > 0) operations.Add(new CleanupIntentOperation { Id = "cleanup", Operations = flow.Finally.Select(Operation).ToList() });
        return operations;
    }
    private static IntentOperation Operation(PlanningNode node)
    {
        if (node.Retry is not null || node.OnError.Count > 0 || node.Steps.Count + node.Branches.Count + node.Cases.Count + node.Default.Count > 0 || node.Output is not null)
            throw Unsupported("technical execution configuration on " + node.Key);
        IntentOperation operation = node.Type switch
        {
            "set" => new CalculateIntentOperation { Value = Value(node.Input) },
            "mcp.call" when node.CapabilityId is not null => new InvokeIntentOperation { Capability = node.CapabilityId, Arguments = Members(PlanningGraphValidation.Member(node.Input, "request")!) },
            "workflow.call" => new CallIntentOperation { Flow = PlanningGraphValidation.Member(node.Input, "ref")!.Source!, Arguments = Members(PlanningGraphValidation.Member(node.Input, "args")!) },
            _ => throw Unsupported("step " + node.Key + " of type " + node.Type)
        };
        operation.Id = node.Key; operation.Purpose = node.Purpose; operation.After = [.. node.Dependencies]; operation.When = node.If is null ? null : Value(node.If);
        return operation;
    }
    private static List<IntentMember> Members(PlanningValue value) => value.Members.Select(m => new IntentMember(m.Name, Value(m.Value))).ToList();
    private static IntentValue Value(PlanningValue value)
    {
        if (value.Kind == "expression")
        {
            var match = SimpleReference().Match(value.Text ?? "");
            if (!match.Success) throw Unsupported("runtime expression " + value.Text);
            return new() { Kind = match.Groups[1].Value == "inputs" ? "input" : "result", Source = match.Groups[2].Value,
                Path = match.Groups[3].Value.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList() };
        }
        if (value.Kind is not ("string" or "number" or "boolean" or "null" or "object" or "array" or "input" or "output" or "compute")) throw Unsupported("value " + value.Kind);
        if (value.ResultChannel is not (null or "default")) throw Unsupported("explicit result channels");
        return new() { Kind = value.Kind == "output" ? "result" : value.Kind, Text = value.Text, Number = value.Number, Boolean = value.Boolean,
            Source = value.Source, Path = [.. value.Path], Members = Members(value), Items = value.Items.Select(Value).ToList() };
    }
    private static IntentType Type(PlanningSchema schema)
    {
        if (schema.CapabilityId is not null || schema.AdditionalProperties is not null) throw Unsupported("non-structural schema");
        return new() { Type = schema.Type, Nullable = schema.Nullable, Enum = [.. schema.Enum], Items = schema.Items is null ? null : Type(schema.Items),
            Fields = schema.Properties.Select(p => new IntentField(p.Name, Type(p.Schema), !p.Required)).ToList() };
    }
    private static InvalidOperationException Unsupported(string construct) => new("The workflow cannot be represented as business intent: " + construct + ".");
    [GeneratedRegex(@"^\$\{\s*data\.(inputs|steps)\.([A-Za-z_][A-Za-z0-9_]*)(\.[A-Za-z0-9_.]+)?\s*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleReference();
}
