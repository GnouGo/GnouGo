using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static JsonArray CommitmentAdditions(PlanningSession state)
    {
        var additions = new JsonArray();
        if (state.IntentVersion != 3 || state.Plan is null) return additions;
        // Recompute obligations from the retained plan, not from model-authored diagnostics.
        foreach (var operation in PlanningCommitments.Assess(state).SelectMany(r => r.Diagnostics)
            .Where(d => d.Code == PlanningCommitments.MissingOperation && state.Diagnostics.Any(issued => issued == d))
            .Select(d => d.Rule!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (PlanningCommitments.Contract(state, operation) is not { } contract ||
                state.Catalog!.Policy.DeniedCapabilityIds.Contains(contract.Id) || !state.Catalog.AllowedStepTypes.Contains(contract.StepType)) continue;
            // An uncalled existing occurrence needs a scoped placement decision, not another duplicate effect.
            if (TaskPlanRevisions.Tasks(state.Plan).Any(t => t.Kind == "operation" && t.Operation == operation)) continue;
            additions.Add((JsonNode)new JsonObject { ["operation"] = operation, ["capability"] = contract.Id,
                ["version"] = contract.Version, ["contract"] = ContractFingerprint(contract) });
        }
        return additions;
    }

    private static string ContractFingerprint(PlanningCapability capability) =>
        PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));

    private static HashSet<string> AuthorizedCommitmentAdditions(PlanningSession state, JsonNode progress)
    {
        if (state.IntentVersion != 3 || progress["version"]?.ToString() != "4" || progress["action"]?.ToString() != "regenerate" ||
            progress["operationAdditions"] is not JsonArray additions || state.Request.Baseline is not { } baseline) return [];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addition in additions.OfType<JsonObject>())
        {
            var id = addition["operation"]?.ToString();
            if (id is null || !(state.Requirements?.Outcomes ?? []).SelectMany(r => r.Commitments ?? []).Any(c => c.Operation == id) ||
                TaskPlanRevisions.Tasks(baseline).Any(t => t.Kind == "operation" && t.Operation == id)) continue;
            var contract = PlanningCommitments.Contract(state, id);
            if (contract is null || contract.Id != addition["capability"]?.ToString() || contract.Version != addition["version"]?.ToString() ||
                ContractFingerprint(contract) != addition["contract"]?.ToString() || state.Catalog!.Policy.DeniedCapabilityIds.Contains(contract.Id) ||
                !state.Catalog.AllowedStepTypes.Contains(contract.StepType)) continue;
            result.Add(id);
        }
        return result;
    }

    // Only exclusive pure producers of a disconnected committed export. Context
    // visibility alone never makes arbitrary producers or their consumers editable.
    private static JsonArray CommitmentAdaptations(PlanningSession state)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (state.IntentVersion != 3 || state.Plan is null) return [];
        var symbols = new TaskPlanSymbols(state.Plan);
        foreach (var finding in PlanningCommitments.Assess(state).SelectMany(r => r.Diagnostics)
            .Where(d => d.Code == PlanningCommitments.DisconnectedResult && state.Diagnostics.Contains(d))) paths.Add(finding.Location);
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var path in paths.ToArray())
            {
                if (!symbols.Values.TryGetValue(path, out var site)) continue;
                foreach (var reference in TaskPlanCompiler.Values(site.Value).Where(v => v.Kind == "output" && v.Source is not null))
                {
                    if (!symbols.Tasks.TryGetValue(reference.Source!, out var producer) || producer.Task.Kind is not ("value" or "transform") ||
                        producer.Task.Requires is not null || tasks.Contains(producer.Task.Id)) continue;
                    var uses = symbols.Values.Where(p => TaskPlanCompiler.Values(p.Value.Value).Any(v => v.Source == producer.Task.Id && v.Kind is "output" or "present")).Select(p => p.Key);
                    if (uses.Any(use => !paths.Contains(use)) || symbols.Tasks.Values.Any(t => t.Task.DependsOn.Contains(producer.Task.Id))) continue;
                    tasks.Add(producer.Task.Id); changed = true;
                    foreach (var output in producer.Task.Outputs) paths.Add("/tasks/" + producer.Task.Id + "/outputs/" + output.Name);
                }
            }
        } while (changed);
        return new JsonArray(tasks.Order(StringComparer.Ordinal).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
    }
}
