using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static IEnumerable<PlanningDiagnostic> RebuildFindings(PlanningSession state, TaskPlan candidate, bool complete)
    {
        var symbols = new TaskPlanSymbols(candidate);
        var tasks = symbols.Tasks.ToDictionary(t => t.Key, t => t.Value.Task, StringComparer.Ordinal);
        foreach (var original in state.Construction!.RebuildTasks)
        {
            if (!tasks.TryGetValue(original.Id, out var restored))
            {
                if (complete) yield return new("CONSTRUCTION_REBUILD_REMAINING", "/tasks/" + original.Id, "Reconstruct this invalidated task before completing the workflow.");
                continue;
            }
            var preserved = restored.Objective == original.Objective && restored.Kind == original.Kind && restored.Operation == original.Operation &&
                restored.Mode == original.Mode && Equals(restored.Each, original.Each) && restored.Group == original.Group && restored.Parallel == original.Parallel &&
                restored.MaxItems == original.MaxItems && restored.MaxConcurrency == original.MaxConcurrency &&
                original.DependsOn.SequenceEqual(restored.DependsOn, StringComparer.Ordinal) &&
                JsonNode.DeepEquals(Node(original.Items, PlanningJsonContext.Default.TaskValue), Node(restored.Items, PlanningJsonContext.Default.TaskValue)) &&
                JsonNode.DeepEquals(Node(original.Requires, PlanningJsonContext.Default.TaskValue), Node(restored.Requires, PlanningJsonContext.Default.TaskValue)) &&
                JsonNode.DeepEquals(Node(original.Condition, PlanningJsonContext.Default.TaskValue), Node(restored.Condition, PlanningJsonContext.Default.TaskValue)) &&
                original.Inputs.Select(i => i.Name).SequenceEqual(restored.Inputs.Select(i => i.Name)) &&
                original.Inputs.Zip(restored.Inputs).All(pair => JsonNode.DeepEquals(Literals(pair.First.Value), Literals(pair.Second.Value))) &&
                original.Outputs.Select(i => i.Name).SequenceEqual(restored.Outputs.Select(i => i.Name)) &&
                original.Outputs.Zip(restored.Outputs).All(pair => JsonNode.DeepEquals(Literals(pair.First.Value), Literals(pair.Second.Value)));
            if (!preserved) yield return new("REVISION_SCOPE_CHANGED", "/tasks/" + original.Id,
                "Rollback preserves original objectives, effects, literal arguments, ordered assertions, decisions and iteration limits. Their change requires an explicit implementation or intent revision.");
            var placement = state.Construction.RebuildScopes.SingleOrDefault(s => s.Tasks.Contains(original.Id, StringComparer.Ordinal) || s.Always.Contains(original.Id, StringComparer.Ordinal));
            if (placement is null || symbols.Tasks[original.Id].Scope.Path != placement.Path ||
                symbols.Phase(original.Id) != (placement.Always.Contains(original.Id, StringComparer.Ordinal) ? "always" : "tasks"))
                yield return new("REVISION_SCOPE_CHANGED", "/tasks/" + original.Id, "Rebuilt work must retain its original lexical scope and normal versus cleanup phase.");
        }
        foreach (var original in state.Construction.RebuildScopes)
        {
            var scope = symbols.Scopes.SingleOrDefault(s => s.Path == original.Path);
            if (scope is null) continue;
            if (!PreservesOrder(original.Tasks, scope.Source.Tasks) || !PreservesOrder(original.Always, scope.Source.Always))
                yield return new("REVISION_SCOPE_CHANGED", original.Path, "Rebuilt work must retain its original order relative to independent work.");
            if ((complete || state.Construction.Candidate?.OpenScopes.Contains(original.Path, StringComparer.Ordinal) == false) &&
                !original.Outputs.Select(o => o.Name).SequenceEqual(scope.Source.Outputs.Take(original.Outputs.Count).Select(o => o.Name), StringComparer.Ordinal))
                yield return new("REVISION_SCOPE_CHANGED", original.Path + "/outputs", "Rebuilt scopes must retain their original exported interface names and order.");
        }

        static bool PreservesOrder(List<string> order, List<PlanTask> tasks) => order.Where(id => tasks.Any(t => t.Id == id))
            .SequenceEqual(tasks.Where(t => order.Contains(t.Id, StringComparer.Ordinal)).Select(t => t.Id), StringComparer.Ordinal);

        static JsonNode? Literals(TaskValue value) => value.Kind switch
        {
            "null" or "string" or "boolean" or "number" => Node(value, PlanningJsonContext.Default.TaskValue),
            "object" => new JsonObject(value.Members.Select(m => new KeyValuePair<string, JsonNode?>(m.Name, Literals(m.Value)))),
            "array" => new JsonArray(value.Items.Select(Literals).ToArray()),
            _ => new JsonObject { ["reference"] = true }
        };
    }

    internal static void RestoreRebuildPositions(PlanningSession state, TaskPlan plan, IReadOnlySet<string> added)
    {
        var construction = state.Construction!;
        var symbols = new TaskPlanSymbols(plan);
        foreach (var id in construction.RebuildTasks.Select(t => t.Id).Where(added.Contains))
        {
            var placement = construction.RebuildScopes.SingleOrDefault(s => s.Tasks.Contains(id, StringComparer.Ordinal) || s.Always.Contains(id, StringComparer.Ordinal));
            if (placement is null || !symbols.Tasks.TryGetValue(id, out var site) || site.Scope.Path != placement.Path ||
                symbols.Phase(id) != (placement.Always.Contains(id, StringComparer.Ordinal) ? "always" : "tasks"))
                Fail("REVISION_SCOPE_CHANGED", "/tasks/" + id, "Rebuild this original task in its original scope and cleanup phase.");
        }
        foreach (var placement in construction.RebuildScopes)
        {
            var scope = symbols.Scopes.SingleOrDefault(s => s.Path == placement.Path)?.Source;
            if (scope is null) continue;
            Restore(scope.Tasks, placement.Tasks); Restore(scope.Always, placement.Always);
        }
        void Restore(List<PlanTask> tasks, List<string> order)
        {
            foreach (var id in order.Where(added.Contains))
            {
                var task = tasks.SingleOrDefault(t => t.Id == id); if (task is null) continue;
                tasks.Remove(task); var position = order.IndexOf(id);
                var next = tasks.FindIndex(t => order.IndexOf(t.Id) > position);
                if (next >= 0) tasks.Insert(next, task);
                else
                {
                    var previous = tasks.FindLastIndex(t => order.IndexOf(t.Id) is var index && index >= 0 && index < position);
                    tasks.Insert(previous + 1, task);
                }
            }
        }
    }

    internal static void Rollback(PlanningSession state, string taskId, string? reason)
    {
        var construction = state.Construction!;
        if (state.PendingCall is not null || construction.Candidate is not null || state.ApprovedHash is not null ||
            state.Plan is null || string.IsNullOrWhiteSpace(reason) || reason.Length > 2048)
            Fail("CONSTRUCTION_ROLLBACK_SCOPE", "/rollbackTask", "Rollback requires an unapproved accepted plan, a concrete reason and no pending candidate or model request.");
        var originalSnapshot = new TaskPlanSymbols(Clone(state.Plan!));
        var plan = Clone(state.Plan!); var symbols = new TaskPlanSymbols(plan);
        if (construction.RebuildTasks.Any(t => !symbols.Tasks.ContainsKey(t.Id)))
            Fail("CONSTRUCTION_ROLLBACK_UNPROVEN", "/rollbackTask", "Complete the retained original rebuild obligations before requesting another rollback region.");
        if (!symbols.Tasks.ContainsKey(taskId)) Fail("CONSTRUCTION_ROLLBACK_SCOPE", "/rollbackTask", "Choose an existing accepted task identity.");
        var invalid = new HashSet<string>(StringComparer.Ordinal); var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var open = construction.OpenScopes.ToHashSet(StringComparer.Ordinal);
        void Invalidate(string id)
        {
            var task = symbols.Tasks[id].Task;
            foreach (var nested in TaskPlanRevisions.Tasks(new TaskScope { Tasks = [task] }))
            { invalid.Add(nested.Id); unavailable.Add(nested.Id); }
            for (var scope = symbols.Tasks[id].Scope; scope is not null; scope = scope.Parent)
                if (scope.Owner is not null) unavailable.Add(scope.Owner.Id);
        }
        Invalidate(taskId);
        bool changed;
        do
        {
            var count = invalid.Count + unavailable.Count;
            foreach (var scope in symbols.Scopes)
                if (scope.Owner is not null && scope.Source.Outputs.SelectMany(o => TaskPlanCompiler.Values(o.Value))
                    .Any(v => v.Kind is "output" or "present" && v.Source is not null && unavailable.Contains(v.Source)))
                    unavailable.Add(scope.Owner.Id);
            foreach (var (task, _) in symbols.Tasks.Values.Where(t => !invalid.Contains(t.Task.Id)))
            {
                var group = task.Kind == "call" ? plan.Groups.SingleOrDefault(g => g.Id == task.Group) : null;
                if (References(task).Any(unavailable.Contains) || group is not null && TaskPlanRevisions.Tasks(group.Body).Any(t => invalid.Contains(t.Id)))
                    Invalidate(task.Id);
            }
            changed = count != invalid.Count + unavailable.Count;
        } while (changed);
        foreach (var id in invalid)
            for (var scope = symbols.Tasks[id].Scope; scope is not null; scope = scope.Parent) open.Add(scope.Path);
        var placements = symbols.Scopes.Where(s => s.Source.Tasks.Concat(s.Source.Always).Any(t => invalid.Contains(t.Id)))
            .Select(s => new PlanningConstructionRebuildScope { Path = s.Path, Tasks = s.Source.Tasks.Select(t => t.Id).ToList(), Always = s.Source.Always.Select(t => t.Id).ToList(), Outputs = s.Source.Outputs.ToList() }).ToArray();
        foreach (var scope in symbols.Scopes)
        {
            scope.Source.Tasks.RemoveAll(t => invalid.Contains(t.Id));
            scope.Source.Always.RemoveAll(t => invalid.Contains(t.Id));
            if (open.Contains(scope.Path)) scope.Source.Outputs.RemoveAll(o => TaskPlanCompiler.Values(o.Value)
                .Any(v => v.Kind is "output" or "present" && v.Source is not null && unavailable.Contains(v.Source)));
        }
        var after = new TaskPlanSymbols(plan);
        open.RemoveWhere(p => after.Scopes.All(s => s.Path != p));
        foreach (var (id, original) in symbols.Tasks.Where(p => !invalid.Contains(p.Key)))
            if (!after.Tasks.TryGetValue(id, out var retained) || retained.Scope.Path != original.Scope.Path ||
                !JsonNode.DeepEquals(TaskNode(original.Task), TaskNode(retained.Task)))
                Fail("CONSTRUCTION_ROLLBACK_UNPROVEN", "/tasks/" + id, "Preservation of an independent fragment could not be established.");
        // A removed producer may leave no future promise in concrete bindings.
        var probe = Clone(plan);
        foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var diagnostics = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request, open).Diagnostics;
        // Choices whose sole use was invalidated remain accepted decisions and
        // cannot be silently deleted. Such rollback needs an explicit revision.
        if (diagnostics.Count > 0) Fail("CONSTRUCTION_ROLLBACK_UNPROVEN", "/rollbackTask", "The dependent region cannot be removed while preserving concrete contracts and accepted decisions.");
        var key = PlanningGraphCompiler.Fingerprint(Hash(state.Plan) + "\n" + string.Join('\n', invalid.Order(StringComparer.Ordinal)));
        if (construction.FailedCandidates.Contains(key, StringComparer.Ordinal)) Fail("REPLAN_NO_PROGRESS", "/rollbackTask", "This rollback region was already attempted without progress.");
        construction.FailedCandidates.Add(key);
        construction.Invalidations.Add(new()
        {
            Sequence = construction.Invalidations.Count + 1, BeforeHash = Hash(state.Plan), AfterHash = Hash(plan), Reason = reason!,
            Tasks = invalid.Order(StringComparer.Ordinal).Select(id => originalSnapshot.Tasks[id].Task).ToList(), Scopes = placements.ToList()
        });
        foreach (var placement in placements)
        {
            construction.RebuildScopes.RemoveAll(s => s.Path == placement.Path);
            construction.RebuildScopes.Add(placement);
        }
        foreach (var checkpoint in construction.Checkpoints)
        {
            var removed = (construction.Version == 2 ? checkpoint.TaskIds.Concat(checkpoint.ImportedTaskIds) : checkpoint.TaskIds).Where(invalid.Contains).ToArray();
            if (removed.Length == 0) continue;
            checkpoint.InvalidatedTaskIds = checkpoint.InvalidatedTaskIds.Concat(removed).Distinct(StringComparer.Ordinal).ToList();
            foreach (var requirement in construction.Requirements.Where(r => checkpoint.RequirementIds.Contains(r.Id, StringComparer.Ordinal))) requirement.Status = "remaining";
        }
        if (construction.Version == 2)
        {
            var reopened = construction.Requirements.Where(r => r.Status == "remaining").Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var checkpoint in construction.Checkpoints.Where(c => c.ClosedRequirementIds?.Any(reopened.Contains) == true))
                checkpoint.InvalidatedTaskIds = checkpoint.InvalidatedTaskIds.Concat(invalid).Distinct(StringComparer.Ordinal).ToList();
        }
        foreach (var id in invalid)
            if (!construction.RebuildTasks.Any(t => t.Id == id)) construction.RebuildTasks.Add(originalSnapshot.Tasks[id].Task);
        state.Plan = plan; construction.OpenScopes = open.OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList();
        construction.Complete = false; construction.CandidateFailureIdentityVersion = null;
        construction.Evidence = null; construction.EvidenceRequests.Clear();
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        if (construction.Compact is { } compact)
        { compact.CurrentStep = null; compact.PendingCommitments.Clear(); compact.ReplacementScope = null; compact.ReplacementRequirements = null; }
        state.Graph = null; state.Yaml = null; state.ApprovedHash = null; state.ApprovalReview = null; state.ValidationResults.Clear(); state.Diagnostics.Clear();
    }
}
