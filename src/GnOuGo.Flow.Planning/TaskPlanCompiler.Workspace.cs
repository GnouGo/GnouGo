using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    internal static bool FixedWorkspaceInput(string stepType, string port) => stepType == "agent.run" && port == "workspace";
    internal static bool LiteralScopeInput(string stepType, string port) => stepType == "agent.run" &&
        port is "objective" or "capabilities" or "budget" or "verification" or "output_schema";

    // Only workspace references may reduce to constants. Normal Value/Read first
    // checks availability; schema const/default annotations never establish this proof.
    private TaskValue ScopeValue(PlanTask consumer, string port, TaskValue value)
    {
        if (port == "workspace" && !Literal(value) && _symbols.Tasks.TryGetValue(consumer.Id, out var site) &&
            Constant(value, site.Scope, new(StringComparer.Ordinal)) is { Kind: "string" } constant)
            return constant;
        if ((FixedWorkspaceInput("agent.run", port) || LiteralScopeInput("agent.run", port)) && !Literal(value))
            Fail("AGENT_SCOPE_DYNAMIC", "Agent scope must be literal before approval. Workspace may reference an available, fixed value; runtime inputs, choices and computed paths cannot change it.");
        return value;
    }

    private TaskValue? Constant(TaskValue value, TaskPlanSymbols.Scope scope, HashSet<string> active)
    {
        if (Literal(value)) return value;
        if (value.Kind == "output" && value.Source is { } id && _symbols.Tasks.TryGetValue(id, out var source) &&
            source.Task.Kind == "value" && source.Scope.Source.Tasks.Contains(source.Task) && active.Add(id))
        {
            try
            {
                var visible = false;
                for (var current = scope; current is not null; current = current.Parent)
                    if (current == source.Scope) visible = true;
                for (var current = source.Scope; current is not null; current = current.Parent)
                    if (current.Owner?.Kind == "foreach") return null;
                var ports = source.Task.Outputs.Where(o => o.Name == value.Port).ToArray();
                return visible && ports.Length == 1 ? Constant(ports[0].Value, source.Scope, active) : null;
            }
            finally { active.Remove(id); }
        }
        if (value.Kind == "field" && value.Items.Count == 1 &&
            Constant(value.Items[0], scope, active) is { Kind: "object" } container)
        {
            var fields = container.Members.Where(m => m.Name == value.Port).ToArray();
            return fields.Length == 1 ? fields[0].Value : null;
        }
        if (value.Kind is "object" or "array")
        {
            var members = value.Members.Select(m => (m.Name, Value: Constant(m.Value, scope, active))).ToArray();
            var items = value.Items.Select(v => Constant(v, scope, active)).ToArray();
            if (members.Any(m => m.Value is null) || items.Any(i => i is null)) return null;
            return new() { Kind = value.Kind, Members = members.Select(m => new TaskOutput(m.Name, m.Value!)).ToList(), Items = items.Select(i => i!).ToList() };
        }
        return null;
    }
}
