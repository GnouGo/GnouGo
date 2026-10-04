using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    internal sealed record RepairSource(TaskValue Value, JsonObject Schema);

    // Repair presentation reuses semantic binding and visibility, including captures,
    // groups, conditional exports and effective operation results. No second type system.
    internal static Dictionary<string, List<RepairSource>> RepairSources(TaskPlan plan, PlanningCatalog catalog)
    {
        var compiler = new TaskPlanCompiler { _plan = plan, _symbols = new(plan), _catalog = catalog };
        var result = new Dictionary<string, List<RepairSource>>(StringComparer.Ordinal);
        compiler.Preflight((location, scope) =>
        {
            var values = new List<TaskValue>();
            for (var current = scope; current is not null; current = current.Parent)
            {
                values.AddRange(current.Inputs.Keys.Select(name => new TaskValue { Kind = "input", Source = name }));
                foreach (var (id, ports) in current.Tasks)
                    values.AddRange(ports.Keys.Select(port => new TaskValue { Kind = "output", Source = id, Port = port.Length == 0 ? null : port }));
                if (current.Item is not null) values.Add(new() { Kind = "item" });
                if (current.Index is not null) values.Add(new() { Kind = "index" });
            }
            var sources = new List<RepairSource>();
            foreach (var value in values.DistinctBy(v => (v.Kind, v.Source, v.Port)))
            {
                try { sources.Add(new(value, compiler.Value(value, scope).Schema.DeepClone().AsObject())); }
                catch (InvalidTask) { }
                catch (UnavailableValue) { }
            }
            result[location] = sources;
        });
        return result;
    }
}
