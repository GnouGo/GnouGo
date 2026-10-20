using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static void AddReviewGroupEvidence(PlanningSession state, JsonObject context)
    {
        if (state.Construction?.Compact?.PresentationVersion != 2) return;
        var plan = state.Construction.Candidate?.Plan ?? state.Plan;
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
