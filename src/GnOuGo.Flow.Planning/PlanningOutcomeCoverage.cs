using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Version 2 review annotations describe support in the existing TaskPlan, never a second program.
internal static class PlanningOutcomeCoverage
{
    internal static List<PlanningDiagnostic> Findings(PlanningSession state)
    {
        var findings = new List<PlanningDiagnostic>();
        void Fail(string code, string path, string message) => findings.Add(new(code, path, message));
        if (state.Plan is null || state.Requirements is null || state.Catalog is null)
        { Fail("OUTCOME_CONTRACT_INVALID", "/requirements", "A complete plan and accepted requirements are required."); return findings; }
        if (TaskPlanCompiler.IdentityDiagnostics(state.Plan).Count > 0)
        { Fail("OUTCOME_TASK_INVALID", "/outcomeBindings", "Support requires unambiguous TaskPlan identities."); return findings; }
        var outcomes = state.Requirements.Outcomes; var bindings = state.OutcomeBindings;
        if (outcomes.Count == 0 || outcomes.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != outcomes.Count ||
            bindings is null || bindings.Any(b => b is null) ||
            !outcomes.Select(o => o.Id).Order(StringComparer.Ordinal).SequenceEqual(bindings.Select(b => b.OutcomeId).Order(StringComparer.Ordinal)))
        { Fail("OUTCOME_BINDINGS_INVALID", "/outcomeBindings", "Bind every accepted outcome exactly once."); return findings; }
        var symbols = new TaskPlanSymbols(state.Plan);
        var groups = state.Plan.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var links = symbols.Tasks.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        void Link(string a, string? b)
        { if (b is not null && links.ContainsKey(b)) links[a].Add(b); }
        foreach (var (id, site) in symbols.Tasks)
        {
            foreach (var dependency in site.Task.DependsOn) Link(id, dependency);
            foreach (var value in site.Task.Inputs.Concat(site.Task.Outputs).Select(v => v.Value)
                .Concat(site.Task.Items is { } items ? [items] : []).Concat(site.Task.Condition is { } condition ? [condition] : []))
                foreach (var reference in TaskPlanCompiler.Values(value).Where(v => v.Kind is "output" or "present")) Link(id, reference.Source);
            foreach (var scope in Children(site.Task, true))
                foreach (var reference in scope.Outputs.SelectMany(o => TaskPlanCompiler.Values(o.Value)).Where(v => v.Kind is "output" or "present"))
                    Link(id, reference.Source);
        }
        foreach (var outcome in outcomes)
        {
            var path = "/outcomeBindings/" + outcome.Id;
            var binding = bindings.Single(b => b.OutcomeId == outcome.Id);
            var perItem = outcome.Coverage == "each_item";
            if (outcome.Execution is not ("data" or "read" or "write" or "execute" or "lifecycle") || outcome.Always is null || outcome.Conditional is null ||
                outcome.Coverage is not (null or "once" or "each_item") || outcome.Execution == "data" && (perItem || outcome.Always == true || outcome.Conditional == true))
            { Fail("REQUIREMENTS_EXECUTION_INVALID", "/requirements/outcomes/" + outcome.Id, "Declare the effect, placement and once/each_item coverage without weakening accepted intent."); continue; }
            if (binding.TaskIds is null || binding.Outputs is null || binding.TaskIds.Count + binding.Outputs.Count == 0 ||
                binding.TaskIds.Any(string.IsNullOrWhiteSpace) || binding.Outputs.Any(string.IsNullOrWhiteSpace) ||
                binding.TaskIds.Distinct(StringComparer.Ordinal).Count() != binding.TaskIds.Count || binding.Outputs.Distinct(StringComparer.Ordinal).Count() != binding.Outputs.Count)
            { Fail("OUTCOME_BINDINGS_INVALID", path, "Supply distinct supporting task identities and reported outputs."); continue; }
            if (binding.Outputs.Any(name => state.Plan.Root.Outputs.All(o => o.Name != name)))
                Fail("OUTCOME_OUTPUT_INVALID", path, "A reported output does not exist.");
            var reachable = Reach(state.Plan.Root, false, perItem);
            var selected = binding.TaskIds.ToHashSet(StringComparer.Ordinal);
            var support = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in selected)
            {
                if (!symbols.Tasks.TryGetValue(id, out var site) || !reachable.Any(r => r.Id == id))
                { Fail("OUTCOME_TASK_UNREACHABLE", path, "Supporting task '" + id + "' is absent or unreachable."); continue; }
                support.Add(id);
                foreach (var child in Children(site.Task, perItem)) support.UnionWith(Reach(child, false, perItem).Select(t => t.Id));
            }
            if (outcome.Execution == "data")
            {
                if (binding.ForEachTaskId is not null) Fail("OUTCOME_COVERAGE_INVALID", path, "Data outcomes cannot declare execution coverage.");
                continue;
            }
            PlanTask? loop = null;
            if (perItem)
            {
                if (binding.ForEachTaskId is null || !symbols.Tasks.TryGetValue(binding.ForEachTaskId, out var loopSite) || loopSite.Task.Kind != "foreach" ||
                    loopSite.Task.Body is null || !reachable.Any(r => r.Id == binding.ForEachTaskId))
                { Fail("OUTCOME_COVERAGE_INVALID", path, "Bind each_item coverage to one reachable existing foreach task."); continue; }
                loop = loopSite.Task;
                var bodyIds = Reach(loop.Body!, false, true).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                support.IntersectWith(bodyIds);
            }
            else if (binding.ForEachTaskId is not null)
            { Fail("OUTCOME_COVERAGE_INVALID", path, "Only accepted each_item outcomes may bind a foreach task."); continue; }
            var witnesses = new HashSet<string>(StringComparer.Ordinal);
            var unknown = new List<string>();
            foreach (var id in support)
            {
                var task = symbols.Tasks[id].Task;
                if (task.Kind != "operation") continue;
                var contracts = state.Catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
                if (contracts.Length != 1 || !state.Catalog.AllowedStepTypes.Contains(contracts[0].StepType) || state.Catalog.Policy.DeniedCapabilityIds.Contains(contracts[0].Id))
                { Fail("OUTCOME_OPERATION_UNAVAILABLE", path, "Supporting operation '" + id + "' has no resolved policy-allowed contract."); continue; }
                if (contracts[0].EffectKind == "unknown") unknown.Add(id);
                if (contracts[0].EffectKind == outcome.Execution) witnesses.Add(id);
            }
            if (witnesses.Count == 0)
            {
                Fail(unknown.Count > 0 ? "OUTCOME_EFFECT_UNDECLARED" : "OUTCOME_OPERATION_REQUIRED", path,
                    unknown.Count > 0 ? "Producer effect metadata is missing for: " + string.Join(", ", unknown) + ". Refresh authoritative discovery; no effect may be inferred from names or prose." :
                    "The supporting computation has no resolved " + outcome.Execution + " operation. Outputs, values and transforms cannot establish external work.");
                continue;
            }
            // Helpers are permitted only when connected to a witness by actual bindings/dependencies.
            var connected = new HashSet<string>(witnesses, StringComparer.Ordinal);
            // Follow producers and consumers separately. An unrelated sibling sharing
            // a prerequisite with a witness is not part of that witness's computation.
            foreach (var upstream in new[] { true, false })
            {
                var visited = new HashSet<string>(witnesses, StringComparer.Ordinal); var queue = new Queue<string>(witnesses);
                while (queue.TryDequeue(out var id))
                    foreach (var adjacent in upstream ? links[id] : links.Where(p => p.Value.Contains(id)).Select(p => p.Key))
                        if (visited.Add(adjacent)) { connected.Add(adjacent); queue.Enqueue(adjacent); }
            }
            foreach (var id in selected.Where(symbols.Tasks.ContainsKey))
                if (!connected.Contains(id) && !Children(symbols.Tasks[id].Task, perItem).Any(s => Reach(s, false, perItem).Any(t => witnesses.Contains(t.Id))))
                    Fail("OUTCOME_SUPPORT_DISCONNECTED", path, "Task '" + id + "' is unrelated to the effect-producing computation.");
            if (!witnesses.Any(id => reachable.Contains((id, outcome.Always.Value))))
                Fail("OUTCOME_PLACEMENT_INVALID", path, "No supporting operation executes in the accepted normal/always placement.");
            if (loop is not null)
            {
                if (!reachable.Where(r => r.Id == loop.Id).Any(r =>
                    Guarantees(loop.Body!, witnesses, outcome.Always.Value, r.Always, false, []) &&
                    (outcome.Conditional == true || Guarantees(state.Plan.Root, new(StringComparer.Ordinal) { loop.Id }, r.Always, false, true, []))))
                    Fail("OUTCOME_PATH_INCOMPLETE", path, "Every item requires an operation on every body path; the collection scope cannot be silently skipped.");
            }
            else if (outcome.Conditional == false && !Guarantees(state.Plan.Root, witnesses, outcome.Always.Value, false, false, []))
                Fail("OUTCOME_PATH_INCOMPLETE", path, "At least one matching operation must execute on every accepted path, including empty-collection paths.");
        }
        return findings;

