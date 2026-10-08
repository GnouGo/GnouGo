using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    // Transient preflight facts only. No executable expressions, plan representation or permissions.
    private sealed record EntryGuard(PlanTask Task, bool Alternative, TaskValue Condition, HashSet<string> Available);
    private readonly Dictionary<string, TaskValue?> _guardRepairs = new(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, TaskValue?> ConditionalRepairs(TaskPlan plan, PlanningCatalog catalog)
    {
        var compiler = new TaskPlanCompiler { _plan = plan, _symbols = new(plan), _catalog = catalog };
        compiler.Preflight();
        return compiler._guardRepairs;
    }

    private IReadOnlyList<PlanningDiagnostic> ConditionalRequirements(Dictionary<string, Dictionary<string, Bound>> contracts, IReadOnlySet<string> invalid)
    {
        _guardRepairs.Clear();
        var findings = new List<PlanningDiagnostic>();
        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var aliases = new Dictionary<(string Source, string? Port), TaskValue>();
        var rootInputs = _plan.Inputs.ToDictionary(i => i.Name, i => new TaskValue { Kind = "input", Source = "root:" + i.Name }, StringComparer.Ordinal);
        foreach (var input in _plan.Inputs)
            try { schemas[Identity(rootInputs[input.Name])] = TypeSchema(input.Type); } catch (ArgumentException) { }
        Visit(_plan.Root, [], new(StringComparer.Ordinal), rootInputs, "", null);
        foreach (var path in _guardRepairs.Keys.ToArray())
            if (_guardRepairs[path] is { } proposed && Contradictory(proposed)) _guardRepairs[path] = null;
        return findings;

        List<TaskOutput> Visit(TaskScope body, List<EntryGuard> guards, HashSet<string> available,
            Dictionary<string, TaskValue> inputs, string invocation, string? item)
        {
            foreach (var phase in new[] { body.Tasks, body.Always })
                foreach (var task in Ordered(phase))
                {
                    List<TaskOutput>? exported = null;
                    TaskValue Resolve(TaskValue value) => Normalize(value, inputs, invocation, item);
                    if (task.Kind == "operation" && task.Requires is not null && guards.Count > 0 && !invalid.Contains("/tasks/" + task.Id + "/requires"))
                    {
                        foreach (var required in Conjuncts(task.Requires))
                        {
                            var goal = Resolve(required);
                            var entry = guards.LastOrDefault(g => Known(goal, g.Available));
                            if (entry is null) continue; // Post-entry observations remain fail-fast runtime assertions.
                            var facts = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var guard in guards) facts.UnionWith(Facts(guard.Condition, guard.Alternative));
                            if (Proves(goal, true, facts)) continue;
                            var contradiction = Proves(goal, false, facts);
                            var path = "/tasks/" + entry.Task.Id + "/condition";
                            var producer = Values(required).FirstOrDefault(v => v.Kind is "output" or "present")?.Source;
                            var context = producer is null ? $" Consumer scope: {_symbols.Tasks[task.Id].Scope.Path}; phase: {_symbols.Phase(task.Id)}." :
                                _symbols.ReferenceContext("/tasks/" + task.Id + "/requires", producer);
                            findings.Add(new("TASK_CONDITIONAL_REQUIREMENT", path,
                                $"Operation '{task.Id}' requires {Display(required)} at /tasks/{task.Id}/requires, but the " +
                                $"{(entry.Alternative ? "body" : "otherwise")} path of '{entry.Task.Id}' " +
                                (contradiction ? "establishes its opposite." : "does not establish it.") +
                                " Guard the conditional entry without weakening requires." + context,
                                Rule: "/tasks/" + task.Id + "/requires"));
                            var branch = entry.Alternative ? entry.Task.Body : entry.Task.Otherwise;
                            var other = entry.Alternative ? entry.Task.Otherwise : entry.Task.Body;
                            var isolated = invocation.Length == 0 && !contradiction && branch is not null && other is not null &&
                                TaskPlanRevisions.Tasks(branch).Where(t => t.Kind is "operation" or "transform" or "call").Select(t => t.Id).SequenceEqual([task.Id]) &&
                                !TaskPlanRevisions.Tasks(other).Any(t => t.Kind is "operation" or "transform" or "call") &&
                                !HasFinalizers(branch) && !HasFinalizers(other) &&
                                // Only lexical references available at this entry may enter the issued condition.
                                Values(required).All(v => v.Kind is "output" or "present" ? entry.Available.Contains(invocation + "output:" + v.Source) :
                                    v.Kind is not ("item" or "index") || entry.Available.Contains(invocation + "item:" + item));
                            if (!isolated || _guardRepairs.TryGetValue(path, out var previous) && previous is null) _guardRepairs[path] = null;
                            else
                            {
                                var condition = _guardRepairs.GetValueOrDefault(path) ?? entry.Task.Condition!;
                                _guardRepairs[path] = Predicate(entry.Alternative ? "and" : "or", condition,
                                    entry.Alternative ? required : Predicate("not", required));
                            }
                        }
                    }
                    if (task.Kind == "conditional" && task.Condition is not null && !invalid.Contains("/tasks/" + task.Id + "/condition"))
                    {
                        var condition = Resolve(task.Condition);
                        var yes = task.Body is null ? [] : Child(task.Body, guards.Append(new(task, true, condition, new(available, StringComparer.Ordinal))).ToList(), item);
                        var no = task.Otherwise is null ? [] : Child(task.Otherwise, guards.Append(new(task, false, condition, new(available, StringComparer.Ordinal))).ToList(), item);
                        exported = yes.Where(o => no.Any(n => n.Name == o.Name && Same(n.Value, o.Value))).ToList();
                    }
                    else if (task.Kind == "call" && _plan.Groups.SingleOrDefault(g => g.Id == task.Group) is { } group)
                    {
                        var args = task.Inputs.ToDictionary(a => a.Name, a => Resolve(a.Value), StringComparer.Ordinal);
                        foreach (var input in group.Inputs.Where(i => !args.ContainsKey(i.Name) && i.Default is not null)) args[input.Name] = Resolve(input.Default!);
                        exported = Visit(group.Body, guards, new(available, StringComparer.Ordinal), args, invocation + task.Id + "/", null);
                    }
                    else
                    {
                        if (task.Body is not null) { var values = Child(task.Body, guards, task.Kind == "foreach" ? task.Id : item); if (task.Kind == "sequence") exported = values; }
                        if (task.Otherwise is not null) Child(task.Otherwise, guards, item);
                        foreach (var branch in task.Branches) Child(branch, guards, item);
                    }
                    available.Add(invocation + "output:" + task.Id);
                    if (task.Kind == "value") exported = task.Outputs.Select(o => new TaskOutput(o.Name, Resolve(o.Value))).ToList();
                    if (exported is not null)
                    {
                        foreach (var output in exported)
                        {
                            aliases[(invocation + task.Id, output.Name)] = output.Value;
                            foreach (var reference in Values(output.Value).Where(v => v.Kind == "output")) available.Add(OutputOrigin(reference.Source!));
                        }
                        // A partial common branch interface must not replace the whole result.
                        if (task.Kind != "conditional" || exported.Count == task.Body!.Outputs.Count)
                            aliases[(invocation + task.Id, null)] = new() { Kind = "object", Members = exported };
                    }
                    if (contracts.TryGetValue(task.Id, out var ports))
                        foreach (var (port, bound) in ports)
                            schemas[Identity(new TaskValue { Kind = "output", Source = invocation + task.Id, Port = port.Length == 0 ? null : port })] = bound.Schema;

                    List<TaskOutput> Child(TaskScope child, List<EntryGuard> path, string? currentItem)
                    {
                        var known = new HashSet<string>(available, StringComparer.Ordinal);
                        if (task.Kind == "foreach") known.Add(invocation + "item:" + task.Id);
                        return Visit(child, path, known, inputs, invocation, currentItem);
                    }
                }
            return body.Outputs.Select(o => new TaskOutput(o.Name, Normalize(o.Value, inputs, invocation, item))).ToList();
        }

        TaskValue Normalize(TaskValue value, Dictionary<string, TaskValue> inputs, string invocation, string? item)
        {
            if (value.Kind == "input" && inputs.TryGetValue(value.Source ?? "", out var input)) return input;
            if (value.Kind == "output" && aliases.TryGetValue((invocation + value.Source, string.IsNullOrEmpty(value.Port) ? null : value.Port), out var alias)) return alias;
            var result = new TaskValue { Kind = value.Kind, Text = value.Text, Number = value.Number, Boolean = value.Boolean,
                Source = value.Kind is "output" or "present" ? invocation + value.Source : value.Kind is "item" or "index" ? invocation + "item:" + item : value.Source,
                Port = string.IsNullOrEmpty(value.Port) ? null : value.Port, Predicate = value.Predicate,
                Items = value.Items.Select(v => Normalize(v, inputs, invocation, item)).ToList(),
                Members = value.Members.Select(m => new TaskOutput(m.Name, Normalize(m.Value, inputs, invocation, item))).ToList() };
            if (result.Kind == "field" && result.Items is [var owner])
            {
                if (owner.Kind == "object" && owner.Members.SingleOrDefault(m => m.Name == result.Port) is { } member) return member.Value;
                if (owner.Kind == "output" && owner.Port is null) return new() { Kind = "output", Source = owner.Source, Port = result.Port };
            }
            if (result is { Kind: "predicate", Predicate: "equal" or "not_equal", Items: [var left, var right] })
            {
                if (left.Kind == "boolean") (left, right) = (right, left);
                if (right.Kind == "boolean" && BooleanValue(left))
                    return (result.Predicate == "equal") == (right.Boolean == true) ? left : Predicate("not", left);
            }
            return result;
        }

        bool Known(TaskValue value, HashSet<string> available) => value.Kind switch
        {
            "output" or "present" => available.Contains(OutputOrigin(value.Source!)),
            "item" or "index" => available.Contains(value.Source!),
            "input" => rootInputs.Values.Any(i => i.Source == value.Source),
            _ => value.Items.All(v => Known(v, available)) && value.Members.All(m => Known(m.Value, available))
        };
        static string OutputOrigin(string source) { var split = source.LastIndexOf('/'); return source[..(split + 1)] + "output:" + source[(split + 1)..]; }
        JsonObject? Schema(TaskValue value)
        {
            if (schemas.TryGetValue(Identity(value), out var schema)) return schema;
            if (value.Kind == "field" && value.Items is [var owner] && Schema(owner) is { } parent &&
                parent["required"] is JsonArray required && required.Any(n => n?.ToString() == value.Port))
                return parent["properties"]?[value.Port!] as JsonObject;
            return null;
        }
        bool BooleanValue(TaskValue value) => value.Kind is "predicate" or "present" or "boolean" || Schema(value)?["type"]?.ToString() == "boolean";
        bool Proves(TaskValue value, bool truth, HashSet<string> facts)
        {
            if (value.Kind == "boolean") return value.Boolean == truth;
            // Object identity is not preserved by value assembly/capture. Do not
            // turn structural aliasing into a proof about JavaScript object equality.
            if (value is { Kind: "predicate", Predicate: "equal" or "not_equal", Items: [var a, var b] } && a.Kind != "null" && b.Kind != "null" &&
                new[] { a, b }.Any(v => v.Kind is "object" or "array" || Schema(v)?["type"]?.ToString() is "object" or "array")) return false;
            if (facts.Contains(Fact(value, truth))) return true;
            if (value is { Kind: "predicate", Predicate: "not", Items: [var operand] }) return Proves(operand, !truth, facts);
            if (value is { Kind: "predicate", Predicate: "and" or "or" })
                return (value.Predicate == "and") == truth ? value.Items.All(v => Proves(v, truth, facts)) : value.Items.Any(v => Proves(v, truth, facts));
            if (value is { Kind: "predicate", Predicate: "equal" or "not_equal", Items: [var left, var right] })
            {
                if (right.Kind == "boolean" && BooleanValue(left)) return Proves(left, (value.Predicate == "equal") == truth ? right.Boolean == true : right.Boolean != true, facts);
                if (left.Kind == "boolean" && BooleanValue(right)) return Proves(right, (value.Predicate == "equal") == truth ? left.Boolean == true : left.Boolean != true, facts);
                if (GuardLiteral(left) && GuardLiteral(right)) return ((value.Predicate == "equal") == truth) == Same(left, right);
                if (GuardLiteral(left)) (left, right) = (right, left);
                if (GuardLiteral(right))
                    foreach (var known in facts.Where(f => f.StartsWith("+=", StringComparison.Ordinal)))
                    {
                        var pair = known[2..].Split('\n'); var identity = Identity(left);
                        var literal = pair[0] == identity ? pair[1] : pair[1] == identity ? pair[0] : null;
                        if (literal is not null && JsonSerializer.Deserialize(literal, PlanningJsonContext.Default.TaskValue) is { } observed && GuardLiteral(observed))
                            return ((value.Predicate == "equal") == truth) == Same(observed, right);
                    }
                if (GuardLiteral(right) && Schema(left) is { } schema)
                {
                    var equal = (value.Predicate == "equal") == truth;
                    if (right.Kind == "null" && schema["type"] is JsonValue type && type.ToString() != "null") return !equal;
                    JsonNode? literal = right.Kind switch { "null" => null, "string" => JsonValue.Create(right.Text), "number" => JsonValue.Create(right.Number), "boolean" => JsonValue.Create(right.Boolean), _ => null };
                    if (schema.ContainsKey("const")) return equal == JsonNode.DeepEquals(schema["const"], literal);
                    if (schema["enum"] is JsonArray domain) return equal ? domain.Count == 1 && JsonNode.DeepEquals(domain[0], literal) : !domain.Any(v => JsonNode.DeepEquals(v, literal));
                }
            }
            if (Schema(value) is { } contract && contract["type"]?.ToString() == "boolean")
                return contract["const"]?.ToJsonString() == (truth ? "true" : "false") ||
                    contract["enum"] is JsonArray { Count: 1 } domain && domain[0]?.ToJsonString() == (truth ? "true" : "false");
            return false;
        }
    }

    private static TaskValue Predicate(string operation, params TaskValue[] items) => new() { Kind = "predicate", Predicate = operation, Items = items.ToList() };
    private static bool GuardLiteral(TaskValue value) => value.Kind is "null" or "string" or "number" or "boolean";
    private static IEnumerable<TaskValue> Conjuncts(TaskValue value) => value is { Kind: "predicate", Predicate: "and" }
        ? value.Items.SelectMany(Conjuncts) : [value];
    private static bool HasFinalizers(TaskScope scope) => scope.Always.Count > 0 || TaskPlanRevisions.Tasks(scope).Any(t =>
        t.Body?.Always.Count > 0 || t.Otherwise?.Always.Count > 0 || t.Branches.Any(b => b.Always.Count > 0));
    private static string Identity(TaskValue value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.TaskValue);
    private static bool Same(TaskValue left, TaskValue right) => Identity(left) == Identity(right);
    private static string Fact(TaskValue value, bool truth)
    {
        if (value is { Kind: "predicate", Predicate: "not", Items: [var operand] }) return Fact(operand, !truth);
        if (value is { Kind: "predicate", Predicate: "equal" or "not_equal", Items: [var left, var right] })
        {
            var keys = new[] { Identity(left), Identity(right) }.Order(StringComparer.Ordinal);
            return ((value.Predicate == "equal") == truth ? "+=" : "-=") + string.Join("\n", keys);
        }
        return (truth ? "+" : "-") + Identity(value);
    }
    private static HashSet<string> Facts(TaskValue value, bool truth)
    {
        var facts = new HashSet<string>(StringComparer.Ordinal) { Fact(value, truth) };
        if (value is { Kind: "predicate", Predicate: "equal" or "not_equal", Items: [var left, var right] } &&
            (value.Predicate == "equal") == truth)
        {
            if (GuardLiteral(left)) (left, right) = (right, left);
            if (GuardLiteral(right) && right.Kind != "null") facts.Add(Fact(Predicate("not_equal", left, new()), true));
        }
        if (value is { Kind: "predicate", Predicate: "not", Items: [var operand] }) facts.UnionWith(Facts(operand, !truth));
        else if (value is { Kind: "predicate", Predicate: "and" or "or", Items.Count: > 0 })
        {
            var common = Facts(value.Items[0], truth);
            foreach (var next in value.Items.Skip(1))
                if ((value.Predicate == "and") == truth) common.UnionWith(Facts(next, truth)); else common.IntersectWith(Facts(next, truth));
            facts.UnionWith(common);
        }
        return facts;
    }
    private static bool Contradictory(TaskValue condition)
    {
        var facts = Facts(condition, true);
        if (facts.Contains(Fact(new() { Kind = "boolean", Boolean = false }, true)) ||
            facts.Any(f => f.StartsWith('+') && facts.Contains("-" + f[1..]))) return true;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var known in facts.Where(f => f.StartsWith("+=", StringComparison.Ordinal)))
        {
            var pair = known[2..].Split('\n');
            var left = JsonSerializer.Deserialize(pair[0], PlanningJsonContext.Default.TaskValue)!;
            var right = JsonSerializer.Deserialize(pair[1], PlanningJsonContext.Default.TaskValue)!;
            if (GuardLiteral(left)) { (left, right) = (right, left); System.Array.Reverse(pair); }
            if (!GuardLiteral(right)) continue;
            if (values.TryGetValue(pair[0], out var previous) && previous != pair[1]) return true;
            values[pair[0]] = pair[1];
        }
        return false;
    }
    private static string Display(TaskValue value) => value.Kind switch
    {
        "output" => value.Source + (value.Port is null ? "" : "." + value.Port), "input" => "input." + value.Source,
        "field" => Display(value.Items[0]) + "." + value.Port, "null" => "null", "string" => JsonValue.Create(value.Text)?.ToJsonString() ?? "null",
        "boolean" => value.Boolean == true ? "true" : "false", "predicate" => value.Predicate + "(" + string.Join(", ", value.Items.Select(Display)) + ")",
        _ => value.Kind
    };
}
