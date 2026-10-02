using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private sealed class UnavailableValue : Exception;

    // Use portable wire-schema syntax: neither lookaround nor .NET/RE2's \z.
    // '$' differs across regex engines for a final newline, so ValidIdentity's
    // exact character checks remain authoritative at every compiler entry point.
    internal const string IdentityPattern = @"^([A-Za-z0-9-][A-Za-z0-9_-]*|_[A-Za-z0-9-][A-Za-z0-9_-]*|_)$";
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
    private IReadOnlyList<PlanningDiagnostic> Preflight(Action<string, Scope>? inspect = null)
    {
        var findings = IdentityDiagnostics(_plan).ToList();
        var symbols = _symbols;
        var artifacts = new TaskArtifactBindings(_plan, _catalog, symbols);
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
            inspect?.Invoke(location, scope);
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
                    scope.Inputs.TryAdd(input.Name, Input(input.Name, port.Schema.Contract!) with { TypeLocation = path + "/" + input.Name + "/type" });
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
            inspect?.Invoke(path + "/outputs", scope);
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
            Check(path + "/dependsOn", () =>
            {
                var unknown = task.DependsOn.Where(d => !scope.Tasks.ContainsKey(d)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (unknown.Length > 0) Fail("TASK_DEPENDENCY_UNKNOWN", "Dependencies " + string.Join(", ", unknown) +
                    " must name preceding tasks in scope " + symbols.Tasks[task.Id].Scope.Path + ". Ancestor values may be captured without cross-scope dependsOn entries.");
            });
            var ports = new Dictionary<string, Bound>(StringComparer.Ordinal);
            var declared = new List<string>();
            Scope? Child(TaskScope? body, string role)
            {
                if (body is null) { findings.Add(new("TASK_SCOPE_REQUIRED", path + "/" + role, "This task requires a semantic scope.")); return null; }
                var child = new Scope(new(), scope);
                if (task.Kind == "foreach")
                {
                    var items = task.Items is null ? null : Read(task.Items, scope, path + "/items");
                    if (items is not null && PlanningContractShapes.IterationItems(items.Schema) is { } itemSchema)
                    { child.Item = new(new() { Kind = "loop_item" }, itemSchema, "data.item", TypeLocation: items.TypeLocation is { } itemType ? itemType + "/items" : null); child.Index = new(Number(0), new() { ["type"] = "integer" }, "data.index"); }
                    else
                    {
                        if (items is not null)
                        {
                            findings.Add(new("TASK_ITEMS_INVALID", path + "/items", "Iteration needs an authoritative array contract."));
                            findings.AddRange(ConstraintFindings(items, new() { ["type"] = "array" }, path + "/items"));
                        }
                        child.Blocked.Add(("item", "", "")); child.Blocked.Add(("index", "", ""));
                    }
                }
                InspectScope(body, child, path + "/" + role); return child;
            }
            switch (task.Kind)
            {
                case "transform":
                    Check(path + "/kind", () =>
                    {
                        if (!_catalog.AllowedStepTypes.Contains("llm.call") || !_catalog.AllowedStepTypes.Contains("template.render"))
                            Fail("TASK_TRANSFORM_DENIED", "Transform tasks require inference and prompt assembly permitted by host policy.");
                    });
                    Check(path + "/inputs", () =>
                    {
                        Unique(task.Inputs.Select(i => i.Name));
                        if (task.Inputs.Count == 0) Fail("TASK_TRANSFORM_INPUT", "A transform interprets supplied business data; bind at least one input.");
                    });
                    foreach (var input in task.Inputs) Read(input.Value, scope, path + "/inputs/" + input.Name);
                    var typeFindings = TransformTypeFindings(task.ResultType, path + "/resultType").ToArray();
                    findings.AddRange(typeFindings);
                    if (typeFindings.Length == 0) ports = StructuredResult(task.Id, TypeSchema(task.ResultType!), path + "/resultType");
                    else scope.Blocked.Add(("output", task.Id, "*"));
                    break;
                case "operation":
                    var matches = _catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
                    if (matches.Length != 1) { findings.Add(new("TASK_OPERATION_UNKNOWN", path + "/operation", "Select one issued, unambiguous operation.")); scope.Blocked.Add(("output", task.Id, "*")); break; }
                    var capability = matches[0];
                    if (TaskOperations.Validate(capability).Count > 0) { scope.Blocked.Add(("output", task.Id, "*")); break; }
                    var operation = PlanningCapabilityArguments.Editable(capability);
                    Check(path + "/operation", () => { if (_catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType)) Fail("TASK_OPERATION_DENIED", "The operation is outside the approved host policy."); });
                    Check(path + "/inputs", () => Unique(task.Inputs.Select(i => i.Name)));
                    var inputFindings = findings.Count;
                    var mapped = new List<(OperationPort Port, JsonObject Schema)>();
                    var mappedValues = Object([]);
                    foreach (var input in task.Inputs)
                    {
                        var location = path + "/inputs/" + input.Name;
                        if (PlanningCapabilityArguments.Assignment(capability, input.Name, input.Value))
                        { findings.Add(new("TASK_INPUT_HOST_OWNED", location, "This binding is supplied by the catalog. Omit it; build partial objects only from editable business fields.")); continue; }
                        var port = operation.Inputs.SingleOrDefault(p => p.Name == input.Name);
                        var value = Read(input.Value, scope, location);
                        if (port is null) { findings.Add(new("TASK_INPUT_UNKNOWN", location, "Choose a declared business input port.")); continue; }
                        Check(location, () =>
                        {
                            if (capability.StepType == "agent.run") ScopeValue(task, port.Path[0], input.Value);
                            if (value is not null)
                            {
                                findings.AddRange(ConstraintFindings(value, port.Schema, location));
                                foreach (var artifact in capability.ArtifactContract?.Consumes ?? [])
                                {
                                    var consumedPath = TaskArtifactBindings.Decode(artifact.Pointer);
                                    var relativePath = consumedPath.Skip(port.Path.Count).ToArray();
                                    if (!artifact.Required && TaskArtifactBindings.ExplicitlyAbsent(input.Value, relativePath)) continue;
                                    if (consumedPath.Take(port.Path.Count).SequenceEqual(port.Path, StringComparer.Ordinal) &&
                                        !artifacts.Proves(input.Value, symbols.Tasks[task.Id].Scope, relativePath, artifact.Kind))
                                        findings.Add(artifact.Required && artifacts.MissingProducer(artifact.Kind)
                                            ? new("TASK_ARTIFACT_PREREQUISITE_MISSING", location, "This plan contains no declared producer of artifact kind '" + artifact.Kind + "'. A structural repair requires an exact policy-allowed producer contract and a complete dependency proof; otherwise an explicit semantic revision is required. Types, literals and transforms cannot manufacture provenance.")
                                            : new("TASK_ARTIFACT_BINDING", location, "Bind a declared producer business port of artifact kind '" + artifact.Kind + "'. A matching type or literal does not establish provenance."));
                                }
                                Fits(value, port.Schema, "TASK_INPUT_TYPE", optional: !port.Required);
                                mapped.Add((port, PlanningGraphValidation.IsLiteral(value.Value)
                                    ? new() { ["const"] = PlanningGraphValidation.Literal(value.Value) } : value.Schema));
                                Bind(mappedValues, port.Path, value.Value);
                            }
                        });
                    }
                    foreach (var port in operation.Inputs.Where(p => p.Required && task.Inputs.All(i => i.Name != p.Name)))
                        findings.Add(new("TASK_INPUT_REQUIRED", path + "/inputs/" + port.Name, "Required business input: " + port.Name));
                    if (findings.Count == inputFindings && mapped.Count == task.Inputs.Count)
                        Check(path + "/inputs", () =>
                        {
                            JsonObject request;
                            try { request = PlanningCapabilityArguments.EffectiveSchema(capability, mapped); }
                            catch (InvalidOperationException) { Fail("TASK_INPUT_BINDING", "Input mappings must assemble disjoint declared fields."); return; }
                            if (!PlanningContractCompatibility.Fits(request, capability.InputSchema))
                            {
                                // A literal discriminator can prove the applicable branch without
                                // granting edits to that selector or unrelated business bindings.
                                var before = findings.Count;
                                if (SelectedInputBranch(request, capability.InputSchema) is { } selected)
                                    foreach (var input in task.Inputs)
                                    {
                                        var port = operation.Inputs.Single(p => p.Name == input.Name);
                                        JsonObject? expected = selected;
                                        foreach (var part in port.Path) expected = expected?["properties"]?[part] as JsonObject;
                                        if (expected is null) continue;
                                        var location = path + "/inputs/" + input.Name;
                                        if (Read(input.Value, scope, location) is { } produced)
                                        {
                                            Check(location, () => Fits(produced, expected, "TASK_INPUT_TYPE"));
                                            Constraints(input.Value, expected, location);
                                        }
                                    }
                                if (findings.Count == before)
                                    Fail("TASK_INPUT_TYPE", "The complete effective request does not satisfy its authoritative contract, including conditional parameter requirements. Omission and null are distinct.");
                            }
                            void Constraints(TaskValue value, JsonObject expected, string location)
                            {
                                if (Read(value, scope, location) is { } produced) findings.AddRange(ConstraintFindings(produced, expected, location));
                                if (value.Kind == "object")
                                    foreach (var member in value.Members)
                                        if (expected["properties"]?[member.Name] is JsonObject field) Constraints(member.Value, field, location);
                            }
                        });
                    var declarationPort = operation.Inputs.SingleOrDefault(p => p.Path.SequenceEqual(["output_schema"]));
                    Check(path + (capability.StepType == "agent.run" && declarationPort is not null ? "/inputs/" + declarationPort.Name : "/operation"), () =>
                        ports = OperationResults(task.Id, capability, PlanningCapabilityArguments.Apply(mappedValues, capability)));
                    if (ports.Count == 0) scope.Blocked.Add(("output", task.Id, "*"));
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
                    if (task.Kind == "foreach") foreach (var name in ports.Keys.ToArray()) ports[name] = ports[name] with { Schema = new() { ["type"] = "array", ["items"] = ports[name].Schema.DeepClone() }, TypeLocation = null };
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
                            if (Exports(no).TryGetValue(name, out var alternative)) ports[name] = value with { Schema = JsonNode.DeepEquals(value.Schema, alternative.Schema) ? value.Schema : new() { ["anyOf"] = new JsonArray(value.Schema.DeepClone(), alternative.Schema.DeepClone()) }, TypeLocation = null };
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

    private static JsonObject? SelectedInputBranch(JsonObject request, JsonObject contract)
    {
        if (contract["oneOf"] is not JsonArray alternatives || request["properties"] is not JsonObject supplied) return null;
        var possible = new List<(JsonObject Schema, bool Proven)>();
        foreach (var branch in alternatives)
        {
            if (branch is not JsonObject schema || schema["properties"] is not JsonObject properties) return null;
            var selectors = properties.Where(p => p.Value is JsonObject expected && (expected.ContainsKey("const") || expected["enum"] is JsonArray) &&
                supplied[p.Key] is JsonObject actual && actual.ContainsKey("const")).ToArray();
            if (selectors.Any(p => !PlanningContractCompatibility.Fits(supplied[p.Key]!.AsObject(), p.Value!.AsObject()))) continue;
            possible.Add((schema, selectors.Length > 0));
        }
        return possible.Count == 1 && possible[0].Proven ? possible[0].Schema : null;
    }

    private void Fits(Bound value, JsonObject schema, string code, bool optional = false)
    {
        if (PlanningGraphValidation.IsLiteral(value.Value)
            ? PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(value.Value), schema).Count > 0
            : !PlanningGraphValidation.TypesFit(value.Schema, schema, allowUnresolved: false))
            Fail(code, "Produced " + Describe(value.Schema) + (value.TypeLocation is null ? "" : " from " + value.TypeLocation) +
                "; expected " + Describe(schema) + ". The business value does not satisfy its authoritative contract." +
                (optional ? " This argument may be omitted; if supplied it must satisfy the contract. Null is not omission." : ""));
    }

    private static string Describe(JsonObject schema) => (schema["type"]?.ToJsonString() ?? "opaque") +
        string.Concat(new[] { "enum", "pattern", "minLength", "maxLength", "default" }
            .Where(key => schema[key] is not null).Select(key => " " + key + " " + schema[key]!.ToJsonString()));

    private static IEnumerable<PlanningDiagnostic> ConstraintFindings(Bound value, JsonObject expected, string consumer)
    {
        if (value.TypeLocation is null) yield break;
        foreach (var finding in Inspect(value.Schema, expected, value.TypeLocation)) yield return finding;
        IEnumerable<PlanningDiagnostic> Inspect(JsonObject actual, JsonObject target, string path)
        {
            var types = PlanningContractCompatibility.Types(actual);
            var input = !value.TypeLocation.StartsWith("/tasks/", StringComparison.Ordinal);
            if (types.Contains("null", StringComparer.Ordinal) && PlanningContractValidation.ValidateInstance(null, actual).Count == 0 &&
                PlanningContractValidation.ValidateInstance(null, target).Count > 0)
                yield return new(input ? "TASK_INPUT_CONSTRAINT" : "TASK_TRANSFORM_CONSTRAINT", path + "/nullable", "The consumer " + consumer + " rejects null; declare non-null only when consistent with accepted requirements and producer guarantees, or change the diagnosed consumer binding. Expected " + Describe(target) + ".");
            // Nullability and the non-null string domain are independent constraints.
            // A nullable A|B producer flowing to A|B needs only its nullable slot repaired.
            if (!input && types.Where(t => t != "null").SequenceEqual(["string"]) && (target["enum"] is JsonArray { Count: > 0 } domain && domain.All(v => v is null || v is JsonValue j && j.TryGetValue<string>(out _)) ||
                    target["pattern"] is not null || target["minLength"] is not null || target["maxLength"] is not null))
            {
                var nonNull = actual.DeepClone().AsObject(); nonNull["type"] = "string";
                if (nonNull["enum"] is JsonArray values)
                    for (var i = values.Count - 1; i >= 0; i--) if (values[i] is null) values.RemoveAt(i);
                if (!PlanningGraphValidation.TypesFit(nonNull, target))
                    yield return new("TASK_TRANSFORM_CONSTRAINT", path + "/enum", "Declare a compatible finite string domain for " + consumer + "; expected " + Describe(target) + ".");
            }
            if (types.Contains("object", StringComparer.Ordinal) && actual["properties"] is JsonObject fields && target["properties"] is JsonObject wanted)
                foreach (var (name, child) in fields)
                    if (!name.Contains('/') && child is JsonObject produced && wanted[name] is JsonObject required)
                        foreach (var finding in Inspect(produced, required, path + "/fields/" + name + "/type")) yield return finding;
            if (types.Contains("array", StringComparer.Ordinal) && actual["items"] is JsonObject items && target["items"] is JsonObject expectedItems)
                foreach (var finding in Inspect(items, expectedItems, path + "/items")) yield return finding;
        }
    }
}