        IEnumerable<TaskScope> Children(PlanTask task, bool perItem)
        {
            if (task.Kind == "call") return groups.TryGetValue(task.Group ?? "", out var group) ? [group.Body] : [];
            if (!perItem && task.Kind == "foreach" && task.Items is { Kind: "array", Items.Count: 0 }) return [];
            if (task.Kind == "conditional" && task.Condition is { Kind: "boolean" } condition)
                return (condition.Boolean == true ? task.Body : task.Otherwise) is { } branch ? [branch] : [];
            return (task.Body is null ? Enumerable.Empty<TaskScope>() : [task.Body]).Concat(task.Otherwise is null ? [] : [task.Otherwise]).Concat(task.Branches);
        }
        HashSet<(string Id, bool Always)> Reach(TaskScope root, bool always, bool perItem)
        {
            var found = new HashSet<(string Id, bool Always)>(); var visited = new HashSet<(TaskScope, bool)>();
            void Walk(TaskScope scope, bool cleanup)
            {
                if (!visited.Add((scope, cleanup))) return;
                foreach (var task in scope.Tasks.Concat(scope.Always))
                {
                    var placement = cleanup || scope.Always.Contains(task); found.Add((task.Id, placement));
                    foreach (var child in Children(task, perItem)) Walk(child, placement);
                }
            }
            Walk(root, always); return found;
        }
        bool Guarantees(TaskScope scope, HashSet<string> selected, bool requiredAlways, bool always, bool perItem,
            Dictionary<(TaskScope, bool), bool> memo)
        {
            if (memo.TryGetValue((scope, always), out var cached)) return cached;
            memo[(scope, always)] = false;
            var result = scope.Tasks.Concat(scope.Always).Any(task =>
            {
                var cleanup = always || scope.Always.Contains(task);
                if (selected.Contains(task.Id) && cleanup == requiredAlways) return true;
                if (!perItem && task.Kind == "foreach" && task.Items is not { Kind: "array", Items.Count: > 0 }) return false;
                var children = Children(task, perItem).ToArray();
                return task.Kind == "conditional" && task.Condition?.Kind != "boolean"
                    ? task.Body is not null && task.Otherwise is not null && children.All(s => Guarantees(s, selected, requiredAlways, cleanup, perItem, memo))
                    : children.Any(s => Guarantees(s, selected, requiredAlways, cleanup, perItem, memo));
            });
            memo[(scope, always)] = result; return result;
        }
    }
}
