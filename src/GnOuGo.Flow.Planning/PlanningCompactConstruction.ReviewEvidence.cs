using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static void AddTaskEvidence(PlanningSession state, JsonObject context, bool review)
    {
        var construction = state.Construction!;
        if (!IsCompactPresentation(state) || state.Plan is null) return;
        var fresh = PlanningConstructionPhasePolicies.IsFresh(state.Request);
        var draft = construction.Candidate;
        if (!fresh && (!review || draft is null)) return;
        var step = construction.Compact!.CurrentStep!;
        var accepted = new TaskPlanSymbols(state.Plan);
        var producers = (review && draft is not null ? draft.ImportedTaskIds : step.InputTaskIds.Concat(step.DependencyTaskIds))
            .Where(accepted.Tasks.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        // Neighbor descriptors locate prior work without granting repair authority.
        // Fresh v5 may also include a bounded complete original as separate evidence.
        var siblings = fresh ? accepted.Tasks.Values.Where(site => site.Scope.Path == step.Scope)
            .Select(site => site.Task.Id).ToArray() : [];
        var selected = producers.Take(8).Concat(siblings.Where(id => !producers.Contains(id, StringComparer.Ordinal))
            .TakeLast(Math.Min(4, Math.Max(0, 8 - producers.Length)))).ToArray();
        var owner = ScopeOwnerNavigation(state, accepted);
        if (selected.Length == 0 && owner is null) return;
        var count = producers.Concat(siblings).Distinct(StringComparer.Ordinal).Count();
        var navigation = new JsonObject
        {
            ["count"] = count,
            ["items"] = new JsonArray(selected.Select(id =>
            {
                var site = accepted.Tasks[id];
                var scopes = accepted.Scopes.Where(scope => scope.Owner?.Id == id).ToArray();
                var entry = fresh ? TrimNavigation(TaskNavigation(state, site.Task, site.Scope.Path)) : new JsonObject();
                entry["address"] = "task:" + id;
                entry["scopeCount"] = scopes.Length;
                entry["scopes"] = new JsonArray(scopes.Take(4).Select(scope => (JsonNode?)JsonValue.Create("scope:" + scope.Path)).ToArray());
                if (fresh) entry["phase"] = accepted.Phase(id);
                return (JsonNode)entry;
            }).ToArray())
        };
        if (fresh)
        {
            navigation["remaining"] = count - selected.Length;
            if (count > selected.Length) navigation["remainingEvidence"] = "page:tasks:0";
            if (siblings.Except(selected, StringComparer.Ordinal).Any()) navigation["scopeEvidence"] = "scope:" + step.Scope;
        }
        if (owner is not null)
            navigation["scopeOwner"] = owner;
        context["taskEvidence"] = navigation;
        AddAcceptedOperationEvidence(state, context, navigation["items"]!.AsArray().OfType<JsonObject>());
    }

    internal static void AddReviewGroupEvidence(PlanningSession state, JsonObject context)
    {
        if (!IsCompactPresentation(state)) return;
        var plan = state.Construction!.Candidate?.Plan ?? state.Plan;
        if (plan is null) return;
        var symbols = new TaskPlanSymbols(plan);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        Scope(context["draft"]);
        if (context["draft"]?["groups"] is JsonArray declarations)
            foreach (var group in declarations) Scope(group?["body"]);
        if (context["evidence"] is JsonObject evidence)
            foreach (var (address, value) in evidence)
            {
                if (address.StartsWith("task:", StringComparison.Ordinal)) Task(value);
                else if (address.StartsWith("scope:", StringComparison.Ordinal)) Scope(value);
                else if (address.StartsWith("page:tasks:", StringComparison.Ordinal)) Tasks(value?["items"]);
            }
        if (groups.Count == 0) return;
        context["groupEvidence"] = new JsonObject
        {
            ["count"] = groups.Count,
            ["items"] = new JsonArray(groups.Order(StringComparer.Ordinal).Take(8).Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["address"] = "scope:/groups/" + id + "/body"
            }).ToArray())
        };

        void Tasks(JsonNode? value)
        {
            if (value is JsonArray tasks) foreach (var task in tasks) Task(task);
        }
        void Scope(JsonNode? value)
        {
            if (value is not JsonObject scope) return;
            Tasks(scope["tasks"]); Tasks(scope["always"]);
        }
        void Task(JsonNode? value)
        {
            if (value is not JsonObject task) return;
            if (task["kind"]?.ToString() == "call" && task["group"] is JsonValue group &&
                symbols.Scopes.Any(scope => scope.Path == "/groups/" + group + "/body"))
                groups.Add(group.ToString());
            Scope(task["body"]); Scope(task["otherwise"]);
            if (task["branches"] is JsonArray branches) foreach (var branch in branches) Scope(branch);
        }
    }
}
