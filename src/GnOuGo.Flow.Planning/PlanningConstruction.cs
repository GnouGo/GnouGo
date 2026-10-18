using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Presentation and atomic semantic edits; no executable representation or second type system.
internal static partial class PlanningConstruction
{
    internal const string DraftPurpose = "construction_draft", LocalPurpose = "construction_local_judge", IntermediatePurpose = "construction_intermediate_judge";
    internal const string Instructions = """
        Extend the same semantic TaskPlan from available producers toward consumers. Return one action: extension, discoveryRequests, clarifications, evidenceRequests, or rollbackTask.
        Generate only the next small coherent group of business tasks. A simple workflow may finish in one extension. Never regenerate accepted tasks.
        Bind copies, fields and assembly directly; preserve required checks, complete observations, original identities, order, repetitions, explicit nulls and cleanup. No invented results, contracts, defaults or provenance.
        No extra effects/artifacts beyond the accepted request and exact operation contracts.
        Use exact inspected operation contracts. Transforms extract supplied observations or interpret supplied data; they cannot call tools or simulate external effects.
        Each extension appends to one issued open scope. New control bodies become open scopes. Close scopes explicitly after all their tasks and exports are present; a container exposes no outputs or completion until all its children close.
        References must already exist and be available in that scope or be established earlier in this extension. Future work is a remaining obligation, never a placeholder binding. requires is a mandatory Boolean assertion; conditional routes alternatives.
        Every completed requirement association is advisory declared coverage, never proof of execution. Preserve accepted requirements and interfaces. The first extension includes requirements with explicit caller inputs and public outputs.
        Ask for original evidence by task:<id>, scope:<stablepath>, requirement:<id>, contract:<operation-id>, or page:tasks:<offset>, page:requirements:<offset>, page:answers:<offset>, page:scopes:<offset>. Pages are bounded; no evidence is silently discarded. Context is read-only and never edit authority.
        Invalidated originals remain available by invalidation:<sequence>, original:<sequence>:<task-id>, and originalScope:<sequence>:<path>; their original scope, phase and relative order are host-owned.
        rollbackTask requests host-computed invalidation of an earlier producer and its dependent work; it cannot change accepted requirements or permissions. Explain the concrete defect in rollbackReason. Do not request rollback merely to regenerate the plan.
        complete=true requires every scope closed, all requested work and cleanup present, and every accepted requirement explicitly associated with the constructed work. No JavaScript, YAML or executor bindings.
        """;

    internal static string Hash(TaskPlan? plan) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    internal static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    internal static JsonNode? Node<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.SerializeToNode(value, type);

