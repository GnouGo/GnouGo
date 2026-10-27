using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

/// <summary>Supplementary authored expectations; never planner guidance or execution authority.</summary>
internal static class SyntheticArtifactOracle
{
    internal const string Version = "synthetic-artifact-v1";
    internal static string VersionFor(string scenario) => PlanningBenchmarkCases.ProofCases.Contains(scenario, StringComparer.Ordinal)
        ? "generic-proof-artifact-v1" : Version;

    internal static IReadOnlyList<string> Validate(string scenario, TaskPlan? plan, PlanningCatalog? catalog = null)
    {
        if (PlanningBenchmarkCases.ProofCases.Contains(scenario, StringComparer.Ordinal)) return ValidateProof(scenario, plan, catalog);
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

    private static IReadOnlyList<string> ValidateProof(string scenario, TaskPlan? plan, PlanningCatalog? catalog)
    {
        if (plan is null) return ["missing_taskplan"];
        var findings = new List<string>();
        var tasks = Walk(plan.Root).Concat(plan.Groups.SelectMany(g => Walk(g.Body))).ToArray();
        var inputs = plan.Inputs;
        if (inputs.Count != 2 || new[] { "left", "right" }.Any(name => !inputs.Any(i => i.Name == name && i.Required &&
                i.Default is null && i.Type.Kind == "number" && !i.Type.Nullable))) findings.Add("wrong_numeric_inputs");
        var outputNames = scenario == PlanningBenchmarkCases.TaskProofCase ? new[] { "doubled", "shifted" } : ["leftResult", "rightResult"];
        if (plan.Root.Outputs.Count != 2 || outputNames.Any(name => plan.Root.Outputs.Count(o => o.Name == name) != 1))
            findings.Add("wrong_public_outputs");
        if (tasks.Any(t => t.Kind == "transform")) findings.Add("unavailable_runtime_dependency");
        var left = ResultDependencies(plan, tasks, outputNames[0]);
        var right = ResultDependencies(plan, tasks, outputNames[1]);
        if (scenario == PlanningBenchmarkCases.TaskProofCase)
        {
            if (tasks.Any(t => t.Kind == "operation")) findings.Add("unavailable_runtime_dependency");
            // A shared nonliteral intermediate must actually feed two different
            // result-producing computations; two independent inlined expressions
            // or an unused demonstration intermediate do not satisfy this case.
            var shared = left.Intersect(right).Where(t => t.Kind == "value" &&
                TaskValues(t).Any(v => ReferencesInput(v, "left")) && TaskValues(t).Any(v => ReferencesInput(v, "right"))).ToArray();
            if (!shared.Any(producer => left.Any(a => a != producer && a.Kind == "value" && TaskValues(a).Any(v => References(v, producer.Id)) &&
                    right.Any(b => b != producer && b != a && b.Kind == "value" && TaskValues(b).Any(v => References(v, producer.Id))))))
                findings.Add("missing_shared_intermediate_consumers");
            return findings;
        }
        var operations = tasks.Where(t => t.Kind == "operation").ToArray();
        var read = operations.Length == 1 ? operations[0] : null;
        if (read is null || plan.Root.Tasks.FirstOrDefault() != read || read.Requires is not null || read.Condition is not null || read.DependsOn.Count != 0 ||
            read.Inputs.Count != 2 || !read.Inputs.Any(i => i.Name == "commandName" && i.Value.Kind == "string" && i.Value.Text == "cat_file") ||
            !read.Inputs.Any(i => i.Name == "parameters" && i.Value.Kind == "object" && i.Value.Members.Count == 1 &&
                i.Value.Members[0] is { Name: "path", Value.Kind: "string", Value.Text: "seed.txt" }))
            findings.Add("missing_exact_sequential_seed_read");
        if (read is not null)
        {
            var matches = (catalog?.Capabilities ?? []).Where(c => TaskOperations.Describe(c).Id == read.Operation).ToArray();
            if (matches.Length != 1 || matches[0].Server != "filesystem" || matches[0].Method != "cmd_run")
                findings.Add("unverified_seed_read_contract");
            if (tasks.Where(t => t != read).Any(t => TaskValues(t).Any(v => References(v, read.Id))) ||
                plan.Root.Outputs.Any(o => References(o.Value, read.Id)) ||
                plan.Groups.Any(g => g.Body.Outputs.Any(o => References(o.Value, read.Id))))
                findings.Add("opaque_read_consumed");
        }
        if (!left.Any(a => a.Kind == "call" && right.Any(b => b.Kind == "call" && b != a && b.Group == a.Group) &&
                plan.Groups.Any(g => g.Id == a.Group && g.Inputs.Count == 1 && g.Inputs[0] is { Required: true, Default: null, Type.Kind: "number", Type.Nullable: false })))
            findings.Add("missing_contributing_parameterized_calls");
        return findings;
    }

    private static IEnumerable<TaskValue> TaskValues(PlanTask task) => task.Inputs.Concat(task.Outputs).Select(o => o.Value)
        .Concat(new[] { task.Requires, task.Condition, task.Items }.OfType<TaskValue>())
        .Concat(new[] { task.Body, task.Otherwise }.OfType<TaskScope>().Concat(task.Branches).SelectMany(s => s.Outputs.Select(o => o.Value)));
    private static bool References(TaskValue value, string task) => value.Kind == "output" && value.Source == task ||
        value.Items.Any(v => References(v, task)) || value.Members.Any(m => References(m.Value, task));
    private static bool ReferencesInput(TaskValue value, string input) => value.Kind == "input" && value.Source == input ||
        value.Items.Any(v => ReferencesInput(v, input)) || value.Members.Any(m => ReferencesInput(m.Value, input));

    private static HashSet<PlanTask> ResultDependencies(TaskPlan plan, PlanTask[] tasks, string outputName = "result")
    {
        // Follow result data, not mere presence of an unused demonstration group/loop.
        var result = new HashSet<PlanTask>();
        var visited = new HashSet<(string Id, string? Port)>();
        foreach (var output in plan.Root.Outputs.Where(o => o.Name == outputName)) VisitValue(output.Value);
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
