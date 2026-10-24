using GnOuGo.Flow.Core.Planning;

/// <summary>Supplementary authored expectations; never planner guidance or execution authority.</summary>
internal static class SyntheticArtifactOracle
{
    internal const string Version = "synthetic-artifact-v1";

    internal static IReadOnlyList<string> Validate(string scenario, TaskPlan? plan)
    {
        if (scenario is not ("local" or "collections")) return [];
        if (plan is null) return ["missing_taskplan"];
        var findings = new List<string>();
        var tasks = Walk(plan.Root).Concat(plan.Groups.SelectMany(g => Walk(g.Body))).ToArray();
        // This isolated host intentionally has no external effects or runtime LLM client.
        if (tasks.Any(t => t.Kind is "operation" or "transform"))
            findings.Add("unavailable_runtime_dependency");
        if (scenario != "collections") return findings;

        var contributing = ResultDependencies(plan, tasks);
        var loops = contributing.Where(t => t.Kind == "foreach").ToArray();
        if (!loops.Any(loop => loop.Parallel && loop.MaxConcurrency > 1 && loop.Body is not null &&
                Walk(loop.Body).Any(call => call.Kind == "call" && contributing.Contains(call) &&
                    plan.Groups.Any(group => group.Id == call.Group))))
            findings.Add("missing_parallel_reusable_item_calls");
        if (loops.Any(loop => !loop.Parallel || loop.MaxConcurrency <= 1))
            findings.Add("serial_item_iteration");
        return findings;
    }

    private static HashSet<PlanTask> ResultDependencies(TaskPlan plan, PlanTask[] tasks)
    {
        // Follow result data, not mere presence of an unused demonstration group/loop.
        var result = new HashSet<PlanTask>();
        var visited = new HashSet<(string Id, string? Port)>();
        foreach (var output in plan.Root.Outputs.Where(o => o.Name == "result")) VisitValue(output.Value);
        return result;

        void VisitValue(TaskValue value)
        {
            if (value.Kind == "output" && value.Source is { } source && visited.Add((source, value.Port)))
            {
                var matches = tasks.Where(t => t.Id == source).ToArray();
                if (matches.Length != 1) return; // Full compiler validation owns malformed identities.
                var task = matches[0];
                result.Add(task);
                foreach (var input in task.Inputs) VisitValue(input.Value);
                if (task.Items is not null) VisitValue(task.Items);
                VisitOutputs(task.Outputs, value.Port);
                if (task.Body is not null) VisitOutputs(task.Body.Outputs, value.Port);
                if (task.Otherwise is not null) VisitOutputs(task.Otherwise.Outputs, value.Port);
                foreach (var branch in task.Branches) VisitOutputs(branch.Outputs, value.Port);
                if (task.Kind == "call")
                    foreach (var group in plan.Groups.Where(g => g.Id == task.Group))
                        VisitOutputs(group.Body.Outputs, value.Port);
            }
            foreach (var item in value.Items) VisitValue(item);
            foreach (var member in value.Members) VisitValue(member.Value);
        }

        void VisitOutputs(IEnumerable<TaskOutput> outputs, string? port)
        {
            foreach (var output in outputs.Where(o => port is null || o.Name == port)) VisitValue(output.Value);
        }
    }

    private static IEnumerable<PlanTask> Walk(TaskScope scope)
    {
        foreach (var task in scope.Tasks.Concat(scope.Always))
        {
            yield return task;
            if (task.Body is not null) foreach (var child in Walk(task.Body)) yield return child;
            if (task.Otherwise is not null) foreach (var child in Walk(task.Otherwise)) yield return child;
            foreach (var branch in task.Branches)
                foreach (var child in Walk(branch)) yield return child;
        }
    }
}
