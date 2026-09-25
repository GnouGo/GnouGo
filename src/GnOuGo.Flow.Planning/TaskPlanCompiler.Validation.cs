using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private sealed class UnavailableValue : Exception;

    // The final lookahead is an absolute end check in both JSON Schema/ECMAScript
    // and .NET; '$' alone would also accept a trailing newline.
    internal const string IdentityPattern = @"^(?!__)[A-Za-z0-9_-]+(?![\s\S])";
    internal static bool ValidIdentity(string? id) => !string.IsNullOrEmpty(id) && !id.StartsWith("__", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static IEnumerable<(string Id, string Location)> Declarations(TaskPlan plan)
    {
        var index = 0;
        foreach (var task in TaskPlanRevisions.Tasks(plan)) yield return (task.Id, Location("tasks", task.Id, index++));
        index = 0;
        foreach (var group in plan.Groups) yield return (group.Id, Location("groups", group.Id, index++));
        index = 0;
        foreach (var choice in plan.Choices) yield return (choice.Id, Location("choices", choice.Id, index++));
        static string Location(string kind, string? id, int index) => "/" + kind + "/" +
            (ValidIdentity(id) ? id : index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "/id";
    }

    internal static HashSet<string> InvalidDeclarations(TaskPlan plan) => Declarations(plan).GroupBy(d => d.Id, StringComparer.Ordinal)
        .Where(g => !ValidIdentity(g.Key) || g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<PlanningDiagnostic> IdentityDiagnostics(TaskPlan plan, bool includeReferences = true)
    {
        var invalid = InvalidDeclarations(plan);
        var findings = Declarations(plan).Where(d => invalid.Contains(d.Id)).Select(d => new PlanningDiagnostic("TASK_IDENTITY_INVALID", d.Location,
            "Task, group and choice IDs must be globally unique, use only ASCII letters, digits, '_' or '-', and not start with '__'. Regenerate invalid identities; they are not repair permissions.")).ToList();
        if (includeReferences)
        {
            ScanScope(plan.Root, "/root");
            foreach (var group in plan.Groups)
                if (ValidIdentity(group.Id)) ScanScope(group.Body, "/groups/" + group.Id + "/body");
            foreach (var choice in plan.Choices.Where(c => ValidIdentity(c.Id)))
                for (var i = 0; i < choice.Alternatives.Count; i++) ScanValue(choice.Alternatives[i].Value, "/choices/" + choice.Id + "/alternatives/" + i + "/value");
        }
        return findings.Distinct().OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray();

        void Reference(string? id, string path)
        {
            if (!ValidIdentity(id)) findings.Add(new("TASK_IDENTITY_INVALID", path, "Identity references must use a nonempty path-safe ID outside the reserved '__' namespace."));
        }
        void ScanValue(TaskValue value, string path)
        {
            foreach (var nested in Values(value))
                if (nested.Kind is "output" or "present" or "choice") Reference(nested.Source, path + "/source");
        }
        void ScanScope(TaskScope scope, string path)
        {
            foreach (var output in scope.Outputs) ScanValue(output.Value, path + "/outputs/" + output.Name);
            foreach (var task in scope.Tasks.Concat(scope.Always))
            {
                if (!ValidIdentity(task.Id)) continue; // Never turn an invalid ID into an authoritative path.
                var location = "/tasks/" + task.Id;
                for (var i = 0; i < task.DependsOn.Count; i++) Reference(task.DependsOn[i], location + "/dependsOn/" + i);
                if (task.Kind == "call") Reference(task.Group, location + "/group");
                foreach (var input in task.Inputs) ScanValue(input.Value, location + "/inputs/" + input.Name);
                foreach (var output in task.Outputs) ScanValue(output.Value, location + "/outputs/" + output.Name);
                if (task.Condition is not null) ScanValue(task.Condition, location + "/condition");
                if (task.Items is not null) ScanValue(task.Items, location + "/items");
                if (task.Body is not null) ScanScope(task.Body, location + "/body");
                if (task.Otherwise is not null) ScanScope(task.Otherwise, location + "/otherwise");
                for (var i = 0; i < task.Branches.Count; i++) ScanScope(task.Branches[i], location + "/branches/" + i);
            }
        }
    }

    // Validate business symbols and contracts before emitting any graph node. Bound
    // values reuse the compiler's existing type rules; no executable plan is built here.
    private IReadOnlyList<PlanningDiagnostic> Preflight()
    {
        var findings = IdentityDiagnostics(_plan).ToList();
        var symbols = new TaskPlanSymbols(_plan);
        var groups = new Dictionary<string, Scope>(StringComparer.Ordinal);
        var activeGroups = new HashSet<string>(StringComparer.Ordinal);
        var invalidChoices = _plan.Choices.Where(c => symbols.InvalidIds.Contains(c.Id)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in _catalog.Capabilities) findings.AddRange(TaskOperations.Validate(capability));
        foreach (var choice in _plan.Choices)
        {
            if (invalidChoices.Contains(choice.Id)) continue;
            var before = findings.Count;
            Check("/choices/" + choice.Id, () => ValidateChoice(choice));
            if (symbols.Values.Values.SelectMany(s => Values(s.Value)).Count(v => v.Kind == "choice" && v.Source == choice.Id) != 1)
                findings.Add(new("CHOICE_SLOT_INVALID", "/choices/" + choice.Id, "A choice must target exactly one business value slot."));
            if (findings.Count != before) invalidChoices.Add(choice.Id);
        }
        // Invalid mappings/ambiguous identities cannot provide authoritative symbols.
        // Independent inputs and scopes are still inspected without lowering.
        var root = new Scope(new(), null);
        Inputs(root, _plan.Inputs, "/inputs");
        InspectScope(_plan.Root, root, "/root");
        foreach (var group in _plan.Groups) GroupScope(group.Id);
        return findings.Distinct().OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray();

        void Check(string location, Action action)
        {
            var saved = _location; _location = location;
            try { action(); }
            catch (InvalidTask ex) { findings.Add(ex.Diagnostic); }
            catch (UnavailableValue) { /* A diagnosed prerequisite has no usable contract. */ }
            finally { _location = saved; }
        }
        Bound? Read(TaskValue value, Scope scope, string location)
        {
            if (value.Kind is "output" or "present" or "choice" && symbols.InvalidIds.Contains(value.Source!)) return null;
            Bound? result = null;
            var children = value.Members.Select(m => m.Value).Concat(value.Items).ToArray();
            var valid = true;
            foreach (var child in children) if (Read(child, scope, location) is null) valid = false;
            if (!valid) return null;
            if (value.Kind == "choice" && invalidChoices.Contains(value.Source ?? "")) return null;
            Check(location, () => result = Value(value, scope));
            if (result is null && value.Kind == "output" && value.Source is { } producer && symbols.Values.TryGetValue(location, out var site))
                foreach (var boundary in symbols.ExportRoute(site.Scope, producer))
                    foreach (var affected in boundary.Owner?.Kind == "conditional" ? symbols.Scopes.Where(s => s.Owner == boundary.Owner) : [boundary])
                        findings.Add(new("TASK_EXPORT_REQUIRED", affected.Path + "/outputs", "Declare the business outputs needed by consumers outside this scope; exports are never implicit."));
            return result;
        }
        void Inputs(Scope scope, List<TaskInput> inputs, string path)
        {
            Check(path, () => Unique(inputs.Select(i => i.Name)));
            foreach (var input in inputs)
            {
                scope.Blocked.Add(("input", input.Name, ""));
                Check(path + "/" + input.Name, () =>
                {
                    var port = InputPort(input);
                    scope.Inputs.TryAdd(input.Name, Input(input.Name, port.Schema.Contract!));
                    scope.Blocked.Remove(("input", input.Name, ""));
                });
            }
        }
        Scope? GroupScope(string id)
        {
            if (!ValidIdentity(id) || symbols.InvalidIds.Contains(id)) return null;
            if (activeGroups.Contains(id)) { findings.Add(new("TASK_GROUP_CYCLE", "/groups/" + id, "Reusable groups cannot recurse.")); return null; }
            if (groups.TryGetValue(id, out var cached)) return cached;
            var matches = _plan.Groups.Where(g => g.Id == id).ToArray();
            if (matches.Length != 1) return null;
            var group = matches[0]; var scope = new Scope(new(), null);
            activeGroups.Add(id); groups[id] = scope;
            Inputs(scope, group.Inputs, "/groups/" + id + "/inputs");
            InspectScope(group.Body, scope, "/groups/" + id + "/body");
            activeGroups.Remove(id); return scope;
        }
        Dictionary<string, Bound> Exports(Scope scope) => scope.Workflow.Outputs.ToDictionary(o => o.Name, o => Output("semantic", "workflow.call", [o.Name], o.Schema.Contract!), StringComparer.Ordinal);
        void InspectScope(TaskScope source, Scope scope, string path)
        {
            Check(path + "/outputs", () => Unique(source.Outputs.Select(o => o.Name)));
            InspectTasks(source.Tasks, scope); InspectTasks(source.Always, scope);
            foreach (var output in source.Outputs)
            {
                var value = Read(output.Value, scope, path + "/outputs/" + output.Name);
                if (value is not null && scope.Workflow.Outputs.All(o => o.Name != output.Name))
                    scope.Workflow.Outputs.Add(new() { Name = output.Name, Value = value.Value, Schema = Contract(value.Schema) });
            }
        }
        void InspectTasks(List<PlanTask> tasks, Scope scope)
        {
            var remaining = tasks.ToList(); var done = new HashSet<string>(StringComparer.Ordinal);
            var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(t => t.DependsOn.All(d => done.Contains(d) || !ids.Contains(d)) &&
                    Values(t).Where(v => v.Kind is "output" or "present" && v.Source is not null && ids.Contains(v.Source)).All(v => done.Contains(v.Source!)));
                if (next is null)
                {
                    foreach (var blocked in remaining) scope.Blocked.Add(("output", blocked.Id, "*"));
                    foreach (var blocked in remaining)
                    {
                        findings.Add(new("TASK_DEPENDENCY_CYCLE", "/tasks/" + blocked.Id + "/dependsOn", "Dependencies and business references must be acyclic."));
                        InspectTask(blocked, scope);
                    }
                    break;
                }
                remaining.Remove(next); InspectTask(next, scope); done.Add(next.Id);
            }
        }
        void InspectTask(PlanTask task, Scope scope)
        {
            var path = "/tasks/" + task.Id;
            if (symbols.InvalidIds.Contains(task.Id)) { scope.Blocked.Add(("output", task.Id, "*")); return; }
            Check(path + "/objective", () => { if (string.IsNullOrWhiteSpace(task.Objective)) Fail("TASK_OBJECTIVE_REQUIRED", "Each task requires an objective."); });
            Check(path + "/dependsOn", () => { if (task.DependsOn.Any(d => !scope.Tasks.ContainsKey(d))) Fail("TASK_DEPENDENCY_UNKNOWN", "A dependency must name a preceding task in this scope."); });
            var ports = new Dictionary<string, Bound>(StringComparer.Ordinal);
            var declared = new List<string>();
            Scope? Child(TaskScope? body, string role)
            {
                if (body is null) { findings.Add(new("TASK_SCOPE_REQUIRED", path + "/" + role, "This task requires a semantic scope.")); return null; }
                var child = new Scope(new(), scope);
                if (task.Kind == "foreach")
                {
                    var items = task.Items is null ? null : Read(task.Items, scope, path + "/items");
                    if (items?.Schema["type"]?.ToString() == "array" && items.Schema["items"] is JsonObject itemSchema)
                    { child.Item = new(new() { Kind = "loop_item" }, itemSchema, "data.item"); child.Index = new(Number(0), new() { ["type"] = "integer" }, "data.index"); }
                    else { if (items is not null) findings.Add(new("TASK_ITEMS_INVALID", path + "/items", "Iteration needs an authoritative array contract.")); child.Blocked.Add(("item", "", "")); child.Blocked.Add(("index", "", "")); }
                }
                InspectScope(body, child, path + "/" + role); return child;
            }
            switch (task.Kind)
            {
                case "operation":
                    var matches = _catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
                    if (matches.Length != 1) { findings.Add(new("TASK_OPERATION_UNKNOWN", path + "/operation", "Select one issued, unambiguous operation.")); scope.Blocked.Add(("output", task.Id, "*")); break; }
                    var capability = matches[0]; var operation = TaskOperations.Describe(capability);
                    if (TaskOperations.Validate(capability).Count > 0) { scope.Blocked.Add(("output", task.Id, "*")); break; }
                    Check(path + "/operation", () => { if (_catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType)) Fail("TASK_OPERATION_DENIED", "The operation is outside the approved host policy."); });
                    Check(path + "/inputs", () => Unique(task.Inputs.Select(i => i.Name)));
                    var mapped = Object([]);
                    foreach (var input in task.Inputs)
                    {
                        var location = path + "/inputs/" + input.Name;
                        var port = operation.Inputs.SingleOrDefault(p => p.Name == input.Name);
                        var value = Read(input.Value, scope, location);
                        if (port is null) { findings.Add(new("TASK_INPUT_UNKNOWN", location, "Choose a declared business input port.")); continue; }
                        Check(location, () =>
                        {
                            if (capability.StepType == "agent.run" && port.Path[0] is "objective" or "workspace" or "capabilities" or "budget" or "verification" or "output_schema" && !Literal(input.Value))
                                Fail("AGENT_SCOPE_DYNAMIC", "Agent scope fields must be literal before approval; choices and runtime references cannot change them.");
                            if (value is not null) { Fits(value, port.Schema, "TASK_INPUT_TYPE"); Bind(mapped, port.Path, value.Value); }
                        });
                    }
                    foreach (var port in operation.Inputs.Where(p => p.Required && task.Inputs.All(i => i.Name != p.Name)))
                        findings.Add(new("TASK_INPUT_REQUIRED", path + "/inputs/" + port.Name, "Required business input: " + port.Name));
                    ports[""] = Output(task.Id, capability.StepType, [], capability.OutputSchema);
                    foreach (var port in operation.Outputs) ports[port.Name] = Output(task.Id, capability.StepType, port.Path, port.Schema);
                    break;
                case "value":
                    Check(path + "/outputs", () => Unique(task.Outputs.Select(o => o.Name)));
                    foreach (var output in task.Outputs)
                    { declared.Add(output.Name); var value = Read(output.Value, scope, path + "/outputs/" + output.Name); if (value is not null) ports.TryAdd(output.Name, value); }
                    break;
                case "call":
                    var group = GroupScope(task.Group ?? "");
                    var definition = _plan.Groups.FirstOrDefault(g => g.Id == task.Group);
                    if (group is null || definition is null) { findings.Add(new("TASK_GROUP_UNKNOWN", path + "/group", "Choose a declared, nonrecursive group.")); scope.Blocked.Add(("output", task.Id, "*")); break; }
                    Check(path + "/inputs", () => Unique(task.Inputs.Select(i => i.Name)));
                    foreach (var input in task.Inputs)
                    {
                        var location = path + "/inputs/" + input.Name; var value = Read(input.Value, scope, location);
                        if (definition.Inputs.All(i => i.Name != input.Name)) findings.Add(new("TASK_GROUP_INPUTS", location, "Choose a declared group input."));
                        else if (value is not null && group.Inputs.TryGetValue(input.Name, out var expected)) Check(location, () => Fits(value, expected.Schema, "TASK_GROUP_INPUT_TYPE"));
                    }
                    foreach (var input in definition.Inputs.Where(i => i.Required && task.Inputs.All(a => a.Name != i.Name))) findings.Add(new("TASK_GROUP_INPUTS", path + "/inputs/" + input.Name, "Supply the required group input."));
                    ports = Exports(group); declared.AddRange(definition.Body.Outputs.Select(o => o.Name)); break;
                case "sequence": case "foreach":
                    if (task.Kind == "foreach")
                    {
                        Check(path + "/maxItems", () => { if (task.MaxItems is < 1 or > 10000) Fail("TASK_ITERATION_BOUND", "Iteration requires a finite positive item ceiling."); });
                        Check(path + "/maxConcurrency", () => { if (task.MaxConcurrency is < 1 or > 100) Fail("TASK_ITERATION_BOUND", "Iteration requires bounded concurrency."); });
                        if (task.Items is null) findings.Add(new("TASK_VALUE_REQUIRED", path + "/items", "Iteration requires items."));
                    }
                    var body = Child(task.Body, "body");
                    if (body is not null) ports = Exports(body);
                    declared.AddRange(task.Body?.Outputs.Select(o => o.Name) ?? []);
                    if (task.Kind == "foreach") foreach (var name in ports.Keys.ToArray()) ports[name] = ports[name] with { Schema = new() { ["type"] = "array", ["items"] = ports[name].Schema.DeepClone() } };
                    break;
                case "conditional":
                    if (task.Condition is null) findings.Add(new("TASK_VALUE_REQUIRED", path + "/condition", "A conditional requires a predicate."));
                    else if (Read(task.Condition, scope, path + "/condition") is { } predicate) Check(path + "/condition", () => RequireBoolean(predicate));
                    var yes = Child(task.Body, "body"); var no = Child(task.Otherwise, "otherwise");
                    var yesNames = task.Body?.Outputs.Select(o => o.Name).ToArray() ?? [];
                    var noNames = task.Otherwise?.Outputs.Select(o => o.Name).ToArray() ?? [];
                    foreach (var name in yesNames.Except(noNames)) findings.Add(new("TASK_BRANCH_OUTPUTS", path + "/otherwise/outputs/" + name, "Declare the matching business output explicitly in this alternative."));
                    foreach (var name in noNames.Except(yesNames)) findings.Add(new("TASK_BRANCH_OUTPUTS", path + "/body/outputs/" + name, "Declare the matching business output explicitly in this alternative."));
                    declared.AddRange(yesNames.Union(noNames));
                    if (yes is not null && no is not null)
                        foreach (var (name, value) in Exports(yes))
                            if (Exports(no).TryGetValue(name, out var alternative)) ports[name] = value with { Schema = JsonNode.DeepEquals(value.Schema, alternative.Schema) ? value.Schema : new() { ["anyOf"] = new JsonArray(value.Schema.DeepClone(), alternative.Schema.DeepClone()) } };
                    break;
                case "parallel":
                    Check(path + "/branches", () => { if (task.Branches.Count < 2) Fail("TASK_PARALLEL_INVALID", "Parallel tasks need at least two branches."); });
                    Check(path + "/maxConcurrency", () => { if (task.MaxConcurrency is < 1 or > 100) Fail("TASK_PARALLEL_INVALID", "Parallel tasks need bounded concurrency."); });
                    for (var i = 0; i < task.Branches.Count; i++)
                    {
                        var branch = Child(task.Branches[i], "branches/" + i)!;
                        foreach (var output in task.Branches[i].Outputs)
                        {
                            if (declared.Contains(output.Name)) findings.Add(new("TASK_BRANCH_OUTPUTS", path + "/branches/" + i + "/outputs/" + output.Name, "Parallel branches must export distinct business output names."));
                            declared.Add(output.Name);
                        }
                        foreach (var (name, value) in Exports(branch)) ports.TryAdd(name, value);
                    }
                    break;
                default: findings.Add(new("TASK_KIND_INVALID", path + "/kind", "Unknown semantic task kind.")); scope.Blocked.Add(("output", task.Id, "*")); break;
            }
            foreach (var missing in declared.Where(n => !ports.ContainsKey(n))) scope.Blocked.Add(("output", task.Id, missing));
            if (!ports.ContainsKey(""))
            {
                if (declared.Any(n => !ports.ContainsKey(n))) scope.Blocked.Add(("output", task.Id, ""));
                else ports[""] = Output(task.Id, "set", [], ObjectSchema(ports.Select(p => (p.Key, p.Value.Schema))));
            }
            scope.Tasks.TryAdd(task.Id, ports);
        }
    }

    private void Fits(Bound value, JsonObject schema, string code)
    {
        if (PlanningGraphValidation.IsLiteral(value.Value)
            ? PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(value.Value), schema).Count > 0
            : !PlanningGraphValidation.TypesFit(value.Schema, schema, allowUnresolved: false))
            Fail(code, "The business value does not satisfy its authoritative contract.");
    }
}
