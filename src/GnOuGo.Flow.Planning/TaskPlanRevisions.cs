using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Only diagnosed semantic slots are editable. Revalidation never grants edit permission.</summary>
internal static class TaskPlanRevisions
{
    internal static void ValidateEditablePaths(PlanningSession state, IReadOnlyList<string> paths)
    {
        if (state.Plan is null || state.Catalog is null || paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace) ||
            TaskPlanCompiler.IdentityDiagnostics(state.Plan, includeReferences: false).Count > 0)
            throw new PlanningConflictException("A targeted revision needs an unambiguous retained TaskPlan and explicit editable paths.");
        var symbols = new TaskPlanSymbols(state.Plan);
        var index = PlanningRepairPatch.Index(JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || paths.Count(p => p == path) != 1 ||
                paths.Any(p => p != path && p.StartsWith(path + "/", StringComparison.Ordinal)))
                throw new ArgumentException("Editable paths must be distinct, nonoverlapping semantic paths.");
            var binding = symbols.Values.ContainsKey(path) && (path.Contains("/inputs/", StringComparison.Ordinal) || path.Contains("/outputs/", StringComparison.Ordinal));
            var field = path.Split('/') is ["", "tasks", var id, var name] && symbols.Tasks.TryGetValue(id, out var task) &&
                (name == "inputs" && task.Task.Kind is "operation" or "transform" or "call" || name == "requires" || name == "condition" && task.Task.Kind == "conditional");
            if (PlanningRepairPatch.FindSite(index, path) is null || !binding && !field || OwnedInput(symbols, path, state.Catalog) is not null)
                throw new ArgumentException("EditablePaths supports existing business bindings, input lists, conditions and export values only: " + path);
        }
    }

    // Null means this is not a broken-reference slot. Empty means revision is
    // required: no existing, visible producer port can resolve this dependency.
    internal static IReadOnlyList<TaskValue>? ReferenceRepairs(PlanningSession state, string path)
    {
        if (state.EditablePaths is not null || !state.Diagnostics.Any(d => d.Required && d.Location == path &&
            d.Code is "TASK_OUTPUT_UNKNOWN" or "TASK_REFERENCE_UNKNOWN")) return null;
        var symbols = new TaskPlanSymbols(state.Plan!);
        if (!symbols.Values.TryGetValue(path, out var site)) return [];
        var references = TaskPlanCompiler.Values(site.Value).Where(v => v.Kind == "output").ToArray();
        if (references.Any(v => v.Source is null || !symbols.Tasks.ContainsKey(v.Source))) return [];
        var sourcePlan = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        // As in complete patch validation, a local witness permits preflight of
        // finite choices. No selection is applied to the session or approved.
        foreach (var choice in sourcePlan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var sources = TaskPlanCompiler.RepairSources(sourcePlan, state.Catalog!).GetValueOrDefault(path) ?? [];
        var broken = references.Where(v => !PlanningRepairContext.Ports(symbols.Tasks[v.Source!].Task, state).Contains(v.Port ?? "", StringComparer.Ordinal)).ToArray();
        if (broken.Length != 1) return [];
        var reference = broken[0]; var values = new List<TaskValue>();
        var outputType = path.Split('/') is ["", "root", "outputs", var outputName]
            ? state.Requirements?.Outputs?.SingleOrDefault(o => o.Name == outputName)?.Type : null;
        foreach (var source in sources.Where(s => s.Value.Kind == "output" && s.Value.Source == reference.Source))
        {
            if (ReferenceEquals(site.Value, reference) && outputType is not null &&
                !PlanningGraphValidation.TypesFit(source.Schema, TaskPlanCompiler.TypeSchema(outputType))) continue;
            var replacement = Rewrite(site.Value);
            var tree = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
            var target = PlanningRepairPatch.Index(tree)[path];
            if (target.Node is JsonObject binding && binding.ContainsKey("name") && binding.ContainsKey("value"))
                binding["value"] = JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue);
            else target.Set(JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue));
            var probe = tree.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
            foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            if (!new TaskPlanCompiler().Compile(probe, state.Catalog!).Diagnostics.Any(d => d.Required &&
                (d.Location == path || d.Location.StartsWith(path + "/", StringComparison.Ordinal)))) values.Add(replacement);

            TaskValue Rewrite(TaskValue value)
            {
                if (ReferenceEquals(value, reference)) return source.Value;
                var copy = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
                copy.Items = value.Items.Select(Rewrite).ToList();
                copy.Members = value.Members.Select(m => new TaskOutput(m.Name, Rewrite(m.Value))).ToList();
                return copy;
            }
        }
        return values;
    }

    internal sealed record Export(TaskPlanSymbols.Scope Scope, string Name, TaskValue? Value, TaskValue Producer, TaskPlanSymbols.Scope ProducerScope);

    internal static (Dictionary<string, TaskValue> Consumers, Dictionary<string, Export> Additions) Exports(TaskPlan plan, IEnumerable<string> paths)
    {
        var consumers = new Dictionary<string, TaskValue>(StringComparer.Ordinal);
        var additions = new Dictionary<string, Export>(StringComparer.Ordinal);
        var symbols = new TaskPlanSymbols(plan);
        foreach (var path in paths.Order(StringComparer.Ordinal))
            if (symbols.Values.TryGetValue(path, out var site))
            {
                var rewritten = Rewrite(site.Value, site.Scope);
                if (!Same(rewritten, site.Value)) consumers[path] = rewritten;
            }

        return (consumers, additions);

        TaskValue Rewrite(TaskValue value, TaskPlanSymbols.Scope scope)
        {
            var copy = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
            if (value.Kind == "output" && value.Source is { } id && symbols.ExportRoute(scope, id) is { Count: > 0 } route)
            {
                var current = copy;
                foreach (var boundary in route)
                {
                    var existing = boundary.Source.Outputs.Where(o => Same(o.Value, current)).OrderBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault();
                    var name = existing?.Name ?? additions.Values.FirstOrDefault(e => e.Scope == boundary && e.Value is not null && Same(e.Value, current))?.Name;
                    if (name is null)
                    {
                        var basis = boundary.Owner?.Kind == "parallel" ? current.Source + "_" + current.Port : current.Port ?? current.Source + "Result";
                        name = basis;
                        for (var suffix = 2; boundary.Source.Outputs.Any(o => o.Name == name) || additions.ContainsKey(boundary.Path + "/outputs/" + name); suffix++)
                            name = basis + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        additions.Add(boundary.Path + "/outputs/" + name, new(boundary, name, current, value, route[0]));
                    }
                    if (boundary.Owner?.Kind == "conditional")
                        foreach (var other in symbols.Scopes.Where(s => s.Owner == boundary.Owner && s != boundary && s.Source.Outputs.All(o => o.Name != name)))
                            additions.TryAdd(other.Path + "/outputs/" + name, new(other, name, null, value, route[0]));
                    current = new() { Kind = "output", Source = boundary.Owner!.Id, Port = name };
                }
                return current;
            }
            copy.Items = value.Items.Select(v => Rewrite(v, scope)).ToList();
            copy.Members = value.Members.Select(m => new TaskOutput(m.Name, Rewrite(m.Value, scope))).ToList();
            return copy;
        }
    }


    internal static bool FixedOperations(PlanningSession state) => state.EditablePaths is not null || state.Plan is not null && state.RevisionScope.Count > 0 &&
        state.Diagnostics.Any(d => d.Required) && !PlanningStructuralRepair.CanChangeOperations(state) && !state.RevisionScope.Any(p => p.EndsWith("/operation", StringComparison.Ordinal));

    internal static IEnumerable<PlanTask> Tasks(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t => new[] { t }.Concat(
        (t.Body is null ? [] : Tasks(t.Body)).Concat(t.Otherwise is null ? [] : Tasks(t.Otherwise)).Concat(t.Branches.SelectMany(Tasks))));
    internal static IEnumerable<PlanTask> Tasks(TaskPlan plan) => Tasks(plan.Root).Concat(plan.Groups.SelectMany(g => Tasks(g.Body)));
    internal static IEnumerable<PlanningDiagnostic> UnrepairableRequirements(PlanningSession state)
    {
        var symbols = new TaskPlanSymbols(state.Plan!);
        var catalog = PlanningStructuralRepair.Catalog(state);
        var exports = Exports(state.Plan!, state.RevisionScope);
        foreach (var finding in state.Diagnostics.Where(d => d.Required && !d.Location.EndsWith("/requires", StringComparison.Ordinal)))
            if (!exports.Consumers.ContainsKey(finding.Location) && ReferenceRepairs(state, finding.Location) is { Count: 0 })
                yield return finding with { Code = "REVISION_REQUIRED", Message = finding.Message +
                    " No compatible port of the referenced producer is available. Explicitly revise the dependency; a constant or unrelated producer cannot replace missing work." };
        foreach (var finding in state.Diagnostics.Where(d => d.Required && d.Code is "TASK_OUTPUT_UNKNOWN" or "TASK_REFERENCE_UNKNOWN" or "TASK_PRESENCE_SCOPE"))
        {
            if (finding.Location.Split('/') is not ["", "tasks", var consumer, "requires"] ||
                !symbols.Tasks.TryGetValue(consumer, out var site) || site.Task.Requires is not { } condition) continue;
            if (finding.Code is "TASK_REFERENCE_UNKNOWN" or "TASK_PRESENCE_SCOPE")
            {
                // Another diagnosed consumer may already authorize the missing data exports.
                // Such exports never make an inaccessible task identity visible to present.
                var conditionExports = Exports(state.Plan!, [finding.Location]);
                if (finding.Code != "TASK_PRESENCE_SCOPE" && conditionExports.Consumers.ContainsKey(finding.Location) &&
                    conditionExports.Additions.Count > 0 && conditionExports.Additions.Keys.All(state.RevisionScope.Contains) &&
                    TaskPlanCompiler.Values(condition).Where(v => v.Kind is "output" or "present").All(v =>
                        v.Source is { } source && symbols.Tasks.TryGetValue(source, out var producer) &&
                        (producer.Scope == site.Scope || IsAncestor(producer.Scope, site.Scope) ||
                            v.Kind == "output" && symbols.ExportRoute(site.Scope, source).Count > 0))) continue;
                yield return finding with { Code = "REVISION_REQUIRED", Message = finding.Message +
                    " The required condition is immutable and its reference cannot be made accessible by the issued edits. Explicitly revise the scope/binding; unrelated repairs cannot resolve it." };
                continue;
            }
            foreach (var reference in TaskPlanCompiler.Values(condition).Where(v => v.Kind == "output" && !string.IsNullOrEmpty(v.Port)))
            {
                if (reference.Source is not { } id || !symbols.Tasks.TryGetValue(id, out var producer) || producer.Task.Kind != "operation" ||
                    !catalog.Capabilities.Any(c => TaskOperations.Describe(c).Id == producer.Task.Operation) ||
                    state.RevisionScope.Any(p => p.StartsWith("/tasks/" + id + "/", StringComparison.Ordinal)) ||
                    PlanningRepairContext.Ports(producer.Task, state).Contains(reference.Port, StringComparer.Ordinal)) continue;
                yield return finding with { Code = "REVISION_REQUIRED", Message = finding.Message +
                    " The required condition and producer contract are outside repair authority. Explicitly revise the binding; unrelated repairs cannot resolve it." };
                break;
            }
        }

        static bool IsAncestor(TaskPlanSymbols.Scope ancestor, TaskPlanSymbols.Scope scope)
        {
            for (var parent = scope.Parent; parent is not null; parent = parent.Parent) if (parent == ancestor) return true;
            return false;
        }
    }
    internal static IReadOnlyList<string> Scope(TaskPlan plan, IReadOnlyList<PlanningDiagnostic> findings)
    {
        if (TaskPlanCompiler.InvalidDeclarations(plan).Count > 0) return [];
        var symbols = new TaskPlanSymbols(plan);
        var inputs = plan.Inputs.Select(i => "/inputs/" + i.Name).Concat(plan.Groups.SelectMany(g => g.Inputs.Select(i => "/groups/" + g.Id + "/inputs/" + i.Name))).ToHashSet(StringComparer.Ordinal);
        var scope = new HashSet<string>(StringComparer.Ordinal);
        var resultSlots = ProducerConstraintSlots(plan);
        foreach (var finding in findings.Where(d => d.Required && d.Code != "REVISION_SCOPE_CHANGED"))
        {
            var path = finding.Location;
            if (path.EndsWith("/requires", StringComparison.Ordinal)) continue;
            if (finding.Code == "TASK_KIND_INVALID" && path.Split('/') is ["", "tasks", var invalidTask, "kind"] && symbols.Tasks.ContainsKey(invalidTask)) { scope.Add(path); continue; }
            if (finding.Code is "TASK_TRANSFORM_TYPE" or "TASK_TRANSFORM_CONSTRAINT" or "TASK_INPUT_CONSTRAINT" && resultSlots.Contains(path)) { scope.Add(path); continue; }
            if (symbols.Values.ContainsKey(path) || inputs.Contains(path) || plan.Choices.Any(c => path == "/choices/" + c.Id)) scope.Add(path);
            else if (finding.Code == "TASK_EXPORT_REQUIRED" && symbols.Scopes.Any(s => s.Path + "/outputs" == path)) scope.Add(path);
            else if (finding.Code == "TASK_BRANCH_OUTPUTS" && symbols.Scopes.Any(s => s.Owner?.Kind == "conditional" && path.StartsWith(s.Path + "/outputs/", StringComparison.Ordinal))) scope.Add(path);
            else if (path.Split('/') is ["", "tasks", var id, var field] && symbols.Tasks.ContainsKey(id) &&
                field is "objective" or "operation" or "group" or "condition" or "items" or "dependsOn" or "maxItems" or "maxConcurrency") scope.Add(path);
            else if (finding.Code is "TASK_INPUT_REQUIRED" or "TASK_GROUP_INPUTS" && path.Split('/') is ["", "tasks", var taskId, "inputs", _] && symbols.Tasks.ContainsKey(taskId)) scope.Add(path);
        }
        // Compute only the necessary missing ports; existing exports stay immutable.
        var exports = Exports(plan, scope);
        scope.RemoveWhere(path => symbols.Scopes.Any(s => path == s.Path + "/outputs"));
        scope.UnionWith(exports.Additions.Keys);
        return scope.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<PlanningDiagnostic> Validate(TaskPlan? previous, TaskPlan candidate, IReadOnlyList<string> scope, PlanningCatalog? catalog = null, IReadOnlyList<string>? editablePaths = null)
    {
        var identities = TaskPlanCompiler.IdentityDiagnostics(candidate);
        if (previous is not null) identities = identities.Concat(TaskPlanCompiler.IdentityDiagnostics(previous, includeReferences: false)).Distinct().ToArray();
        if (identities.Count > 0)
        {
            foreach (var finding in identities) yield return finding;
            if (TaskPlanCompiler.InvalidDeclarations(candidate).Count > 0 || previous is not null && TaskPlanCompiler.InvalidDeclarations(previous).Count > 0) yield break;
        }
        if (previous is null) yield break;
        var symbols = new TaskPlanSymbols(previous); var revised = new TaskPlanSymbols(candidate);
        foreach (var (id, original) in symbols.Tasks)
            if (revised.Tasks.TryGetValue(id, out var updated) && original.Task.Each != updated.Task.Each)
                yield return new("REVISION_SCOPE_CHANGED", "/tasks/" + id + "/each", "Independent extraction semantics require an explicit revision and fresh approval.");
        foreach (var (id, original) in symbols.Tasks)
            if (editablePaths?.Contains("/tasks/" + id + "/requires") != true && revised.Tasks.TryGetValue(id, out var updated) && !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(original.Task.Requires, PlanningJsonContext.Default.TaskValue),
                JsonSerializer.SerializeToNode(updated.Task.Requires, PlanningJsonContext.Default.TaskValue)))
                yield return new("REVISION_SCOPE_CHANGED", "/tasks/" + id + "/requires", "Required conditions are immutable during repair; changing one requires an explicit revision.");
        var before = JsonSerializer.SerializeToNode(previous, PlanningJsonContext.Default.TaskPlan)!;
        var after = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!;
        var additions = new HashSet<string>(StringComparer.Ordinal);
        var removals = new HashSet<string>(StringComparer.Ordinal);
        var resultSlots = ProducerConstraintSlots(previous);
        var permittedValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in scope)
        {
            if (editablePaths?.Contains(path) == true) { permittedValues.Add(path); continue; }
            if (symbols.Values.ContainsKey(path) && path.Split('/') is ["", "tasks", var consumer, "inputs", var argument] &&
                symbols.Tasks.TryGetValue(consumer, out var originalTask) && revised.Tasks.TryGetValue(consumer, out var revisedTask) &&
                revisedTask.Task.Kind == "operation" && originalTask.Task.Operation == revisedTask.Task.Operation &&
                revisedTask.Task.Inputs.All(i => i.Name != argument) && RemovableInput(originalTask.Task, argument, catalog)) removals.Add(path);
            if (!path.EndsWith("/requires", StringComparison.Ordinal) && symbols.Values.TryGetValue(path, out var site) && revised.Values.TryGetValue(path, out var replacement))
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                var owned = OwnedInput(symbols, path, catalog);
                if (owned is null ? Related(site.Value, replacement.Value, site.Scope, used) : Same(site.Value, replacement.Value) ||
                    PlanningCapabilityArguments.RemovalOnly(owned, path.Split('/')[4], site.Value, replacement.Value))
                { permittedValues.Add(path); additions.UnionWith(used); }
            }
            // A diagnosed missing conditional counterpart has a fixed name. Its value
            // must be explicitly supplied and will undergo the complete compiler preflight.
            foreach (var boundary in symbols.Scopes.Where(s => s.Owner?.Kind == "conditional"))
            {
                var prefix = boundary.Path + "/outputs/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = path[prefix.Length..];
                var other = boundary.Owner!.Body == boundary.Source ? boundary.Owner.Otherwise : boundary.Owner.Body;
                if (boundary.Source.Outputs.All(o => o.Name != name) && other?.Outputs.Any(o => o.Name == name) == true) additions.Add(path);
            }
            if (path.Split('/') is ["", "tasks", var id, "inputs", var input] && symbols.Tasks.TryGetValue(id, out var task) && task.Task.Inputs.All(i => i.Name != input)) additions.Add(path);
        }
        Mask(before, "", false); Mask(after, "", true);
        foreach (var location in Differences(before, after, "").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            yield return new("REVISION_SCOPE_CHANGED", location, "This slot is outside the permitted repair. Preserve unaffected tasks, business interfaces, choices and ordering.");

        bool Related(TaskValue original, TaskValue replacement, TaskPlanSymbols.Scope owner, HashSet<string> used, bool preserve = false)
        {
            var routes = original.Kind == "output" && original.Source is { } producer ? symbols.ExportRoute(owner, producer) : [];
            if (routes.Count > 0)
            {
                if (Same(original, replacement)) return true; // Unchanged errors remain errors; no addition is authorized.
                var current = replacement;
                foreach (var boundary in routes.Reverse())
                {
                    if (current.Kind != "output" || current.Source != boundary.Owner!.Id || current.Port is null) return false;
                    var replacementScope = revised.Scopes.SingleOrDefault(s => s.Path == boundary.Path);
                    var exports = replacementScope?.Source.Outputs.Where(o => o.Name == current.Port).ToArray();
                    if (exports is not { Length: 1 }) return false;
                    if (boundary.Source.Outputs.All(o => o.Name != current.Port))
                    {
                        if (!scope.Contains(boundary.Path + "/outputs") && !scope.Contains(boundary.Path + "/outputs/" + current.Port)) return false;
                        used.Add(boundary.Path + "/outputs/" + current.Port);
                    }
                    if (boundary.Owner.Kind == "conditional")
                    {
                        var other = symbols.Scopes.Single(s => s.Owner == boundary.Owner && s != boundary);
                        var counterpart = revised.Scopes.SingleOrDefault(s => s.Path == other.Path);
                        if (counterpart?.Source.Outputs.Count(o => o.Name == current.Port) != 1) return false;
                        if (other.Source.Outputs.All(o => o.Name != current.Port))
                        {
                            if (!scope.Contains(other.Path + "/outputs") && !scope.Contains(other.Path + "/outputs/" + current.Port)) return false;
                            used.Add(other.Path + "/outputs/" + current.Port);
                        }
                    }
                    current = exports[0].Value;
                }
                return Same(original, current);
            }
            // A composite's unaffected members cannot hide unrelated edits when only
            // descendant references need an export route.
            if (TaskPlanCompiler.Values(original).Any(v => v.Kind == "output" && v.Source is { } id && symbols.ExportRoute(owner, id).Count > 0))
            {
                if (original.Kind != replacement.Kind || original.Members.Count != replacement.Members.Count || original.Items.Count != replacement.Items.Count) return false;
                for (var i = 0; i < original.Members.Count; i++)
                    if (original.Members[i].Name != replacement.Members[i].Name || !Related(original.Members[i].Value, replacement.Members[i].Value, owner, used, true)) return false;
                for (var i = 0; i < original.Items.Count; i++) if (!Related(original.Items[i], replacement.Items[i], owner, used, true)) return false;
                var a = JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.TaskValue)!.AsObject();
                var b = JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue)!.AsObject();
                a.Remove("members"); a.Remove("items"); b.Remove("members"); b.Remove("items");
                return JsonNode.DeepEquals(a, b);
            }
            return !preserve || Same(original, replacement);
        }
        void Mask(JsonNode? node, string path, bool updated)
        {
            if (node is JsonArray list)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var location = Element(path, list[i], i);
                    if (updated ? additions.Contains(location) : removals.Contains(location)) { list.RemoveAt(i); continue; }
                    Mask(list[i], location, updated);
                }
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["name"] is not null && obj.ContainsKey("value") && permittedValues.Contains(path)) { obj["value"] = null; return; }
            if (obj["name"] is not null && obj.ContainsKey("required") && scope.Contains(path))
            { foreach (var key in obj.Select(p => p.Key).Where(k => k != "name").ToArray()) obj.Remove(key); return; }
            if (obj["id"] is not null && path.StartsWith("/choices/", StringComparison.Ordinal) && scope.Contains(path))
            { foreach (var key in obj.Select(p => p.Key).Where(k => k is not ("id" or "selected")).ToArray()) obj.Remove(key); return; }
            foreach (var (key, child) in obj.ToArray())
            {
                var location = path + "/" + key;
                if (editablePaths?.Contains(location) == true) { obj[key] = null; continue; }
                if (scope.Contains(location) && (resultSlots.Contains(location) || permittedValues.Contains(location) || location.Split('/') is ["", "tasks", _, var field] &&
                    field is "objective" or "operation" or "group" or "dependsOn" or "maxItems" or "maxConcurrency")) obj[key] = null;
                else Mask(child, location, updated);
            }
        }
    }
    internal static PlanningCapability? OwnedInput(TaskPlanSymbols symbols, string path, PlanningCatalog? catalog)
    {
        if (catalog is null || path.Split('/') is not ["", "tasks", var id, "inputs", var name] || !symbols.Tasks.TryGetValue(id, out var site)) return null;
        var matches = catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == site.Task.Operation).ToArray();
        return matches.Length == 1 && TaskOperations.Validate(matches[0]).Count == 0 && site.Task.Inputs.Count(i => i.Name == name) == 1 &&
            PlanningCapabilityArguments.Assignment(matches[0], name, site.Task.Inputs.Single(i => i.Name == name).Value) ? matches[0] : null;
    }

    internal static bool RemovableInput(PlanTask task, string name, PlanningCatalog? catalog)
    {
        if (catalog is null || task.Kind != "operation" || task.Inputs.Count(i => i.Name == name) != 1) return false;
        var matches = catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
        if (matches.Length != 1 || TaskOperations.Validate(matches[0]).Count != 0) return false;
        var port = TaskOperations.Describe(matches[0]).Inputs.SingleOrDefault(p => p.Name == name);
        if (port is null) return false;
        if (PlanningCapabilityArguments.Owns(matches[0], port.Path)) return true;
        if (PlanningCapabilityArguments.Assignment(matches[0], name, task.Inputs.Single(i => i.Name == name).Value)) return false;
        if (port.Required) return false;
        // A producer mapping cannot override requiredness in the authoritative schema.
        var parent = matches[0].InputSchema;
        foreach (var segment in port.Path.SkipLast(1))
        {
            if (parent["properties"]?[segment] is not JsonObject child) return false;
            parent = child;
        }
        return parent["required"] is not JsonArray required || !required.Any(n => n?.ToString() == port.Path[^1]);
    }

    internal static HashSet<string> ProducerConstraintSlots(TaskPlan plan)
    {
        var slots = TransformResultSlots(plan);
        Inputs(plan.Inputs, "/inputs");
        foreach (var group in plan.Groups) Inputs(group.Inputs, "/groups/" + group.Id + "/inputs");
        return slots;
        void Inputs(List<TaskInput> inputs, string path)
        {
            foreach (var input in inputs.Where(i => !i.Name.Contains('/') && inputs.Count(other => other.Name == i.Name) == 1))
                Nullable(input.Type, path + "/" + input.Name + "/type");
        }
        void Nullable(TaskType type, string path)
        {
            slots.Add(path + "/nullable");
            if (type.Items is not null) Nullable(type.Items, path + "/items");
            foreach (var field in type.Fields.Where(f => !f.Name.Contains('/') && type.Fields.Count(other => other.Name == f.Name) == 1))
                Nullable(field.Type, path + "/fields/" + field.Name + "/type");
        }
    }

    // Only exact, unambiguous type slots grant permission. Existing field names and
    // order are immutable; a missing subtype may be supplied explicitly by repair.
    internal static HashSet<string> TransformResultSlots(TaskPlan plan)
    {
        var paths = new List<string>();
        foreach (var task in Tasks(plan).Where(t => t.Kind == "transform")) Add(task.ResultType, "/tasks/" + task.Id + "/resultType");
        return paths.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        void Add(TaskType? type, string path)
        {
            if (type is null) { paths.Add(path); return; }
            paths.Add(path + "/kind"); paths.Add(path + "/nullable");
            paths.Add(path + "/enum");
            Add(type.Items, path + "/items");
            // Adding fields is legal only when no existing declaration can be changed.
            if (type.Fields.Count == 0) paths.Add(path + "/fields");
            foreach (var field in type.Fields)
            {
                // Business names need not be path-safe. Do not interpret ambiguous
                // display locations as authority for nested type edits.
                if (field.Name.Contains('/') || type.Fields.Count(f => f.Name == field.Name) != 1) continue;
                var location = path + "/fields/" + field.Name;
                paths.Add(location + "/required"); paths.Add(location + "/default");
                Add(field.Type, location + "/type");
            }
        }
    }
    private static bool Same(TaskValue left, TaskValue right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.TaskValue), JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.TaskValue));
    internal static string Element(string path, JsonNode? node, int index)
    {
        var collection = path.Split('/')[^1];
        if (collection is "tasks" or "always" && node?["id"] is { } task) return "/tasks/" + task;
        return path + "/" + ((node as JsonObject)?[collection is "groups" or "choices" ? "id" : "name"]?.ToString() ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static IEnumerable<string> Differences(JsonNode? left, JsonNode? right, string path)
    {
        if (JsonNode.DeepEquals(left, right)) yield break;
        if (left is JsonObject a && right is JsonObject b)
        {
            foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key), StringComparer.Ordinal))
                foreach (var difference in Differences(a[key], b[key], path + "/" + key)) yield return difference;
        }
        else if (left is JsonArray aList && right is JsonArray bList && aList.Count == bList.Count)
        {
            for (var i = 0; i < aList.Count; i++)
                foreach (var difference in Differences(aList[i], bList[i], Element(path, aList[i], i))) yield return difference;
        }
        else yield return path;
    }
}
