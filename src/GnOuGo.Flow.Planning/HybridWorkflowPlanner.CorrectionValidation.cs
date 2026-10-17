using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static IReadOnlyList<PlanningDiagnostic> ValidateAutomaticCorrection(PlanningSession state, TaskPlan candidate, bool verifiedPatch)
    {
        if (!AutomaticCorrection(state) || state.Request.Options[CorrectionProgressKey] is null || state.Request.Baseline is not { } baseline) return [];
        var progress = state.Request.Options[CorrectionProgressKey]!;
        var fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(candidate, PlanningJsonContext.Default.TaskPlan));
        progress["candidate"] = fingerprint;
        // A retained runtime failure can have a statically valid TaskPlan. Its
        // unchanged revision is not progress merely because compilation succeeds.
        if (state.Diagnostics.Any(d => d.Code == "RUNTIME_COMPOSITION_FAILURE") &&
            progress["attempts"]!.AsArray().Any(a => a?["candidate"]?.ToString() == fingerprint))
            return [new("REPLAN_NO_PROGRESS", "/", "The revision repeats the TaskPlan associated with the retained execution failure. Compilation alone does not resolve that failure; no candidate was applied.")];
        // Ordinary patches already passed the issued slot, producer-chain and host
        // ownership validators. Preserve their existing authority; additional checks
        // here constrain the newly policy-authorized revision paths/regeneration.
        if (verifiedPatch && state.EditablePaths is null) return [];
        var findings = new List<PlanningDiagnostic>();
        var before = new TaskPlanSymbols(baseline); var after = new TaskPlanSymbols(candidate);
        var commitmentRevision = state.IntentVersion == 3 && progress["version"]?.ToString() is "4" or "5";
        var additions = AuthorizedCommitmentAdditions(state, progress);
        var adaptations = commitmentRevision && progress["action"]?.ToString() == "regenerate" && progress["adaptationTasks"] is JsonArray tasks
            ? tasks.Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal) : [];
        var locations = state.Request.Options[CorrectionProgressKey]!["locations"]!.AsArray().Select(p => p!.ToString()).ToList();
        // Read-only producer/consumer context is not revision authority. Otherwise
        // a guard could retain its syntax while its observed producer becomes true.
        var affected = locations.Contains("/") ? before.Tasks.Keys.ToHashSet(StringComparer.Ordinal) :
            PlanningRepairContext.Select(new() { Plan = baseline, RevisionScope = locations }).EditableTasks;
        void RejectChange(string path, string responsibility) => findings.Add(new("REVISION_SCOPE_CHANGED", path,
            "Automatic correction cannot establish preservation of " + responsibility + ". Review this difference explicitly; no candidate was applied."));
        if (!PlanningClarifications.SameInputs(baseline.Inputs, candidate.Inputs)) RejectChange("/inputs", "the accepted caller interface");
        // A rejected proposal is not authority for extra public exports. Fresh
        // corrections may remove only the unaccepted names diagnosed before the
        // request was issued; historical progress keeps its exact comparison.
        var outputRemovals = progress["version"]?.ToString() is "3" or "4" or "5" && progress["outputRemovals"] is JsonArray removals && state.Requirements?.Outputs is { } accepted
            ? removals.Select(n => n!.ToString()).Where(name => accepted.All(o => o.Name != name)).ToHashSet(StringComparer.Ordinal) : [];
        var retainedOutputs = baseline.Root.Outputs.Where(o => !outputRemovals.Contains(o.Name) || candidate.Root.Outputs.Any(c => c.Name == o.Name)).ToList();
        if (!retainedOutputs.Select(o => o.Name).SequenceEqual(candidate.Root.Outputs.Select(o => o.Name), StringComparer.Ordinal))
            RejectChange("/root/outputs", "the public output names and ordering");
        if (outputRemovals.Count > 0 || commitmentRevision)
            foreach (var output in retainedOutputs)
                if (candidate.Root.Outputs.FirstOrDefault(o => o.Name == output.Name) is { } revised && !SameValue(output.Value, revised.Value) &&
                    !locations.Contains("/root/outputs/" + output.Name) && !locations.Contains("/outputs/" + output.Name))
                    RejectChange("/root/outputs/" + output.Name, "the independent public output binding");
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(baseline.Choices, PlanningJsonContext.Default.ListPlanningChoice),
                JsonSerializer.SerializeToNode(candidate.Choices, PlanningJsonContext.Default.ListPlanningChoice))) RejectChange("/choices", "declared choices and host selections");

        var operations = TaskPlanRevisions.Tasks(baseline).Where(t => t.Kind is "operation" or "call").ToArray();
        var ordered = operations.Select(t => t.Id).ToArray();
        var addedOperations = TaskPlanRevisions.Tasks(candidate).Where(t => t.Kind is "operation" or "call" && !ordered.Contains(t.Id, StringComparer.Ordinal)).ToArray();
        if (addedOperations.Any(t => t.Kind != "operation" || t.Operation is null || !additions.Contains(t.Operation) ||
                before.Tasks.ContainsKey(t.Id) && !adaptations.Contains(t.Id)) ||
            addedOperations.GroupBy(t => t.Operation, StringComparer.Ordinal).Any(g => g.Count() > 1))
            RejectChange("/tasks", "new external work or workflow calls not established by the baseline");
        if (commitmentRevision)
        {
            var needed = addedOperations.Select(t => t.Id).Concat(affected).Concat(adaptations).ToHashSet(StringComparer.Ordinal);
            foreach (var location in locations)
                if (after.Values.TryGetValue(location, out var value))
                    needed.UnionWith(TaskPlanCompiler.Values(value.Value).Where(v => v.Kind is "output" or "present" && v.Source is not null).Select(v => v.Source!));
            var queue = new Queue<string>(needed);
            while (queue.TryDequeue(out var id))
            {
                if (!after.Tasks.TryGetValue(id, out var site)) continue;
                foreach (var reference in TaskPlanCompiler.Values(site.Task).Where(v => v.Kind is "output" or "present" && v.Source is not null))
                    if (needed.Add(reference.Source!)) queue.Enqueue(reference.Source!);
                for (var scope = site.Scope; scope.Owner is not null; scope = scope.Parent!)
                    if (needed.Add(scope.Owner.Id)) queue.Enqueue(scope.Owner.Id);
            }
            foreach (var task in after.Tasks.Values.Select(t => t.Task).Where(t => !before.Tasks.ContainsKey(t.Id) && !needed.Contains(t.Id)))
                RejectChange("/tasks/" + task.Id, "new work outside the diagnosed adaptation and committed-operation inputs");
        }
        var revisedOrder = TaskPlanRevisions.Tasks(candidate).Where(t => ordered.Contains(t.Id, StringComparer.Ordinal)).Select(t => t.Id);
        if (!ordered.SequenceEqual(revisedOrder, StringComparer.Ordinal)) RejectChange("/tasks", "requested operation occurrences and relative order");
        var checkedOrder = TaskPlanRevisions.Tasks(baseline).Where(t => t.Requires is not null || t.Kind is "operation" or "call").Select(t => t.Id).ToArray();
        var revisedCheckedOrder = TaskPlanRevisions.Tasks(candidate).Where(t => checkedOrder.Contains(t.Id, StringComparer.Ordinal)).Select(t => t.Id);
        if (!checkedOrder.SequenceEqual(revisedCheckedOrder, StringComparer.Ordinal))
            RejectChange("/tasks", "ordered assertions relative to requested operations");
        foreach (var original in operations)
        {
            var path = "/tasks/" + original.Id;
            if (!after.Tasks.TryGetValue(original.Id, out var revised) || revised.Task.Kind != original.Kind ||
                revised.Task.Group != original.Group || revised.Task.Operation != original.Operation &&
                !(state.RevisionScope.Contains(path + "/operation") && state.Catalog!.Capabilities.All(c => TaskOperations.Describe(c).Id != original.Operation)))
            { RejectChange(path, "the requested operation"); continue; }
            if (before.Phase(original.Id) != after.Phase(original.Id)) RejectChange(path, "normal versus cleanup execution");
            if (!original.DependsOn.SequenceEqual(revised.Task.DependsOn, StringComparer.Ordinal))
                RejectChange(path + "/dependsOn", "the operation's scheduling dependencies");
            if (!Boundaries(before.Tasks[original.Id].Scope).SequenceEqual(Boundaries(revised.Scope), StringComparer.Ordinal))
                RejectChange(path, "the operation's branch, loop and scope boundaries");
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(original.Inputs, PlanningJsonContext.Default.ListTaskOutput),
                    JsonSerializer.SerializeToNode(revised.Task.Inputs, PlanningJsonContext.Default.ListTaskOutput)))
            {
                // Existing typed slots, not a full-plan replacement, authorize changes
                // to action arguments. Host-owned descendants remain protected there.
                var paths = state.EditablePaths ?? state.RevisionScope;
                var changes = TaskPlanRevisions.Validate(baseline, candidate, paths, state.Catalog, state.EditablePaths);
                if (changes.Any(d => d.Location == path || d.Location.StartsWith(path + "/inputs", StringComparison.Ordinal)))
                    RejectChange(path + "/inputs", "original action arguments");
            }
        }

        var sourcePlan = ClonePlan(baseline);
        foreach (var choice in sourcePlan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var directGuards = DirectGuardCorrections(progress);
        var conditionLocations = directGuards ? progress["conditionLocations"]!.AsArray().Select(p => p!.ToString()).ToHashSet(StringComparer.Ordinal) : [];
        // Require a real compiler diagnosis on the retained baseline, not merely a
        // model claim or a permissive editable path.
        if (directGuards) conditionLocations.IntersectWith(new TaskPlanCompiler().Compile(sourcePlan, state.Catalog!).Diagnostics
            .Where(d => d.Code == "TASK_CONDITION_TYPE").Select(d => d.Location));
        var sources = directGuards ? GuardSources(sourcePlan, state.Catalog!, candidate) : TaskPlanCompiler.RepairSources(sourcePlan, state.Catalog!);
        foreach (var (id, original) in before.Tasks)
        {
            if (!affected.Contains(id) && !adaptations.Contains(id))
            {
                if (!after.Tasks.TryGetValue(id, out var untouched)) RejectChange("/tasks/" + id, "the independent task declaration");
                else if (DeclarationDifference(original.Task, untouched.Task) is { } difference)
                    RejectChange("/tasks/" + id + (directGuards ? difference : ""), "the independent task declaration");
            }
            if (original.Task.ResultType is { } contract && after.Tasks.TryGetValue(id, out var adapted) && adapted.Task.ResultType is { } replacement &&
                !locations.Any(p => p == "/" || p.StartsWith("/tasks/" + id + "/", StringComparison.Ordinal)) &&
                !JsonNode.DeepEquals(TaskPlanCompiler.TypeSchema(replacement), TaskPlanCompiler.TypeSchema(contract)))
                RejectChange("/tasks/" + id + "/resultType", "the existing intermediate contract");
            if (original.Task.Requires is { } guard && (!after.Tasks.TryGetValue(id, out var revised) ||
                revised.Task.Requires is not { } next || !SameGuard(guard, next, "/tasks/" + id + "/requires")))
            {
                var path = "/tasks/" + id + "/requires";
                if (conditionLocations.Contains(path)) findings.Add(UnsafeGuard(path));
                else RejectChange(path, "the ordered assertion");
            }
            if (original.Task.Requires is not null && after.Tasks.TryGetValue(id, out var guarded) &&
                (before.Phase(id) != after.Phase(id) || !Boundaries(original.Scope).SequenceEqual(Boundaries(guarded.Scope), StringComparer.Ordinal) ||
                 !original.Task.DependsOn.SequenceEqual(guarded.Task.DependsOn, StringComparer.Ordinal)))
                RejectChange("/tasks/" + id + "/requires", "the assertion's execution boundary and scheduling dependencies");
            if (original.Task.Kind == "conditional" && (!after.Tasks.TryGetValue(id, out var conditional) ||
                !SameValue(original.Task.Condition, conditional.Task.Condition)))
                RejectChange("/tasks/" + id + "/condition", "the existing conditional routing");
            if (original.Task.Kind == "foreach" && (!after.Tasks.TryGetValue(id, out var loop) ||
                original.Task.MaxItems != loop.Task.MaxItems || !SameValue(original.Task.Items, loop.Task.Items)))
                RejectChange("/tasks/" + id + "/items", "the original collection and iteration bound");
        }
        if (commitmentRevision)
            foreach (var scope in before.Scopes)
            {
                if (scope.Path == "/root") continue;
                var replacement = after.Scopes.SingleOrDefault(s => s.Path == scope.Path);
                if (replacement is null || !scope.Source.Outputs.Select(o => o.Name).SequenceEqual(replacement.Source.Outputs.Select(o => o.Name), StringComparer.Ordinal))
                { RejectChange(scope.Path + "/outputs", "the independent scope exports"); continue; }
                foreach (var output in scope.Source.Outputs)
                    if (!locations.Contains(scope.Path + "/outputs/" + output.Name) &&
                        !SameValue(output.Value, replacement.Source.Outputs.Single(o => o.Name == output.Name).Value))
                        RejectChange(scope.Path + "/outputs/" + output.Name, "the independent scope export binding");
            }
        return findings;

        static IEnumerable<string> Boundaries(TaskPlanSymbols.Scope scope)
        {
            for (var current = scope; current is not null; current = current.Parent)
                yield return current.Path;
        }

        static string? DeclarationDifference(PlanTask old, PlanTask next)
        {
            var left = JsonSerializer.SerializeToNode(old, PlanningJsonContext.Default.PlanTask)!.AsObject();
            var right = JsonSerializer.SerializeToNode(next, PlanningJsonContext.Default.PlanTask)!.AsObject();
            // Child scopes are compared through their own identities and assertions.
            foreach (var name in new[] { "body", "otherwise", "branches" }) { left.Remove(name); right.Remove(name); }
            return Difference(left, right, "");
            static string? Difference(JsonNode? left, JsonNode? right, string path)
            {
                if (JsonNode.DeepEquals(left, right)) return null;
                if (left is JsonObject oldObject && right is JsonObject newObject)
                    foreach (var key in oldObject.Select(p => p.Key).Union(newObject.Select(p => p.Key), StringComparer.Ordinal))
                    {
                        var child = path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                        if (!oldObject.ContainsKey(key) || !newObject.ContainsKey(key)) return child;
                        if (Difference(oldObject[key], newObject[key], child) is { } change) return change;
                    }
                if (left is JsonArray oldArray && right is JsonArray newArray)
                {
                    for (var i = 0; i < Math.Min(oldArray.Count, newArray.Count); i++)
                        if (Difference(oldArray[i], newArray[i], path + "/" + i) is { } change) return change;
                    if (oldArray.Count != newArray.Count) return path + "/" + Math.Min(oldArray.Count, newArray.Count);
                }
                return path;
            }
        }

        bool SameGuard(TaskValue old, TaskValue next, string path, bool conditionOperand = true)
        {
            if (SameValue(old, next)) return true;
            var available = sources.GetValueOrDefault(path) ?? [];
            if (conditionOperand && conditionLocations.Contains(path) && NullableGuardReference(old, available))
                return NullableGuardReplacement(old, next, available);
            // A diagnosed missing precondition may follow the original assertion;
            // it cannot run earlier, replace it, or turn failure into skipping.
            if (!conditionLocations.Contains(path) && next.Kind == "predicate" && next.Predicate == "and" && next.Items.Count == 2 && SameGuard(old, next.Items[0], path)) return true;
            if (old.Kind is "output" or "choice" && next.Kind == "output" && old.Source == next.Source &&
                before.Tasks.TryGetValue(old.Source ?? "", out var producer) &&
                (old.Kind == "choice" && baseline.Choices.All(c => c.Id != old.Source) ||
                 old.Kind == "output" && old.Port is not null && !PlanningRepairContext.Ports(producer.Task, state).Contains(old.Port)) &&
                available.Any(v => SameValue(v.Value, next))) return true;
            if (old.Kind != next.Kind || old.Items.Count != next.Items.Count || old.Members.Count != next.Members.Count) return false;
            var left = JsonSerializer.SerializeToNode(old, PlanningJsonContext.Default.TaskValue)!.AsObject();
            var right = JsonSerializer.SerializeToNode(next, PlanningJsonContext.Default.TaskValue)!.AsObject();
            left.Remove("items"); right.Remove("items"); left.Remove("members"); right.Remove("members");
            if (old.Kind == "field" && old.Items.Count == 1 && SameValue(old.Items[0], next.Items[0]) && old.Port != next.Port)
            {
                var parent = available.FirstOrDefault(v => SameValue(v.Value, old.Items[0]))?.Schema;
                if (parent?["properties"] is not JsonObject properties || old.Port is null || properties.ContainsKey(old.Port) ||
                    next.Port is null || !properties.ContainsKey(next.Port)) return false;
                left.Remove("port"); right.Remove("port");
            }
            var booleanChildren = conditionOperand && old.Kind == "predicate" && old.Predicate is "and" or "or" or "not";
            return JsonNode.DeepEquals(left, right) && old.Items.Zip(next.Items).All(p => SameGuard(p.First, p.Second, path, booleanChildren)) &&
                old.Members.Zip(next.Members).All(p => p.First.Name == p.Second.Name && SameGuard(p.First.Value, p.Second.Value, path, false));
        }
    }

    private static bool SameValue(TaskValue? left, TaskValue? right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.TaskValue), JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.TaskValue));
}
