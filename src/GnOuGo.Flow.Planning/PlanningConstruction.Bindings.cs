using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static void AssembleBindings(PlanningSession state, PlanningConstructionExtension extension,
        List<PlanningConstructionCommitment>? proposed)
    {
        if (state.Construction?.Compact?.CurrentStep is not { } step ||
            state.Construction.Candidate?.AuthorizedPaths is not null || state.EditablePaths is not null) return;
        var newTasks = TaskPlanRevisions.Tasks(new TaskScope { Tasks = extension.Tasks, Always = extension.Always })
            .Concat(extension.Groups.SelectMany(group => TaskPlanRevisions.Tasks(group.Body)));
        foreach (var task in newTasks.Where(task => task.Kind == "operation"))
        {
            foreach (var group in task.Inputs.GroupBy(input => input.Name).Where(group => group.Count() > 1).ToArray())
            {
                var first = group.First();
                if (group.All(input => TaskPlanRevisions.Same(first.Value, input.Value)))
                    task.Inputs.RemoveAll(input => input.Name == first.Name && !ReferenceEquals(input, first));
            }
        }

        if (extension.Scope != "/root" || state.Requirements?.Outputs is not { } declarations) return;
        var roots = (state.Plan?.Root.Tasks ?? []).Concat(extension.Tasks).ToArray();
        var claims = (proposed ?? []).Where(claim => claim.Result is not null && step.RequirementIds.Contains(claim.RequirementId) &&
            step.OperationIds.Contains(claim.Operation)).GroupBy(claim => claim.Result!.Output);
        foreach (var claim in claims)
        {
            if (!step.OutputNames.Contains(claim.Key) || !declarations.Any(output => output.Name == claim.Key) ||
                (state.Plan?.Root.Outputs ?? []).Concat(extension.Outputs).Any(binding => binding.Name == claim.Key)) continue;
            var sources = claim.Select(item => (item.Operation, item.Result!.Port)).Distinct().ToArray();
            if (sources.Length != 1) continue;
            var producers = roots.Where(task => task.Kind == "operation" && task.Operation == sources[0].Operation).ToArray();
            if (producers.Length == 1 && extension.Tasks.Contains(producers[0]))
                extension.Outputs.Add(new(claim.Key, new() { Kind = "output", Source = producers[0].Id, Port = sources[0].Port }));
        }

        // A single explicit, direct public result fixes the new agent's payload
        // schema. A field projection or multiple consumers may need a wider shape.
        foreach (var task in extension.Tasks.Where(task => task.Kind == "operation" && task.Inputs.All(input => input.Name != "output_schema")))
        {
            var contracts = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
                .Where(capability => TaskOperations.Describe(capability).Id == task.Operation)
                .DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
            if (contracts is not [{ StepType: "agent.run" }]) continue;
            var bindings = extension.Outputs.Where(output => WholeAgentOutput(output.Value, task.Id, roots)).ToArray();
            if (bindings.Length != 1 || !step.OutputNames.Contains(bindings[0].Name)) continue;
            var declared = declarations.Where(output => output.Name == bindings[0].Name).ToArray();
            if (declared is [{ Type.Kind: "object" }])
                task.Inputs.Add(new("output_schema", SchemaLiteral(TaskPlanCompiler.TypeSchema(declared[0].Type, state.Request))));
        }
    }

    private static bool WholeAgentOutput(TaskValue value, string producer, PlanTask[] roots)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (value is { Kind: "output", Source: { } source, Port: { } port } && visited.Add(source))
        {
            if (source == producer) return port == "output";
            var aliases = roots.Where(task => task.Id == source && task.Kind == "value")
                .SelectMany(task => task.Outputs.Where(binding => binding.Name == port)).ToArray();
            if (aliases.Length != 1) return false;
            value = aliases[0].Value;
        }
        return false;
    }

    private static TaskValue SchemaLiteral(JsonNode? value) => value switch
    {
        JsonObject obj => new() { Kind = "object", Members = obj.Select(pair => new TaskOutput(pair.Key, SchemaLiteral(pair.Value))).ToList() },
        JsonArray items => new() { Kind = "array", Items = items.Select(SchemaLiteral).ToList() },
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => new() { Kind = "string", Text = text },
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => new() { Kind = "boolean", Boolean = flag },
        JsonValue scalar when scalar.TryGetValue<int>(out var count) => new() { Kind = "number", Number = count },
        null => new() { Kind = "null" },
        _ => throw new ArgumentException("Unsupported public type schema literal.")
    };
}
