using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static void CompactAuthoringNavigation(JsonObject context, bool compactCheckpoints = false)
    {
        // Actual operands, producer tasks/contracts, ancestor controls, originals
        // and explicitly retrieved evidence remain complete. Only optional
        // navigation descriptions move behind their existing exact addresses.
        if (context["taskEvidence"]?["items"] is JsonArray tasks) CompactTasks(tasks);
        foreach (var requirement in (context["requirements"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (requirement["acceptedProgress"] is JsonObject progress && progress["checkpointCount"] is JsonValue count &&
                count.TryGetValue<int>(out var total) && total == 0 && progress["items"] is JsonArray { Count: 0 })
            {
                requirement.Remove("acceptedProgress");
                continue;
            }
            foreach (var checkpoint in (requirement["acceptedProgress"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (compactCheckpoints && checkpoint["sequence"] is JsonValue sequence && sequence.TryGetValue<int>(out var id) && id > 0 &&
                    checkpoint["evidence"]?.ToString() is { } address && address.StartsWith("page:checkpoints:", StringComparison.Ordinal))
                {
                    // Actual consumed interfaces and original obligations are
                    // already present. Prior checkpoints are navigation here.
                    foreach (var key in checkpoint.Select(pair => pair.Key).Except(["sequence", "evidence"], StringComparer.Ordinal).ToArray())
                        checkpoint.Remove(key);
                }
                else if (checkpoint["tasks"] is JsonArray contributions) CompactTasks(contributions);
            }
        }
        if (context["companionOperations"]?["items"] is JsonArray operations)
            foreach (var operation in operations.OfType<JsonObject>())
            {
                if (operation.Remove("description"))
                {
                    operation.Remove("descriptionComplete"); operation["descriptionOmitted"] = true;
                }
            }
        static void CompactTasks(JsonArray rows)
        {
            foreach (var task in rows.OfType<JsonObject>())
            {
                if (task["task"]?.ToString() is not { } id ||
                    task["evidence"]?.ToString() != "task:" + id && task["address"]?.ToString() != "task:" + id) continue;
                var removed = task.Select(pair => pair.Key).Except(
                    ["task", "address", "evidence", "directory", "scope", "phase", "kind", "operation", "group", "scopes", "scopeCount", "nextPorts", "objectiveEvidence", "detailsOmitted",
                     "openResultScopeCount", "portCount", "ports"], StringComparer.Ordinal).ToArray();
                foreach (var key in removed) task.Remove(key);
                if (removed.Length > 0) task["detailsOmitted"] = true;
            }
        }
    }

    private const int ContinuedScopePreviewBytes = 8192;

    private static bool CompleteScopePage(JsonObject scope) =>
        scope["tasks"]!.AsArray().Count == scope["taskCount"]!.GetValue<int>() &&
        scope["always"]!.AsArray().Count == scope["alwaysCount"]!.GetValue<int>();

    private static void AddContinuedChildScopes(PlanningSession state, JsonObject context, PlanningConstructionCheckpoint checkpoint, string parentPath)
    {
        if (state.Plan is not { } plan) return;
        var symbols = new TaskPlanSymbols(plan);
        var parent = symbols.Scopes.Single(scope => scope.Path == parentPath);
        var bytes = 0;
        foreach (var path in checkpoint.ClosedScopes.Take(3))
        {
            var address = "scope:" + path;
            var site = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
            if (site?.Owner is not { } owner || !checkpoint.TaskIds.Contains(owner.Id, StringComparer.Ordinal) ||
                site.Parent != parent && symbols.ExportRoute(parent, owner.Id).Count == 0 || context["evidence"]?[address] is not null) continue;
            var view = PlanningConstruction.ScopeEvidence(state, path);
            if (view?["open"]?.GetValue<bool>() != false || !CompleteScopePage(view)) continue;
            var size = System.Text.Encoding.UTF8.GetByteCount(view.ToJsonString());
            if (size > ContinuedScopePreviewBytes - bytes) continue;
            (context["evidence"] ??= new JsonObject())[address] = view;
            bytes += size;
        }
    }

    private static void RetainInspectedChildScope(PlanningSession state, string parent, JsonObject inspected, JsonObject retained)
    {
        foreach (var (address, value) in inspected.Reverse())
        {
            if (!address.StartsWith("scope:", StringComparison.Ordinal) || value is not JsonObject scope ||
                scope["open"]?.GetValue<bool>() != false || scope["parent"]?.ToString() != parent ||
                scope["path"]?.ToString() is not { } path || address != "scope:" + path) continue;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(scope.ToJsonString());
            if (bytes > ContinuedScopePreviewBytes || !JsonNode.DeepEquals(scope, PlanningConstruction.ScopeEvidence(state, path))) continue;
            retained[address] = scope.DeepClone();
            foreach (var phase in new[] { "tasks", "always" })
                foreach (var declaration in scope[phase]!.AsArray().OfType<JsonObject>())
                {
                    var taskAddress = "task:" + declaration["id"]!.ToString();
                    if (inspected[taskAddress] is not JsonObject task || retained.ContainsKey(taskAddress)) continue;
                    var original = declaration.DeepClone().AsObject();
                    original["scope"] = path; original["phase"] = phase;
                    if (!JsonNode.DeepEquals(task, original)) continue;
                    var size = System.Text.Encoding.UTF8.GetByteCount(task.ToJsonString());
                    if (size > ContinuedScopePreviewBytes - bytes) continue;
                    retained[taskAddress] = task.DeepClone();
                    bytes += size;
                }
            break;
        }
    }

    private static void AddTaskEvidence(PlanningSession state, JsonObject context, bool useCandidate, PlanningConstructionCheckpoint? continued)
    {
        var construction = state.Construction!;
        if (state.Plan is null) return;
        var draft = construction.Candidate;
        var step = construction.Compact!.CurrentStep!;
        var accepted = new TaskPlanSymbols(state.Plan);
        var producers = (useCandidate && draft is not null ? draft.ImportedTaskIds : step.InputTaskIds.Concat(step.DependencyTaskIds))
            .Where(accepted.Tasks.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        // Neighbor descriptors locate prior work without granting repair authority.
        // A bounded complete original remains available as separate evidence.
        var siblings = accepted.Tasks.Values.Where(site => site.Scope.Path == step.Scope)
            .Select(site => site.Task.Id).ToArray();
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
                var entry = TrimNavigation(TaskNavigation(state, site.Task, site.Scope.Path));
                entry["address"] = "task:" + id;
                entry["scopeCount"] = scopes.Length;
                entry["scopes"] = new JsonArray(scopes.Take(4).Select(scope => (JsonNode?)JsonValue.Create("scope:" + scope.Path)).ToArray());
                entry["phase"] = accepted.Phase(id);
                return (JsonNode)entry;
            }).ToArray())
        };
        {
            navigation["remaining"] = count - selected.Length;
            if (count > selected.Length)
            {
                navigation["remainingEvidence"] = "page:tasks:0";
                var directory = draft is null ? accepted : new TaskPlanSymbols(draft.Plan);
                var rows = navigation["items"]!.AsArray().OfType<JsonObject>().ToDictionary(row => row["task"]!.ToString(), StringComparer.Ordinal);
                const int pageSize = PlanningConstruction.TaskDirectoryPageSize;
                var pages = new JsonArray();
                foreach (var (page, number) in directory.Tasks.Values.Chunk(pageSize).Select((page, number) => (page, number)).Skip(1))
                {
                    var address = "page:tasks:" + (number * pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var linked = false;
                    foreach (var site in page)
                        if (rows.TryGetValue(site.Task.Id, out var row))
                        {
                            row["directory"] = address;
                            linked = true;
                        }
                    if (continued is null || linked || pages.Count >= pageSize || construction.Evidence?.ContainsKey(address) == true) continue;
                    var local = page.Where(site => site.Scope.Path == step.Scope).ToArray();
                    if (local.Length > 0)
                        pages.Add((JsonNode)new JsonObject { ["address"] = address, ["firstTask"] = local[0].Task.Id, ["lastTask"] = local[^1].Task.Id });
                }
                if (pages.Count > 0) navigation["directoryPages"] = pages;
            }
            if (siblings.Except(selected, StringComparer.Ordinal).Any()) navigation["scopeEvidence"] = "scope:" + step.Scope;
        }
        if (owner is not null)
            navigation["scopeOwner"] = owner;
        context["taskEvidence"] = navigation;
        AddAcceptedOperationEvidence(state, context, navigation["items"]!.AsArray().OfType<JsonObject>(), continued);
    }

    internal static void AddCandidateGroupEvidence(PlanningSession state, JsonObject context)
    {
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
            ["items"] = new JsonArray(groups.Order(StringComparer.Ordinal).Select(id => (JsonNode)new JsonObject
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
