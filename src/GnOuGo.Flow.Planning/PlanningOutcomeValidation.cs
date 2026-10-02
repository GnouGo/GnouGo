using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Checks review annotations against executable contracts, never inferring effects from prose.</summary>
internal static class PlanningOutcomeValidation
{
    internal static List<PlanningDiagnostic> Findings(PlanningSession state)
    {
        if (state.OutcomeVersion is null) return [];
        if (state.OutcomeVersion == 2) return PlanningOutcomeCoverage.Findings(state);
        var findings = new List<PlanningDiagnostic>();
        void Fail(string code, string path, string message) => findings.Add(new(code, path, message));
        if (state.OutcomeVersion != 1 || state.Requirements is null || state.Plan is null || state.Catalog is null)
        { Fail("OUTCOME_CONTRACT_INVALID", "/requirements", "A supported outcome contract and complete plan are required."); return findings; }
        var outcomes = state.Requirements.Outcomes;
        var bindings = state.OutcomeBindings;
        if (outcomes.Count == 0 || outcomes.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != outcomes.Count ||
            bindings is null || bindings.Any(b => b is null) ||
            !outcomes.Select(o => o.Id).Order(StringComparer.Ordinal).SequenceEqual(bindings.Select(b => b.OutcomeId).Order(StringComparer.Ordinal)))
        { Fail("OUTCOME_BINDINGS_INVALID", "/outcomeBindings", "Bind every accepted outcome exactly once, without additions or omissions."); return findings; }
        if (TaskPlanCompiler.IdentityDiagnostics(state.Plan).Count > 0)
        { Fail("OUTCOME_TASK_INVALID", "/outcomeBindings", "Outcome references require unambiguous valid TaskPlan identities."); return findings; }
        var reachable = new List<(PlanTask Task, bool Always)>();
        var visited = new HashSet<(TaskScope Scope, bool Always)>();
        var groups = state.Plan.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        Walk(state.Plan.Root, false, new(StringComparer.Ordinal));
        foreach (var outcome in outcomes)
        {
            var path = "/outcomeBindings/" + outcome.Id;
            var binding = bindings.Single(b => b.OutcomeId == outcome.Id);
            if (outcome.Execution is not ("data" or "read" or "write" or "execute" or "lifecycle") || outcome.Always is null || outcome.Conditional is null ||
                outcome.Execution == "data" && (outcome.Always == true || outcome.Conditional == true))
            { Fail("REQUIREMENTS_EXECUTION_INVALID", "/requirements/outcomes/" + outcome.Id, "Declare the operation effect, cleanup placement and conditional execution, or data production."); continue; }
            if (binding.TaskIds is null || binding.Outputs is null ||
                binding.TaskIds.Any(string.IsNullOrWhiteSpace) || binding.Outputs.Any(string.IsNullOrWhiteSpace) ||
                binding.TaskIds.Distinct(StringComparer.Ordinal).Count() != binding.TaskIds.Count || binding.Outputs.Distinct(StringComparer.Ordinal).Count() != binding.Outputs.Count ||
                binding.TaskIds.Count + binding.Outputs.Count == 0)
            { Fail("OUTCOME_BINDINGS_INVALID", path, "Supply distinct supporting tasks or root outputs."); continue; }
            if (binding.Outputs.Any(name => state.Plan.Root.Outputs.All(o => o.Name != name)))
                Fail("OUTCOME_OUTPUT_INVALID", path + "/outputs", "A supporting root output does not exist.");
            var tasks = binding.TaskIds.ToHashSet(StringComparer.Ordinal);
            foreach (var id in tasks)
            {
                var occurrences = reachable.Where(t => t.Task.Id == id).ToArray();
                if (occurrences.Length == 0)
                { Fail("OUTCOME_TASK_UNREACHABLE", path + "/taskIds", "Supporting task '" + id + "' is absent or unreachable from the root."); continue; }
                if (outcome.Execution == "data") continue;
                var task = occurrences[0].Task;
                var contracts = state.Catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
                if (task.Kind != "operation" || contracts.Length != 1 || contracts[0].EffectKind != outcome.Execution ||
                    !state.Catalog.AllowedStepTypes.Contains(contracts[0].StepType) || state.Catalog.Policy.DeniedCapabilityIds.Contains(contracts[0].Id))
                    Fail("OUTCOME_OPERATION_REQUIRED", path + "/taskIds", "Outcome '" + outcome.Id + "' requires a resolved, policy-allowed " + outcome.Execution +
                        " operation; task '" + id + "' cannot establish it. Discover the required contract or explicitly revise the implementation; values and transforms cannot perform external work.");
                if (!occurrences.Any(t => t.Always == outcome.Always))
                    Fail("OUTCOME_PLACEMENT_INVALID", path + "/taskIds", "Supporting task '" + id + "' has no invocation in the accepted normal/always placement.");
            }
            if (outcome.Execution == "data") continue;
            if (binding.Outputs.Count > 0 || tasks.Count == 0)
                Fail("OUTCOME_OPERATION_REQUIRED", path, "An external outcome requires operation tasks. Output values are not execution evidence.");
            if (outcome.Conditional == false && !Guarantees(state.Plan.Root, tasks, outcome.Always.Value, false, new(StringComparer.Ordinal), []))
                Fail("OUTCOME_PATH_INCOMPLETE", path, "The supporting operations do not cover all normal paths. A branch or possibly empty collection cannot silently make an unconditional outcome optional.");
        }
        return findings;

        void Walk(TaskScope scope, bool always, HashSet<string> stack)
        {
            if (!visited.Add((scope, always))) return;
            foreach (var task in scope.Tasks.Concat(scope.Always))
            {
                var cleanup = always || scope.Always.Contains(task);
                reachable.Add((task, cleanup));
                foreach (var child in Children(task))
                {
                    if (task.Kind == "call" && !stack.Add(task.Group!)) continue;
                    Walk(child, cleanup, stack);
                    if (task.Kind == "call") stack.Remove(task.Group!);
                }
            }
        }
        IEnumerable<TaskScope> Children(PlanTask task)
        {
            if (task.Kind == "call") return groups.TryGetValue(task.Group ?? "", out var group) ? [group.Body] : [];
            if (task.Kind == "foreach" && task.Items is { Kind: "array", Items.Count: 0 }) return [];
            if (task.Kind == "conditional" && task.Condition is { Kind: "boolean" } condition)
                return (condition.Boolean == true ? task.Body : task.Otherwise) is { } branch ? [branch] : [];
            return (task.Body is null ? Enumerable.Empty<TaskScope>() : [task.Body])
                .Concat(task.Otherwise is null ? [] : [task.Otherwise]).Concat(task.Branches);
        }
        bool Guarantees(TaskScope scope, HashSet<string> selected, bool requiredAlways, bool always, HashSet<string> stack,
            Dictionary<(TaskScope Scope, bool Always), bool> memo)
        {
            if (memo.TryGetValue((scope, always), out var known)) return known;
            memo[(scope, always)] = false;
            var result = scope.Tasks.Concat(scope.Always).Any(task =>
            {
                var cleanup = always || scope.Always.Contains(task);
                if (selected.Contains(task.Id) && cleanup == requiredAlways) return true;
                if (task.Kind == "call" && !stack.Add(task.Group!)) return false;
                try
                {
                    if (task.Kind == "foreach" && task.Items is not { Kind: "array", Items.Count: > 0 }) return false;
                    var children = Children(task).ToArray();
                    if (task.Kind == "conditional" && task.Condition?.Kind != "boolean")
                        return task.Body is not null && task.Otherwise is not null && children.All(s => Guarantees(s, selected, requiredAlways, cleanup, stack, memo));
                    return children.Any(s => Guarantees(s, selected, requiredAlways, cleanup, stack, memo));
                }
                finally { if (task.Kind == "call") stack.Remove(task.Group!); }
            });
            memo[(scope, always)] = result; return result;
        }
    }

    internal static IEnumerable<PlanningValidationResult> Review(PlanningSession state) => state.OutcomeVersion is not (1 or 2) ? [] :
        state.Requirements!.Outcomes.Select(o =>
        {
            var binding = state.OutcomeBindings!.Single(b => b.OutcomeId == o.Id);
            return new PlanningValidationResult("outcome:" + o.Id, "supported", o.Description + " — " + o.Execution +
                (o.Coverage == "each_item" ? ", per item in " + binding.ForEachTaskId : "") +
                (o.Always == true ? ", always" : "") + (o.Conditional == true ? ", conditional" : "") +
                "; " + string.Join("; ", (binding.TaskIds.Count == 0 ? Array.Empty<string>() : new[] { "tasks: " + string.Join(", ", binding.TaskIds) })
                    .Concat(binding.Outputs.Count == 0 ? [] : new[] { "outputs: " + string.Join(", ", binding.Outputs) })) +
                ". Structural support only; external success has not been observed.", []);
        });
}
