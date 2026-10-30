using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// A request-local view of compiler-validated behavior. This is never executable
// and contains neither a generated outline nor technical contract schema trees.
internal static class PlanningBusinessProjection
{
    internal static JsonObject Build(PlanningSession state, bool global)
    {
        var construction = state.Construction!;
        var candidate = construction.Candidate!;
        var plan = candidate.Plan;
        var symbols = new TaskPlanSymbols(plan);
        var selected = ReviewedTaskIds(state, global);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(selected);
        var openScopes = candidate.OpenScopes.ToHashSet(StringComparer.Ordinal);
        var closedChildren = symbols.Scopes.Where(scope => scope.Owner is not null && !openScopes.Contains(scope.Path))
            .ToLookup(scope => scope.Owner!.Id, StringComparer.Ordinal);
        var exportScopes = symbols.Scopes.Where(scope => global || scope.Path == candidate.Extension.Scope ||
            candidate.Extension.CloseScopes.Contains(scope.Path, StringComparer.Ordinal)).ToArray();
        var claimedValues = (candidate.Extension.BoundaryTransitions ?? []).SelectMany(transition => transition.Outputs)
            .SelectMany(output => TaskPlanCompiler.Values(output.Value))
            .Concat(candidate.Extension.ContinuationBinding is { } binding ? TaskPlanCompiler.Values(binding.Binding) : [])
            .ToArray();
        foreach (var value in exportScopes.SelectMany(scope => scope.Source.Outputs)
            .SelectMany(output => TaskPlanCompiler.Values(output.Value)).Concat(claimedValues))
            if (value.Kind is "output" or "present" && value.Source is { } producer) Include(producer);
        while (pending.TryDequeue(out var id))
        {
            if (!symbols.Tasks.TryGetValue(id, out var entry)) continue;
            // A consumed closed control is behavior, not only an output type.
            // Prior review does not make its executable children disappear.
            foreach (var scope in closedChildren[id])
            {
                foreach (var child in TaskPlanRevisions.Tasks(scope.Source)) Include(child.Id);
                foreach (var value in scope.Source.Outputs.SelectMany(output => TaskPlanCompiler.Values(output.Value)))
                    if (value.Kind is "output" or "present" && value.Source is { } producer) Include(producer);
            }
            foreach (var dependency in entry.Task.DependsOn.Concat(TaskPlanCompiler.Values(entry.Task)
                .Where(value => value.Kind is "output" or "present").Select(value => value.Source).OfType<string>()))
                Include(dependency);
            for (var scope = entry.Scope; scope is not null; scope = scope.Parent)
            {
                foreach (var cleanup in scope.Source.Always) Include(cleanup.Id);
                if (scope.Owner is { } owner) Include(owner.Id);
                if (plan.Groups.SingleOrDefault(group => ReferenceEquals(group.Body, scope.Source)) is { } owningGroup)
                    groups.Add(owningGroup.Id);
            }
            if (entry.Task.Group is { } groupId && groups.Add(groupId) && plan.Groups.SingleOrDefault(group => group.Id == groupId) is { } group)
                foreach (var task in TaskPlanRevisions.Tasks(group.Body)) Include(task.Id);
        }
        if (global) foreach (var group in plan.Groups) groups.Add(group.Id);
        else if (plan.Groups.SingleOrDefault(group => candidate.Extension.Scope.StartsWith("/groups/" + group.Id + "/", StringComparison.Ordinal)) is { } selectedGroup)
            groups.Add(selectedGroup.Id);
        var requirements = new JsonArray();
        foreach (var row in PlanningCompactConstruction.OriginalRequirementRows(state, global ? null :
            PlanningCompactConstruction.LocalRequirementIds(state, selected, exportScopes.Select(scope => scope.Path))).OfType<JsonObject>())
            requirements.Add((JsonNode)new JsonObject { ["id"] = row["id"]!.DeepClone(), ["original"] = row["original"]!.DeepClone(),
                ["evidence"] = "requirement:" + row["id"]!.ToString() });
        var globals = new JsonArray(PlanningCompactConstruction.OriginalGlobalRows(state).OfType<JsonObject>()
            .Select(row => (JsonNode)new JsonObject { ["id"] = row["id"]!.DeepClone(), ["text"] = row["text"]!.DeepClone(),
                ["evidence"] = "source:" + row["id"]!.ToString() }).ToArray());
        if (!string.IsNullOrWhiteSpace(state.Request.Policy.Instructions))
            globals.Add((JsonNode)new JsonObject { ["id"] = "policy:instructions", ["text"] = state.Request.Policy.Instructions });
        if (!string.IsNullOrWhiteSpace(state.Catalog?.Policy.Instructions) && state.Catalog.Policy.Instructions != state.Request.Policy.Instructions)
            globals.Add((JsonNode)new JsonObject { ["id"] = "catalog-policy:instructions", ["text"] = state.Catalog.Policy.Instructions });
        var actions = new JsonArray(symbols.Tasks.Where(pair => selected.Contains(pair.Key))
            .Select(pair => (JsonNode)Action(state, pair.Value.Task, pair.Value.Scope.Path)).ToArray());
        // Applicable original interface obligations need their actual declarations,
        // even when the candidate omits the required consumer. A complete interface
        // claim also exposes unexpected declarations; neither grants group access.
        var compact = construction.Compact!;
        var claimedSources = PlanningCompactConstruction.CandidateCompletionClaims(state)
            .SelectMany(id => compact.RequirementSources.GetValueOrDefault(id) ?? []).ToHashSet(StringComparer.Ordinal);
        var applicableSources = PlanningCompactConstruction.LocalRequirementIds(state, selected,
                exportScopes.Select(scope => scope.Path), includeCompleteOriginal: false)
            .SelectMany(id => compact.RequirementSources.GetValueOrDefault(id) ?? [])
            .Concat(compact.GlobalSourceIds).ToHashSet(StringComparer.Ordinal);
        var currentSources = compact.Sources.Where(source => source.Indexed && source.Version == PlanningGraphCompiler.Fingerprint(source.Text)).ToArray();
        var interfaceClaims = currentSources.Where(source => global || claimedSources.Contains(source.Id) || source.ContinuationOf is { } previous && claimedSources.Contains(previous))
            .SelectMany(source => source.Inputs ?? []).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        var applicableInputs = currentSources.Where(source => global || applicableSources.Contains(source.Id) || source.ContinuationOf is { } previous && applicableSources.Contains(previous))
            .SelectMany(source => source.Inputs ?? []).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        var isolated = !global && symbols.Scopes.SingleOrDefault(scope => scope.Path == candidate.Extension.Scope) is { } reviewedScope && InGroup(reviewedScope);
        var rootValues = symbols.Tasks.Where(pair => selected.Contains(pair.Key) && !InGroup(pair.Value.Scope))
            .SelectMany(pair => TaskPlanCompiler.Values(pair.Value.Task))
            .Concat(exportScopes.Where(scope => !InGroup(scope)).SelectMany(scope => scope.Source.Outputs).SelectMany(output => TaskPlanCompiler.Values(output.Value)))
            .Concat((candidate.Extension.BoundaryTransitions ?? []).Where(transition =>
                symbols.Scopes.SingleOrDefault(scope => scope.Path == transition.Scope) is { } scope && !InGroup(scope))
                .SelectMany(transition => transition.Outputs).SelectMany(output => TaskPlanCompiler.Values(output.Value))).ToArray();
        var rootInputs = rootValues.Where(value => value.Kind == "input").Select(value => value.Source).ToHashSet(StringComparer.Ordinal);
        var visibleScopes = symbols.Scopes.Where(scope => global || scope.Path == candidate.Extension.Scope ||
                scope.Owner is { } owner && selected.Contains(owner.Id) ||
                scope.Path.StartsWith("/groups/", StringComparison.Ordinal) && groups.Any(group => scope.Path == "/groups/" + group + "/body")).ToHashSet();
        foreach (var scope in visibleScopes.ToArray())
            for (var parent = scope.Parent; parent is not null; parent = parent.Parent) visibleScopes.Add(parent);
        var scopes = new JsonArray(symbols.Scopes.Where(visibleScopes.Contains)
            .Select(scope => (JsonNode)new JsonObject
            {
                ["id"] = scope.Path, ["open"] = openScopes.Contains(scope.Path), ["evidence"] = "scope:" + scope.Path,
                ["tasks"] = Strings(scope.Source.Tasks.Select(task => task.Id).Where(selected.Contains)),
                ["cleanup"] = Strings(scope.Source.Always.Select(task => task.Id).Where(selected.Contains)),
                ["outputs"] = Outputs(scope.Source.Outputs)
            }).ToArray());
        var context = new JsonObject
        {
            ["review"] = global ? "completed_workflow" : "current_block",
            ["businessBlock"] = PlanningCompactConstruction.CurrentBlock(state) is { } businessBlock
                ? PlanningCompactConstruction.BusinessBlockContext(businessBlock) : null,
            ["requirements"] = requirements, ["globalConstraints"] = globals,
            ["inputs"] = new JsonArray(plan.Inputs.Where(input => global || interfaceClaims.Count > 0 || applicableInputs.Contains(input.Name) || rootInputs.Contains(input.Name))
                .Select(input =>
                {
                    var declaration = Declaration(input, rootValues, "input:" + input.Name, completeType: interfaceClaims.Contains(input.Name) || applicableInputs.Contains(input.Name));
                    declaration["scope"] = "/root";
                    if (isolated) declaration["availableInReviewedScope"] = false;
                    return (JsonNode)declaration;
                }).ToArray()),
            ["actions"] = actions, ["scopes"] = scopes,
            ["groups"] = new JsonArray(plan.Groups.Where(group => groups.Contains(group.Id)).Select(group => (JsonNode)new JsonObject
            {
                ["id"] = group.Id, ["evidence"] = "group:" + group.Id, ["inputs"] = new JsonArray(group.Inputs.Select(input => (JsonNode)Declaration(input,
                    symbols.Tasks.Where(pair => selected.Contains(pair.Key) && pair.Value.Scope.Path.StartsWith("/groups/" + group.Id + "/", StringComparison.Ordinal))
                        .SelectMany(pair => TaskPlanCompiler.Values(pair.Value.Task)))).ToArray()),
                ["body"] = "/groups/" + group.Id + "/body"
            }).ToArray()),
            ["publicOutputs"] = Outputs(plan.Root.Outputs),
            ["claims"] = new JsonObject { ["completedRequirements"] = Strings(PlanningCompactConstruction.CandidateCompletionClaims(state)),
                ["workflowComplete"] = candidate.Extension.Complete }
        };
        if (plan.Choices.Count > 0)
            context["choices"] = new JsonArray(plan.Choices.Where(choice => global || actions.ToJsonString().Contains('"' + choice.Id + '"', StringComparison.Ordinal))
                .Select(choice => (JsonNode)new JsonObject { ["id"] = choice.Id, ["selected"] = choice.Selected,
                    ["value"] = Value(choice.Alternatives.SingleOrDefault(alternative => alternative.Id == choice.Selected)?.Value) }).ToArray());
        if (candidate.Extension.BoundaryTransitions is { Count: > 0 } transitions)
            context["claims"]!["results"] = Outputs(transitions.SelectMany(transition => transition.Outputs));
        if (construction.Evidence is { Count: > 0 } evidence)
            context["evidence"] = new JsonObject(evidence.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Evidence(pair.Value))));
        return context;

        void Include(string id) { if (selected.Add(id)) pending.Enqueue(id); }
        static bool InGroup(TaskPlanSymbols.Scope scope)
        {
            for (var current = scope; current is not null; current = current.Parent)
                if (current.Path.StartsWith("/groups/", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    private static JsonObject Action(PlanningSession state, PlanTask task, string scope)
    {
        var result = new JsonObject { ["id"] = task.Id, ["kind"] = task.Kind, ["scope"] = scope, ["evidence"] = "task:" + task.Id };
        if (task.DependsOn.Count > 0) result["after"] = Strings(task.DependsOn);
        if (task.Requires is not null) result["requires"] = Value(task.Requires);
        if (task.Inputs.Count > 0) result["arguments"] = Outputs(task.Inputs);
        if (task.Outputs.Count > 0) result["outputs"] = Outputs(task.Outputs);
        if (task.Condition is not null) result["condition"] = Value(task.Condition);
        if (task.Group is not null) result["group"] = task.Group;
        if (task.Kind == "transform")
        {
            result["mode"] = task.Mode ?? "interpret";
            result["instruction"] = task.Objective;
            result["result"] = ResultShape(task.ResultType);
            if (task.Each is not null) result["each"] = JsonSerializer.SerializeToNode(task.Each, PlanningJsonContext.Default.TaskExtractionEach);
        }
        if (task.Kind == "foreach")
        {
            result["items"] = Value(task.Items); result["parallel"] = task.Parallel;
            result["maxItems"] = task.MaxItems; result["maxConcurrency"] = task.MaxConcurrency;
        }
        if (task.Kind == "parallel") result["maxConcurrency"] = task.MaxConcurrency;
        if (task.Operation is { } operationId)
        {
            var contracts = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
                .Where(capability => TaskOperations.Describe(capability).Id == operationId).DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
            if (contracts.Length != 1) throw new PlanningConflictException("Business review requires one exact original operation contract.");
            var capability = contracts[0]; var operation = TaskOperations.Describe(capability);
            result["operation"] = operationId; result["effect"] = capability.EffectKind;
            result["meaning"] = operation.Description;
            if (capability.InputSchema["description"] is { } inputMeaning) result["inputMeaning"] = inputMeaning.DeepClone();
            if (capability.OutputSchema["description"] is { } outputMeaning) result["outputMeaning"] = outputMeaning.DeepClone();
            var identity = Uri.EscapeDataString(operation.Id) + "@" + Uri.EscapeDataString(operation.Version);
            result["contract"] = "contract:" + identity;
            result["outputDirectory"] = "port-page:" + identity + ":/output:0";
            if (capability.FixedInput.Count > 0) result["fixedArguments"] = capability.FixedInput.DeepClone();
            if (capability.RequestBindings.Count > 0)
                result["boundArguments"] = new JsonArray(capability.RequestBindings.Select(binding => (JsonNode)new JsonObject
                    { ["path"] = binding.Path, ["value"] = binding.Value?.DeepClone() }).ToArray());
            var candidate = state.Construction!.Candidate!;
            var consumed = TaskPlanRevisions.Tasks(candidate.Plan).SelectMany(TaskPlanCompiler.Values)
                .Concat(new TaskPlanSymbols(candidate.Plan).Scopes.SelectMany(scope => scope.Source.Outputs)
                    .SelectMany(output => TaskPlanCompiler.Values(output.Value)))
                .Concat((candidate.Extension.BoundaryTransitions ?? []).SelectMany(transition => transition.Outputs)
                    .SelectMany(output => TaskPlanCompiler.Values(output.Value)))
                .Concat(candidate.Extension.ContinuationBinding is { } association ? TaskPlanCompiler.Values(association.Binding) : [])
                .Where(value => value.Kind == "output" && value.Source == task.Id).Select(value => value.Port).ToHashSet(StringComparer.Ordinal);
            var ports = operation.Inputs.Where(port => task.Inputs.Any(argument => argument.Name == port.Name) || port.Schema.ContainsKey("default"))
                .Select(port => Port(port, "input")).Concat(operation.Outputs.Where(port => consumed.Contains(port.Name)).Select(port => Port(port, "output")));
            result["ports"] = new JsonArray(ports.Select(port => (JsonNode)port).ToArray());
        }
        return result;
    }

    internal static HashSet<string> ReviewedTaskIds(PlanningSession state, bool global)
    {
        var construction = state.Construction!;
        var candidate = construction.Candidate!;
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var selected = global ? symbols.Tasks.Keys.ToHashSet(StringComparer.Ordinal)
            : candidate.TaskIds.ToHashSet(StringComparer.Ordinal);
        if (!global)
        {
            var closed = candidate.Extension.CloseScopes.ToHashSet(StringComparer.Ordinal);
            foreach (var scope in symbols.Scopes.Where(scope => closed.Contains(scope.Path)))
                selected.UnionWith(TaskPlanRevisions.Tasks(scope.Source).Select(task => task.Id));
            var closingRequirements = PlanningCompactConstruction.CandidateCompletionClaims(state).ToHashSet(StringComparer.Ordinal);
            foreach (var checkpoint in construction.Checkpoints.Where(checkpoint => checkpoint.Fragment["businessReviewPending"]?.GetValue<bool>() == true &&
                (checkpoint.RequirementIds.Any(closingRequirements.Contains) || closed.Contains(checkpoint.Step?.Scope ?? "") ||
                    PlanningCompactConstruction.CandidateCompletesBlock(candidate) && checkpoint.Step?.BlockId is { } block && block == construction.Compact!.CurrentBlockId)))
                selected.UnionWith(checkpoint.TaskIds);
        }
        return selected;
    }

    private static JsonObject Port(OperationPort port, string direction)
    {
        var result = new JsonObject { ["name"] = port.Name, ["direction"] = direction };
        foreach (var key in new[] { "description", "title", "default", "const", "enum" })
            if (port.Schema[key] is { } value) result[key] = value.DeepClone();
        if (port.Schema["type"] is { } type) result["type"] = type.DeepClone();
        return result;
    }

    private static JsonObject Declaration(TaskInput input, IEnumerable<TaskValue>? values = null, string? evidence = null, bool completeType = false)
    {
        var result = new JsonObject { ["name"] = input.Name, ["type"] = completeType ? ResultShape(input.Type, includeEnum: true) : Type(input.Type), ["required"] = input.Required };
        if (input.Default is not null) result["default"] = Value(input.Default);
        if (evidence is not null) result["evidence"] = evidence;
        if (values is not null)
        {
            var fields = new JsonArray();
            foreach (var value in values.Where(value => value.Kind == "field"))
            {
                var path = new List<string>();
                var source = value;
                while (source.Kind == "field" && source.Items.Count == 1 && source.Port is { } field)
                { path.Insert(0, field); source = source.Items[0]; }
                if (source.Kind != "input" || source.Source != input.Name) continue;
                var type = input.Type;
                foreach (var field in path) { type = type.Fields.SingleOrDefault(item => item.Name == field)?.Type; if (type is null) break; }
                if (type is not null) fields.Add((JsonNode)new JsonObject { ["path"] = Strings(path), ["type"] = Type(type) });
            }
            if (fields.Count > 0) result["accessedFields"] = new JsonArray(fields.DistinctBy(field => field!.ToJsonString()).Select(field => field!.DeepClone()).ToArray());
        }
        return result;
    }

    // Extraction/interpretation targets are executable behavior, unlike an
    // unrelated input schema. Keep their complete semantic field structure.
    private static JsonNode? ResultShape(TaskType? type, bool includeEnum = false)
    {
        if (type is null) return null;
        var result = Type(type)!.AsObject();
        if (includeEnum && type.Enum is { } alternatives) result["enum"] = Strings(alternatives);
        if (type.Items is { } item) result["items"] = ResultShape(item, includeEnum);
        if (type.Fields.Count > 0)
            result["fields"] = new JsonArray(type.Fields.Select(field =>
            {
                var value = new JsonObject { ["name"] = field.Name, ["required"] = field.Required, ["type"] = ResultShape(field.Type, includeEnum) };
                if (field.Default is not null) value["default"] = Value(field.Default);
                return (JsonNode)value;
            }).ToArray());
        return result;
    }

    private static JsonNode? Type(TaskType? type)
    {
        if (type is null) return null;
        var result = new JsonObject { ["kind"] = type.Kind, ["nullable"] = type.Nullable };
        if (type.Items is { } item) result["items"] = new JsonObject { ["kind"] = item.Kind, ["nullable"] = item.Nullable };
        if (type.MinItems is { } minimum) result["minItems"] = minimum;
        if (type.MaxItems is { } maximum) result["maxItems"] = maximum;
        return result;
    }
    private static JsonNode? Value(TaskValue? value) => value is null ? null : PlanningJsonTransport.TaskPlanPart(
        JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue));
    private static JsonArray Outputs(IEnumerable<TaskOutput> outputs) => new(outputs.Select(output => (JsonNode)new JsonObject
        { ["name"] = output.Name, ["value"] = Value(output.Value) }).ToArray());
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    // Explicit retrieval is preserved. An exact schema requested by the Judge is
    // evidence, rather than an excuse to inject the whole catalog by default.
    private static JsonNode? Evidence(JsonNode? evidence) => evidence?.DeepClone();
}
