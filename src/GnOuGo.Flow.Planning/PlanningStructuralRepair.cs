using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Deterministic permissions over the existing TaskPlan, never another plan representation.
internal static class PlanningStructuralRepair
{
    private const int MaxPrerequisites = 8;
    internal static bool CanChangeOperations(PlanningSession state) => state.Plan is not null && state.Diagnostics.Any(d => d.Required &&
        d.Code is "TASK_ARTIFACT_PREREQUISITE_MISSING" or "TASK_KIND_INVALID" or "TASK_OPERATION_UNKNOWN" && state.RevisionScope.Contains(d.Location));

    internal static PlanningCatalog Catalog(PlanningSession state) => new()
    {
        AllowedStepTypes = state.Catalog!.AllowedStepTypes, StepContracts = state.Catalog.StepContracts, Policy = state.Catalog.Policy,
        Capabilities = state.Catalog.Capabilities.Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
            .GroupBy(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => g.Single()).Where(c => TaskOperations.Validate(c).Count == 0 && state.Catalog.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).ToList()
    };
    internal static IEnumerable<PlanningOperation> Operations(PlanningSession state)
    {
        if (!CanChangeOperations(state)) return [];
        var catalog = Catalog(state);
        if (state.Diagnostics.Any(d => d.Required && d.Code is "TASK_KIND_INVALID" or "TASK_OPERATION_UNKNOWN" && state.RevisionScope.Contains(d.Location)))
            return catalog.Capabilities.Select(PlanningCapabilityArguments.Editable);
        var kinds = Missing(state, catalog).SelectMany(m => m.Kinds).ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal); var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var capability in catalog.Capabilities.Where(c => c.ArtifactContract?.Produces.Any(p => kinds.Contains(p.Kind)) == true))
                if (selected.Add(capability.Id))
                {
                    changed = true;
                    foreach (var required in capability.ArtifactContract!.Consumes.Where(c => c.Required)) kinds.Add(required.Kind);
                }
        }
        return catalog.Capabilities.Where(c => selected.Contains(c.Id)).Select(PlanningCapabilityArguments.Editable);
    }
    private sealed record MissingInput(string Task, string Input, string[] Kinds);
    private static IEnumerable<MissingInput> Missing(PlanningSession state, PlanningCatalog catalog)
    {
        var symbols = new TaskPlanSymbols(state.Plan!);
        foreach (var finding in state.Diagnostics.Where(d => d.Required && d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING" && state.RevisionScope.Contains(d.Location)))
        {
            if (finding.Location.Split('/') is not ["", "tasks", var id, "inputs", var name] || !symbols.Values.ContainsKey(finding.Location) || !symbols.Tasks.TryGetValue(id, out var site)) continue;
            var capability = catalog.Capabilities.SingleOrDefault(c => TaskOperations.Describe(c).Id == site.Task.Operation);
            var port = capability is null ? null : TaskOperations.Describe(capability).Inputs.SingleOrDefault(p => p.Name == name);
            if (port is null || capability!.ArtifactContract is null) continue;
            var kinds = capability.ArtifactContract.Consumes.Where(c => c.Required && TaskArtifactBindings.Decode(c.Pointer).Take(port.Path.Count).SequenceEqual(port.Path, StringComparer.Ordinal)).Select(c => c.Kind).Distinct(StringComparer.Ordinal).ToArray();
            if (kinds.Length > 0 && kinds.All(k => catalog.Capabilities.Any(c => c.ArtifactContract?.Produces.Any(p => p.Kind == k) == true))) yield return new(id, name, kinds);
        }
    }
    internal static IReadOnlyList<PlanningRepairPatch.Slot> Slots(PlanningSession state, JsonObject definitions)
    {
        if (state.Plan is null || TaskPlanCompiler.InvalidDeclarations(state.Plan).Count > 0) return [];
        var result = new List<PlanningRepairPatch.Slot>(); var symbols = new TaskPlanSymbols(state.Plan); var catalog = Catalog(state);
        var allowed = Operations(state).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        JsonObject OperationSchema() => new() { ["anyOf"] = new JsonArray(catalog.Capabilities.Where(c => allowed.Contains(TaskOperations.Describe(c).Id)).Select(c =>
        {
            var operation = PlanningCapabilityArguments.Editable(c);
            var inputs = operation.Inputs.Select(p => (JsonNode)PlanningSchemas.Object(("name", PlanningSchemas.Enum(p.Name)), ("value", PlanningBindingSchemas.For(p.Schema, definitions)))).ToArray();
            var name = "repairInputs" + definitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            definitions[name] = inputs.Length == 0 ? PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, 0) : PlanningSchemas.Array(new JsonObject { ["anyOf"] = new JsonArray(inputs) });
            return (JsonNode)PlanningSchemas.Object(("id", PlanningSchemas.Ref("id")), ("kind", PlanningSchemas.Enum("operation")), ("objective", PlanningSchemas.Ref("goal")),
                ("dependsOn", PlanningSchemas.Ref("identities")), ("operation", PlanningSchemas.Enum(operation.Id)),
                ("inputs", PlanningSchemas.Ref(name)));
        }).ToArray()) };
        foreach (var missing in Missing(state, catalog))
        {
            // Cleanup insertion has different lifetime semantics; it requires explicit revision.
            if (!symbols.Tasks[missing.Task].Scope.Source.Tasks.Any(t => t.Id == missing.Task)) continue;
            var schema = PlanningSchemas.Object(("tasks", PlanningSchemas.Array(OperationSchema(), 1, MaxPrerequisites)), ("value", PlanningSchemas.Ref("value")));
            result.Add(new("", "/tasks/" + missing.Task + "/prerequisites/" + missing.Input, "prerequisites", schema, ["insert_prerequisites"]));
        }
        foreach (var finding in state.Diagnostics.Where(d => d.Required && state.RevisionScope.Contains(d.Location)))
        {
            if (finding.Location.Split('/') is not ["", "tasks", var id, var field] || !symbols.Tasks.TryGetValue(id, out var site)) continue;
            var task = site.Task;
            if (finding.Code is "TASK_KIND_INVALID" or "TASK_OPERATION_UNKNOWN" && field is "kind" or "operation" &&
                task.Body is null && task.Otherwise is null && task.Branches.Count == 0 && task.Outputs.Count == 0 && allowed.Count > 0)
            {
                var schema = OperationSchema();
                foreach (var alternative in schema["anyOf"]!.AsArray())
                {
                    alternative!["properties"]!["id"] = PlanningSchemas.Enum(id);
                    alternative["properties"]!["objective"] = PlanningSchemas.Enum(task.Objective);
                    alternative["properties"]!["dependsOn"] = PlanningSchemas.Ref("identities");
                }
                result.Add(new("", "/tasks/" + id, "task", schema, ["replace_task"]));
            }
            if (finding.Code == "TASK_DEPENDENCY_UNKNOWN" && field == "dependsOn" && Removable(state.Plan, task, symbols))
                result.Add(new("", "/tasks/" + id, "forwarder", PlanningSchemas.Type("null"), ["remove_forwarder"]));
        }
        return result.DistinctBy(s => s.Location).OrderBy(s => s.Location, StringComparer.Ordinal).ToArray();
    }
    private static bool Removable(TaskPlan plan, PlanTask task, TaskPlanSymbols symbols)
    {
        if (task.Kind != "value" || task.Inputs.Count > 0 || task.Outputs.Count == 0 || task.Body is not null || task.Otherwise is not null || task.Branches.Count > 0 ||
            task.Outputs.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != task.Outputs.Count ||
            task.Outputs.Any(o => o.Value.Kind != "output" || o.Value.Source == task.Id || o.Value.Source is null || !symbols.Tasks.ContainsKey(o.Value.Source)) ||
            !symbols.Tasks[task.Id].Scope.Source.Tasks.Contains(task)) return false;
        if (task.DependsOn.Any(d => symbols.Tasks.ContainsKey(d) && !task.Outputs.Any(o => o.Value.Source == d))) return false;
        return symbols.Values.Values.SelectMany(v => TaskPlanCompiler.Values(v.Value)).Where(v => v.Source == task.Id)
            .All(v => v.Kind == "output" && v.Port is not null && task.Outputs.Any(o => o.Name == v.Port)) &&
            TaskPlanRevisions.Tasks(plan).Where(t => t.DependsOn.Contains(task.Id)).All(t => symbols.Tasks[t.Id].Scope == symbols.Tasks[task.Id].Scope);
    }
    internal static void Apply(TaskPlan plan, PlanningRepairPatch.Slot slot, RepairEdit edit)
    {
        var symbols = new TaskPlanSymbols(plan); var parts = slot.Location.Split('/'); var id = parts[2]; var site = symbols.Tasks[id];
        if (edit.Action == "insert_prerequisites")
        {
            var tasks = edit.Value!["tasks"]!.AsArray().Select(n => n!.Deserialize(PlanningJsonContext.Default.PlanTask)!).ToArray();
            if (tasks.Any(t => symbols.Tasks.ContainsKey(t.Id)) || tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tasks.Length) Reject(slot.Location);
            site.Scope.Source.Tasks.InsertRange(site.Scope.Source.Tasks.IndexOf(site.Task), tasks);
            var input = parts[4]; var index = site.Task.Inputs.FindIndex(i => i.Name == input);
            site.Task.Inputs[index] = new(input, edit.Value["value"]!.Deserialize(PlanningJsonContext.Default.TaskValue)!);
        }
        else if (edit.Action == "replace_task")
        {
            var task = edit.Value!.Deserialize(PlanningJsonContext.Default.PlanTask)!;
            if (task.Id != id || task.Objective != site.Task.Objective || !task.DependsOn.SequenceEqual(site.Task.DependsOn)) Reject(slot.Location);
            // Replacement changes the diagnosed operation, not its existing business
            // arguments or other task semantics. Further diagnosed bindings have their
            // own slots; required arguments may be appended for the resolved contract.
            if (task.Inputs.Count < site.Task.Inputs.Count || !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(task.Inputs.Take(site.Task.Inputs.Count).ToList(), PlanningJsonContext.Default.ListTaskOutput),
                JsonSerializer.SerializeToNode(site.Task.Inputs, PlanningJsonContext.Default.ListTaskOutput))) Reject(slot.Location);
            var original = JsonSerializer.SerializeToNode(site.Task, PlanningJsonContext.Default.PlanTask)!.AsObject();
            var replacement = JsonSerializer.SerializeToNode(task, PlanningJsonContext.Default.PlanTask)!.AsObject();
            foreach (var field in new[] { "kind", "operation", "inputs" }) { original.Remove(field); replacement.Remove(field); }
            if (!JsonNode.DeepEquals(original, replacement)) Reject(slot.Location);
            var list = site.Scope.Source.Tasks.Contains(site.Task) ? site.Scope.Source.Tasks : site.Scope.Source.Always;
            list[list.IndexOf(site.Task)] = task;
        }
        else if (edit.Action == "remove_forwarder")
        {
            if (!Removable(plan, site.Task, symbols)) Reject(slot.Location);
            foreach (var value in symbols.Values.Values.SelectMany(v => TaskPlanCompiler.Values(v.Value)).Where(v => v.Kind == "output" && v.Source == id))
            {
                var forwarded = site.Task.Outputs.Single(o => o.Name == value.Port).Value;
                value.Source = forwarded.Source; value.Port = forwarded.Port;
            }
            foreach (var task in TaskPlanRevisions.Tasks(plan).Where(t => t.DependsOn.Contains(id)))
                task.DependsOn = task.DependsOn.SelectMany(d => d == id ? site.Task.Outputs.Select(o => o.Value.Source!) : [d]).Distinct(StringComparer.Ordinal).ToList();
            site.Scope.Source.Tasks.Remove(site.Task);
        }
        else Reject(slot.Location);
    }
    internal static void Validate(PlanningSession state, TaskPlan candidate, IReadOnlyList<PlanningRepairPatch.Slot> used)
    {
        var before = new TaskPlanSymbols(state.Plan!); var after = new TaskPlanSymbols(candidate); var catalog = Catalog(state);
        var added = after.Tasks.Keys.Where(id => !before.Tasks.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        void Trace(TaskValue value, IReadOnlyList<string> path, string kind)
        {
            if (value.Kind == "field" && value.Items.Count == 1 && value.Port is not null) { Trace(value.Items[0], new[] { value.Port }.Concat(path).ToArray(), kind); return; }
            if (value.Kind == "object" && path.Count > 0 && value.Members.Count(m => m.Name == path[0]) == 1)
            { Trace(value.Members.Single(m => m.Name == path[0]).Value, path.Skip(1).ToArray(), kind); return; }
            if (value.Kind != "output" || value.Source is null || !added.Contains(value.Source)) return;
            var producer = after.Tasks[value.Source].Task;
            var capability = catalog.Capabilities.SingleOrDefault(c => TaskOperations.Describe(c).Id == producer.Operation);
            var port = value.Port is null ? null : capability is null ? null : TaskOperations.Describe(capability).Outputs.SingleOrDefault(p => p.Name == value.Port);
            var selectedPath = value.Port is null ? path : (port?.Path ?? []).Concat(path).ToArray();
            if (value.Port is not null && port is null || capability?.ArtifactContract?.Produces.Any(p => p.Kind == kind && TaskArtifactBindings.Decode(p.Pointer).SequenceEqual(selectedPath, StringComparer.Ordinal)) != true) Reject("/tasks/" + producer.Id);
            if (!reached.Add(producer.Id)) return;
            var references = producer.Inputs.SelectMany(i => TaskPlanCompiler.Values(i.Value)).Where(v => v.Kind == "output" && v.Source is not null).Select(v => v.Source).ToHashSet(StringComparer.Ordinal);
            if (producer.DependsOn.Any(d => !references.Contains(d))) Reject("/tasks/" + producer.Id + "/dependsOn");
            foreach (var required in capability!.ArtifactContract!.Consumes.Where(c => c.Required))
            {
                var requiredPath = TaskArtifactBindings.Decode(required.Pointer);
                var inputPort = TaskOperations.Describe(capability).Inputs.Where(p => requiredPath.Take(p.Path.Count).SequenceEqual(p.Path, StringComparer.Ordinal)).OrderByDescending(p => p.Path.Count).FirstOrDefault();
                var input = producer.Inputs.SingleOrDefault(i => i.Name == inputPort?.Name);
                if (input is not null) Trace(input.Value, requiredPath.Skip(inputPort!.Path.Count).ToArray(), required.Kind);
            }
        }
        foreach (var slot in used.Where(s => s.Kind == "prerequisites"))
        {
            var parts = slot.Location.Split('/'); var requirement = Missing(state, catalog).Single(m => m.Task == parts[2] && m.Input == parts[4]);
            var task = after.Tasks[requirement.Task].Task;
            var capability = catalog.Capabilities.Single(c => TaskOperations.Describe(c).Id == task.Operation);
            var port = TaskOperations.Describe(capability).Inputs.Single(p => p.Name == requirement.Input);
            foreach (var consumed in capability.ArtifactContract!.Consumes.Where(c => c.Required && TaskArtifactBindings.Decode(c.Pointer).Take(port.Path.Count).SequenceEqual(port.Path, StringComparer.Ordinal)))
                Trace(task.Inputs.Single(i => i.Name == port.Name).Value, TaskArtifactBindings.Decode(consumed.Pointer).Skip(port.Path.Count).ToArray(), consumed.Kind);
        }
        if (!added.SetEquals(reached)) Reject("/patch/edits");
        var validated = JsonSerializer.Deserialize(JsonSerializer.Serialize(candidate, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        foreach (var choice in validated.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var findings = new TaskPlanCompiler().Compile(validated, catalog).Diagnostics;
        if (findings.Count > 0) throw new PlanningResponseException(findings.ToList());
    }
    private static void Reject(string path) => throw new PlanningResponseException([new("REVISION_SCOPE_CHANGED", path, "This structural edit has no complete diagnostic-derived dependency proof.")]);
}
