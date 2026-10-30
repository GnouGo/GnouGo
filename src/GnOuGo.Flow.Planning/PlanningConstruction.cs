using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Presentation and atomic semantic edits; no executable representation or second type system.
internal static partial class PlanningConstruction
{
    internal const string DraftPurpose = "construction_draft", LocalPurpose = "construction_local_judge", GlobalPurpose = "construction_global_judge";
    internal static string Hash(TaskPlan? plan) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    internal static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    internal static JsonNode? Node<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.SerializeToNode(value, type);

    private static string[] IssuedScopes(PlanningSession state)
    {
        var construction = state.Construction!;
        if (construction.Compact?.CurrentStep is { } selected) return [selected.Scope];
        var scopes = construction.OpenScopes.Skip(construction.ScopeCursor).Take(8).ToArray();
        if (scopes.Length == 0) scopes = construction.OpenScopes.Take(8).ToArray();
        if (construction.Candidate is { } retained && !scopes.Contains(retained.Extension.Scope, StringComparer.Ordinal)) scopes = [retained.Extension.Scope];
        return scopes;
    }

    internal static JsonNode TaskNode(PlanTask task)
    {
        var node = Node(task, PlanningJsonContext.Default.PlanTask)!.AsObject();
        // Scope bodies are retrieved independently. The current bounded extension retains its own complete body.
        node.Remove("body"); node.Remove("otherwise"); node.Remove("branches");
        node["scopes"] = new JsonArray((task.Body is null ? [] : new[] { "/tasks/" + task.Id + "/body" })
            .Concat(task.Otherwise is null ? [] : ["/tasks/" + task.Id + "/otherwise"])
            .Concat(task.Branches.Select((_, i) => "/tasks/" + task.Id + "/branches/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        return PlanningJsonTransport.TaskPlanPart(node)!;
    }

    private static JsonObject ContractNode(PlanningCapability capability)
    {
        var operation = PlanningCapabilityArguments.Editable(capability);
        JsonArray Ports(IEnumerable<OperationPort> ports) => new(ports.Select(p => (JsonNode)new JsonObject
            { ["name"] = p.Name, ["type"] = p.Schema.DeepClone(), ["required"] = p.Required }).ToArray());
        return new() { ["id"] = operation.Id, ["version"] = operation.Version, ["description"] = operation.Description,
            ["effect"] = capability.EffectKind, ["inputs"] = Ports(operation.Inputs), ["outputs"] = Ports(operation.Outputs),
            ["artifacts"] = TaskOperations.ArtifactPorts(capability) };
    }

    internal static PlanningConstructionCandidate Apply(PlanningSession state, PlanningConstructionExtension extension)
    {
        var construction = state.Construction!;
        var issued = IssuedScopes(state).ToHashSet(StringComparer.Ordinal);
        if (!issued.Contains(extension.Scope)) Fail("CONSTRUCTION_SCOPE_INVALID", extension.Scope, "Choose an issued open scope.");
        // Host-derived closure must not mutate the received response or a retained
        // caller-owned fragment. The candidate owns its normalized edit envelope.
        if (construction.Compact?.CurrentStep is { } block && PlanningCompactConstruction.SupportsBusinessFragment(block.Kind))
            extension = JsonSerializer.Deserialize(JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                PlanningJsonContext.Default.PlanningConstructionExtension)!;
        var additions = TaskPlanRevisions.Tasks(new TaskScope { Tasks = extension.Tasks, Always = extension.Always })
            .Concat(extension.Groups.SelectMany(g => TaskPlanRevisions.Tasks(g.Body))).ToArray();
        if (additions.Length > construction.Limits.MaxTasksPerDraft)
            Fail("CONSTRUCTION_DRAFT_LIMIT", extension.Scope,
                $"The extension contains {additions.Length} recursive tasks; its captured maximum is {construction.Limits.MaxTasksPerDraft}, shared across nested bodies and cleanup.");
        if (extension.Groups.Count > construction.Limits.MaxTasksPerDraft || extension.Choices.Count > construction.Limits.MaxTasksPerDraft)
            Fail("CONSTRUCTION_DRAFT_LIMIT", extension.Scope,
                $"The extension contains {extension.Groups.Count} groups and {extension.Choices.Count} choices; the captured maximum is {construction.Limits.MaxTasksPerDraft} for each.");
        var closesRequirement = extension.ContinueInScope is null && construction.Compact?.CurrentStep is { } step &&
            step.CloseRequirementIds.Any(id => PlanningCompactConstruction.CanClaimRequirementCompletion(state, id));
        if (additions.Length == 0 && extension.Outputs.Count == 0 && extension.CloseScopes.Count == 0 &&
            (extension.Groups.Count == 0 && extension.Choices.Count == 0) && !closesRequirement &&
            !PlanningCompactConstruction.CanFinishContinuedBlock(state, extension))
            Fail("REPLAN_NO_PROGRESS", extension.Scope, "An extension must add work, declare an output or close an existing obligation.");
        if (extension.Choices.Any(c => c.Selected is not null)) Fail("CHOICE_SELECTION_FORBIDDEN", "/choices", "Choice selections remain host-owned.");
        var plan = state.Plan is null ? new TaskPlan { Inputs = state.Requirements?.Inputs ?? [] } : Clone(state.Plan);
        var previous = new TaskPlanSymbols(plan);
        var target = previous.Scopes.Single(s => s.Path == extension.Scope).Source;
        target.Tasks.AddRange(extension.Tasks); target.Always.AddRange(extension.Always);
        target.Outputs.AddRange(extension.Outputs); plan.Groups.AddRange(extension.Groups); plan.Choices.AddRange(extension.Choices);
        var identities = TaskPlanCompiler.IdentityDiagnostics(plan);
        if (identities.Count > 0) throw new PlanningResponseException(identities.ToList());
        RestoreRebuildPositions(state, plan, additions.Select(t => t.Id).ToHashSet(StringComparer.Ordinal));
        var symbols = new TaskPlanSymbols(plan);
        var open = construction.OpenScopes.Concat(symbols.Scopes.Where(s => previous.Scopes.All(p => p.Path != s.Path)).Select(s => s.Path))
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        CompleteNewBusinessScopes(state, extension, previous, symbols);
        foreach (var path in extension.CloseScopes)
            if ((!issued.Contains(path) && previous.Scopes.Any(s => s.Path == path)) || !open.Remove(path))
                Fail("CONSTRUCTION_SCOPE_INVALID", path, "Only an issued open scope or a newly introduced child may close, once.");
        foreach (var closed in extension.CloseScopes)
            if (open.Any(p => IsDescendantScope(symbols, p, closed))) Fail("CONSTRUCTION_SCOPE_OPEN", closed, "Close child scopes before their parent.");
        if (extension.Complete && open.Count > 0)
            Fail("CONSTRUCTION_INCOMPLETE", "/", $"Finalization requires explicit closure of remaining open scopes ({open.Count}): {string.Join(", ", open.Order(StringComparer.Ordinal).Take(8))}{(open.Count > 8 ? ", …" : "")}.");
        if (!extension.Complete && open.Count == 0) Fail("CONSTRUCTION_INCOMPLETE", "/", "A closed root must explicitly declare construction complete.");
        if (extension.RequirementIds.Distinct(StringComparer.Ordinal).Count() != extension.RequirementIds.Count ||
            extension.RequirementIds.Any(id => state.Requirements?.Outcomes.All(r => r.Id != id) != false))
            Fail("CONSTRUCTION_REQUIREMENT_INVALID", "/requirementIds", "Associate only distinct accepted requirement identities.");
        var addedIds = additions.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var imported = additions.SelectMany(References).Concat(extension.Outputs.SelectMany(o => TaskPlanCompiler.Values(o.Value))
                .Where(v => v.Kind is "output" or "present" && v.Source is not null).Select(v => v.Source!))
            .Where(id => !addedIds.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        var owners = construction.Checkpoints.Where(c => c.TaskIds.Any(imported.Contains)).ToArray();
        return new()
        {
            BaseHash = Hash(state.Plan), Plan = plan, Hash = Hash(plan), Extension = extension,
            OpenScopes = OrderOpenScopes(symbols, open),
            TaskIds = addedIds.Order(StringComparer.Ordinal).ToList(), ImportedTaskIds = imported,

        };
    }

    private static List<string> OrderOpenScopes(TaskPlanSymbols symbols, IEnumerable<string> paths)
    {
        var depths = symbols.Scopes.ToDictionary(scope => scope.Path, scope => scope.Depth, StringComparer.Ordinal);
        return paths.OrderByDescending(path => depths.GetValueOrDefault(path, -1)).ThenBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static bool IsDescendantScope(TaskPlanSymbols symbols, string path, string parent)
    {
        for (var scope = symbols.Scopes.SingleOrDefault(s => s.Path == path)?.Parent; scope is not null; scope = scope.Parent)
            if (scope.Path == parent) return true;
        return false;
    }

    internal static IEnumerable<string> References(PlanTask task) => task.DependsOn.Concat(TaskPlanCompiler.Values(task)
        .Where(v => v.Kind is "output" or "present" && v.Source is not null).Select(v => v.Source!));

    internal static void Fail(string code, string location, string message) => throw new PlanningResponseException([new(code, location, message)]);
}