    internal static JsonObject Schema(PlanningSession state, IEnumerable<string> operations)
    {
        var schema = PlanningSchemas.FullProposal(state, compact: false, admitted: operations.ToHashSet(StringComparer.Ordinal));
        var definitions = schema["$defs"]!.AsObject();
        var properties = schema["properties"]!.AsObject();
        var maximum = state.Construction!.Limits.MaxTasksPerDraft;
        var scopes = IssuedScopes(state);
        var outputs = PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, maximum);
        if (scopes is ["/root"] && state.Requirements?.Outputs is { } publicOutputs)
        {
            if (publicOutputs.Count == 0) outputs["maxItems"] = 0;
            else
            {
                var output = definitions["output"]!.DeepClone();
                output["properties"]!["name"] = PlanningSchemas.Enum(publicOutputs.Select(o => o.Name).ToArray());
                outputs["items"] = output;
            }
        }
        var extension = PlanningSchemas.Object(("scope", PlanningSchemas.Enum(scopes)),
            ("tasks", PlanningSchemas.Array(PlanningSchemas.Ref("task"), 0, maximum)),
            ("always", PlanningSchemas.Array(PlanningSchemas.Ref("task"), 0, maximum)),
            ("outputs", outputs),
            ("groups", PlanningSchemas.Array(PlanningSchemas.Ref("group"), 0, maximum)),
            ("choices", PlanningSchemas.Array(PlanningSchemas.Ref("choice"), 0, maximum)),
            ("closeScopes", PlanningSchemas.Array(PlanningSchemas.String(), 0, 32)),
            ("requirementIds", PlanningSchemas.Array(PlanningSchemas.String(), 0, 32)),
            ("complete", PlanningSchemas.Type("boolean")));
        properties.Remove("plan");
        properties["baseHash"] = PlanningSchemas.Enum(Hash(state.Plan));
        properties["extension"] = PlanningSchemas.Nullable(extension);
        properties["evidenceRequests"] = PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.String(), 1, 8));
        properties["rollbackTask"] = PlanningSchemas.Nullable(PlanningSchemas.String());
        properties["rollbackReason"] = PlanningSchemas.Nullable(PlanningSchemas.String());
        schema["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        // Keep the semantic definitions used by the extension; recursive task/scope definitions are shared.
        foreach (var task in definitions["task"]!["anyOf"]!.AsArray().Where(t => t!["properties"]?["kind"]?["enum"]?[0]?.ToString() == "transform" &&
            t["properties"]?["mode"] is null).ToArray()) definitions["task"]!["anyOf"]!.AsArray().Remove(task);
        return PlanningContractValidation.ProjectStructuredOutputSchema(PlanningSchemas.Compact(schema));
    }

    private static string[] IssuedScopes(PlanningSession state)
    {
        var construction = state.Construction!;
        var scopes = construction.OpenScopes.Skip(construction.ScopeCursor).Take(8).ToArray();
        if (scopes.Length == 0) scopes = construction.OpenScopes.Take(8).ToArray();
        if (construction.Candidate is { } retained && !scopes.Contains(retained.Extension.Scope, StringComparer.Ordinal)) scopes = [retained.Extension.Scope];
        return scopes;
    }

    internal static IReadOnlyList<PlanningCapability> Contracts(PlanningSession state, IEnumerable<string>? required = null)
    {
        var all = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version)).ToArray();
        var ids = (required ?? []).ToHashSet(StringComparer.Ordinal);
        ids.UnionWith((state.Construction?.Evidence ?? new JsonObject()).Where(p => p.Key.StartsWith("contract:", StringComparison.Ordinal)).Select(p => p.Key[9..]));
        var explicitIds = PlanningDiscoveryContext.Inspected(state).TakeLast(4).Select(c => c.Operation!.Id).ToHashSet(StringComparer.Ordinal);
        return all.Where(c => ids.Contains(TaskOperations.Describe(c).Id) || explicitIds.Contains(TaskOperations.Describe(c).Id))
            .Concat(all.Where(c => c.Kind == "registered").Take(4))
            .Concat(PlanningDiscoveryContext.Candidates(state).Take(4).Select(c => all.FirstOrDefault(a => a.Id == c.Id && a.Version == c.Version)).OfType<PlanningCapability>())
            .DistinctBy(c => (c.Id, c.Version)).ToArray();
    }

    internal static JsonObject Context(PlanningSession state, bool review = false, bool intermediate = false)
    {
        var construction = state.Construction!;
        var draft = construction.Candidate;
        var plan = draft?.Plan ?? state.Plan ?? new TaskPlan { Inputs = state.Requirements?.Inputs ?? [] };
        var symbols = new TaskPlanSymbols(plan);
        var scopes = (draft?.OpenScopes ?? construction.OpenScopes).Skip(construction.ScopeCursor).Take(8).ToArray();
        if (scopes.Length == 0 && construction.OpenScopes.Count > 0) scopes = construction.OpenScopes.Take(8).ToArray();
        var taskIds = review && draft is not null ? draft.TaskIds.Concat(draft.ImportedTaskIds).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (!review)
            foreach (var scope in symbols.Scopes.Where(s => scopes.Contains(s.Path, StringComparer.Ordinal)))
                taskIds.UnionWith(scope.Source.Tasks.Concat(scope.Source.Always).TakeLast(8).Select(t => t.Id));
        if (intermediate && draft is not null)
            taskIds.UnionWith(construction.Checkpoints.Where(c => c.TaskIds.Any(draft.ImportedTaskIds.Contains)).SelectMany(c => c.TaskIds).Take(16));
        var selected = taskIds.Where(symbols.Tasks.ContainsKey).Select(id => symbols.Tasks[id].Task).ToArray();
        var requiredIds = draft?.Extension.RequirementIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var requirements = state.Requirements?.Outcomes.Where(r => requiredIds.Contains(r.Id)).ToList() ?? [];
        foreach (var requirement in state.Requirements?.Outcomes.Skip(construction.RequirementCursor).Take(8) ?? [])
            if (!requirements.Any(r => r.Id == requirement.Id)) requirements.Add(requirement);
        var requests = new Dictionary<string, IReadOnlyList<TaskValue>>(StringComparer.Ordinal);
        foreach (var scope in symbols.Scopes.Where(s => scopes.Contains(s.Path, StringComparer.Ordinal) || s.Path == draft?.Extension.Scope).Take(8))
        {
            var visibleScopes = new HashSet<string>(StringComparer.Ordinal);
            for (var ancestor = scope; ancestor is not null; ancestor = ancestor.Parent) visibleScopes.Add(ancestor.Path);
            var values = taskIds.Where(id => symbols.Tasks.TryGetValue(id, out var site) && visibleScopes.Contains(site.Scope.Path)).TakeLast(8)
                .Select(id => new TaskValue { Kind = "output", Source = id }).ToList();
            values.AddRange(plan.Inputs.Take(6).Select(i => new TaskValue { Kind = "input", Source = i.Name }));
            values.Add(new() { Kind = "item" }); values.Add(new() { Kind = "index" });
            requests[scope.Path] = values;
        }
        var probe = Clone(plan);
        foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var validated = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request,
            (draft?.OpenScopes ?? construction.OpenScopes).ToHashSet(StringComparer.Ordinal), requests);
        var interfaces = new JsonObject();
        foreach (var (scope, values) in validated.Interfaces)
            interfaces[scope] = new JsonArray(values.Select(v => (JsonNode)new JsonObject
            { ["value"] = Node(v.Value, PlanningJsonContext.Default.TaskValue), ["schema"] = v.Schema.DeepClone() }).ToArray());
        var operations = selected.Select(t => t.Operation).OfType<string>().Concat(requirements.SelectMany(r => r.Commitments ?? []).Select(c => c.Operation));
        var contracts = Contracts(state, operations);
        var context = new JsonObject
        {
            ["request"] = state.Request.Prompt,
            ["revision"] = state.Request.RevisionContext,
            ["executionFailure"] = PlanningPrompt.FailureContext(state.Request.FailureEvidence),
            ["instructions"] = state.Request.Policy.Instructions,
            ["baseHash"] = Hash(state.Plan),
            ["candidateHash"] = draft?.Hash,
            ["requirements"] = Node(new PlanningRequirements { Summary = state.Requirements?.Summary ?? "", Inputs = state.Requirements?.Inputs,
                Outputs = state.Requirements?.Outputs, Outcomes = requirements }, PlanningJsonContext.Default.PlanningRequirements),
            ["requirementPage"] = new JsonObject { ["offset"] = construction.RequirementCursor, ["total"] = state.Requirements?.Outcomes.Count ?? 0 },
            ["answers"] = Node(state.AnswerHistory?.TakeLast(3).ToList(), PlanningJsonContext.Default.ListPlanningAnswerBatch),
            ["answerHistoryCount"] = state.AnswerHistory?.Count ?? 0,
            ["frontier"] = new JsonArray(scopes.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["frontierCount"] = construction.OpenScopes.Count,
            ["interfaces"] = interfaces,
            ["tasks"] = new JsonArray(selected.Select(t => TaskNode(t)).ToArray()),
            ["operations"] = new JsonArray(contracts.Select(c => (JsonNode)ContractNode(c)).ToArray()),
            ["sources"] = Node(state.Discovery.Sources, PlanningJsonContext.Default.ListCapabilitySource),
            ["operationDirectory"] = new JsonArray(PlanningDiscoveryContext.Candidates(state).Take(12).Select(c => (JsonNode)new JsonObject
                { ["id"] = c.Operation!.Id, ["sourceId"] = c.SourceId, ["description"] = c.Description }).ToArray()),
            ["evidence"] = construction.Evidence?.DeepClone(),
            ["draft"] = draft is null ? null : Node(draft.Extension, PlanningJsonContext.Default.PlanningConstructionExtension),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(draft?.Diagnostics ?? state.Diagnostics),
            ["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state), ["remainingRepairs"] = PlanningModelCalls.RemainingRepairs(state),
                ["maxTasks"] = construction.Limits.MaxTasksPerDraft, ["acceptedCheckpoints"] = construction.Checkpoints.Count,
                ["maxCheckpoints"] = construction.Limits.MaxCheckpoints },
            ["taskCount"] = symbols.Tasks.Count,
            ["interfacePresentation"] = "At most eight recent task ports and six caller inputs per scope are shown; original inputs remain in requirements and other original task contracts can be retrieved as evidence.",
            ["rebuildTasks"] = new JsonArray(construction.RebuildTasks.Where(t => !symbols.Tasks.ContainsKey(t.Id)).Take(8).Select(t =>
            {
                var node = TaskNode(t); var placement = construction.RebuildScopes.SingleOrDefault(s => s.Tasks.Contains(t.Id, StringComparer.Ordinal) || s.Always.Contains(t.Id, StringComparer.Ordinal));
                node["requiredScope"] = placement?.Path; node["requiredPhase"] = placement?.Always.Contains(t.Id, StringComparer.Ordinal) == true ? "always" : "tasks";
                return node;
            }).ToArray()),
            ["rebuildTaskCount"] = construction.RebuildTasks.Count(t => !symbols.Tasks.ContainsKey(t.Id)),
            ["invalidationCount"] = construction.Invalidations.Count,
            ["reviewScope"] = intermediate ? "junction" : review ? "draft" : "extension"
        };
        if (review && draft is not null)
        {
            var closed = symbols.Scopes.Where(s => draft.Extension.CloseScopes.Contains(s.Path, StringComparer.Ordinal)).ToArray();
            var anchors = closed.SelectMany(s => s.Source.Tasks.Concat(s.Source.Always)).Select(t => t.Id).Distinct(StringComparer.Ordinal).Take(16).ToHashSet(StringComparer.Ordinal);
            context["closures"] = new JsonArray(closed.Select(s => (JsonNode)new JsonObject
            {
                ["path"] = s.Path, ["parent"] = s.Parent?.Path, ["owner"] = s.Owner is null ? null : TaskNode(s.Owner),
                ["outputs"] = Node(s.Source.Outputs, PlanningJsonContext.Default.ListTaskOutput),
                ["taskCount"] = s.Source.Tasks.Count, ["alwaysCount"] = s.Source.Always.Count,
                ["tasks"] = new JsonArray(s.Source.Tasks.Where(t => anchors.Contains(t.Id)).Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray()),
                ["always"] = new JsonArray(s.Source.Always.Where(t => anchors.Contains(t.Id)).Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray()),
                ["checkpoints"] = new JsonArray(construction.Checkpoints.Where(c => c.TaskIds.Any(id => symbols.Tasks.TryGetValue(id, out var site) &&
                    (site.Scope.Path == s.Path || IsDescendantScope(symbols, site.Scope.Path, s.Path)))).TakeLast(16).Select(c => (JsonNode)new JsonObject
                    { ["sequence"] = c.Sequence, ["planHash"] = c.PlanHash, ["taskCount"] = c.TaskIds.Count }).ToArray()),
                ["evidence"] = "scope:" + s.Path, ["directory"] = "page:tasks:0"
            }).ToArray());
        }
        return context;
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
        var additions = TaskPlanRevisions.Tasks(new TaskScope { Tasks = extension.Tasks, Always = extension.Always })
            .Concat(extension.Groups.SelectMany(g => TaskPlanRevisions.Tasks(g.Body))).ToArray();
        if (additions.Length > construction.Limits.MaxTasksPerDraft || extension.Groups.Count > construction.Limits.MaxTasksPerDraft || extension.Choices.Count > construction.Limits.MaxTasksPerDraft)
            Fail("CONSTRUCTION_DRAFT_LIMIT", extension.Scope, "The extension exceeds its captured recursive task limit.");
        if (additions.Length == 0 && extension.Outputs.Count == 0 && extension.CloseScopes.Count == 0)
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
        foreach (var path in extension.CloseScopes)
            if ((!issued.Contains(path) && previous.Scopes.Any(s => s.Path == path)) || !open.Remove(path))
                Fail("CONSTRUCTION_SCOPE_INVALID", path, "Only an issued open scope or a newly introduced child may close, once.");
        foreach (var closed in extension.CloseScopes)
            if (open.Any(p => IsDescendantScope(symbols, p, closed))) Fail("CONSTRUCTION_SCOPE_OPEN", closed, "Close child scopes before their parent.");
        if (extension.Complete && open.Count > 0) Fail("CONSTRUCTION_INCOMPLETE", "/", "All construction scopes must close before finalization.");
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
            OpenScopes = open.OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList(),
            TaskIds = addedIds.Order(StringComparer.Ordinal).ToList(), ImportedTaskIds = imported,
            NeedsIntermediateReview = construction.Checkpoints.Count > 0 &&
                ((construction.Checkpoints.Count + 1) % construction.Limits.IntermediateReviewInterval == 0 || owners.Length > 1 ||
                 extension.CloseScopes.Any(p => previous.Scopes.Any(s => s.Path == p)))
        };
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
