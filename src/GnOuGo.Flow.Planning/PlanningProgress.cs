using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Measures the existing coordinator's transitions. These facts never establish
// business approval, select a producer, or create another executable plan.
internal static class PlanningProgress
{
    internal static void Initialize(PlanningSession state)
    {
        if (state.Construction?.Compact is not { } compact || compact.Progress is not null) return;
        var facts = Facts(state);
        compact.Progress = new()
        {
            Evidence = facts.Evidence.Order(StringComparer.Ordinal).ToList(),
            Behaviors = facts.Behaviors.Order(StringComparer.Ordinal).ToList(),
            CompletedRequirements = facts.Completed.Order(StringComparer.Ordinal).ToList(),
            CountedModelCalls = state.ModelCalls
        };
    }

    internal static void EnsureCallAvailable(PlanningSession state)
    {
        if (state.PendingCall is not null || state.Construction?.Compact is not { } compact) return;
        var limit = state.Construction.Limits.MaxNonProductiveModelCalls;
        if (limit is null) return; // Retained sessions keep their original captured limits.
        if (limit < 1 || limit > 64 && !PlanningTokenThresholds.OptimizationAdvisory(state.Request))
            throw new PlanningConflictException("The captured nonproductive-call allowance is invalid.");
        if (compact.Progress is { } retained && SuppliedOriginals(state).Except(retained.Evidence, StringComparer.Ordinal).Any())
            Record(state, null, annotate: false);
        if (compact.Progress is null || compact.Progress.NonProductiveModelCalls < limit.Value) return;
        var requirements = compact.CurrentStep?.RequirementIds ?? state.Construction.Requirements
            .Where(requirement => requirement.Status != "closed").Select(requirement => requirement.Id).ToList();
        var prerequisites = (compact.SuspendedSteps ?? []).Where(step => step.Status is "waiting" or "resuming")
            .Select(step => step.Prerequisite.Need).Concat(compact.CurrentStep?.Prerequisites?.Select(item => item.Need) ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        var strategies = compact.FailedActions.Concat(state.Construction.FailedCandidates.Where(identity =>
            identity.StartsWith("strategy:", StringComparison.Ordinal)))
            .Concat(state.Construction.LastProposalHash is { } last && last.StartsWith("strategy:", StringComparison.Ordinal) ? [last] : [])
            .Distinct(StringComparer.Ordinal).ToArray();
        var attempts = strategies.TakeLast(limit.Value).ToArray();
        var missing = prerequisites.Length > 0 ? string.Join("; ", prerequisites) :
            "No validated executable contribution or new evidence for the open obligations was produced; no more specific prerequisite was established.";
        if (PlanningTokenThresholds.OptimizationAdvisory(state.Request))
        {
            PlanningOptimizationThresholds.Record(state, "progress", "nonproductive_calls", compact.Progress.NonProductiveModelCalls, limit.Value,
                new JsonObject { ["attemptedStrategies"] = Strings(strategies), ["missingPrerequisites"] = Strings(prerequisites),
                    ["requirements"] = Strings(requirements), ["explanation"] = missing,
                    ["attempts"] = new JsonArray((state.StrategyAttempts ?? []).TakeLast(limit.Value).Select(attempt => attempt.DeepClone()).ToArray()) });
            return;
        }
        throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS",
            $"{compact.Progress.NonProductiveModelCalls} model calls produced no new useful evidence, executable behavior or reviewed completion. " +
            "Unfinished obligations: " + string.Join(", ", requirements) +
            ". Missing prerequisite: " + missing + " Retained strategy identities: " + string.Join(", ", attempts) +
            ". Retained strategies cannot be repeated without a new binding, original fact or authorized productive correction.",
            details: new JsonObject { ["location"] = "/construction/compact", ["limit"] = limit,
                ["current"] = compact.Progress.NonProductiveModelCalls,
                ["strategy_count"] = strategies.Length,
                ["attempted_strategies"] = Strings(attempts), ["missing_prerequisites"] = Strings(prerequisites),
                ["requirements"] = new JsonArray(requirements.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
    }

    internal static void Record(PlanningSession state) => Record(state, null, annotate: true);

    internal static void RecordOriginal(PlanningSession state, string text)
    {
        Initialize(state);
        Record(state, [OriginalIdentity(text)], annotate: false);
    }

    private static void Record(PlanningSession state, IEnumerable<string>? supplied, bool annotate)
    {
        if (state.Construction?.Compact is not { Progress: not null } compact) return;
        var progress = compact.Progress!;
        var facts = Facts(state);
        if (supplied is not null) facts.Evidence.UnionWith(supplied);
        var evidence = facts.Evidence.Except(progress.Evidence, StringComparer.Ordinal).ToArray();
        var behaviors = facts.Behaviors.Except(progress.Behaviors, StringComparer.Ordinal).ToArray();
        var completed = facts.Completed.Except(progress.CompletedRequirements, StringComparer.Ordinal).ToArray();
        var calls = Math.Max(0, state.ModelCalls - progress.CountedModelCalls);
        var kind = completed.Length > 0 ? "business_completion" : behaviors.Length > 0 ? "executable_behavior" :
            evidence.Length > 0 ? "new_evidence" : "administrative";
        progress.NonProductiveModelCalls = kind == "administrative" ? progress.NonProductiveModelCalls + calls : 0;
        progress.CountedModelCalls = state.ModelCalls;
        progress.Evidence.AddRange(evidence.Order(StringComparer.Ordinal));
        progress.Behaviors.AddRange(behaviors.Order(StringComparer.Ordinal));
        progress.CompletedRequirements.AddRange(completed.Order(StringComparer.Ordinal));
        var requestPrefix = state.Request.SessionId + ":" + state.ModelCalls + ":";
        if (annotate && state.RequestMeasurements?.LastOrDefault(measurement => measurement.Admission == "admitted" &&
            measurement.RequestId.StartsWith(requestPrefix, StringComparison.Ordinal)) is { } measurement)
        {
            measurement.BlockId ??= compact.CurrentBlockId ?? state.Construction.Checkpoints.LastOrDefault()?.Step?.BlockId;
            measurement.StepId ??= compact.CurrentStep?.Id ?? state.Construction.Checkpoints.LastOrDefault()?.Step?.Id;
            (measurement.Transitions ??= []).Add(new(state.Revision + 1, kind, evidence.Length, behaviors.Length, completed.Length,
                progress.NonProductiveModelCalls));
        }
    }

    internal static string Fingerprint(PlanningSession state)
    {
        var facts = Facts(state);
        return Hash(new JsonObject { ["evidence"] = Strings(facts.Evidence.Order(StringComparer.Ordinal)),
            ["behavior"] = Strings(facts.Behaviors.Order(StringComparer.Ordinal)),
            ["completed"] = Strings(facts.Completed.Order(StringComparer.Ordinal)) });
    }

    internal static string Strategy(PlanningSession state, string purpose, JsonNode response) => Strategy(state, purpose, response, out _);

    internal static string Strategy(PlanningSession state, string purpose, JsonNode response, out JsonObject evidence)
    {
        evidence = Canonical(StrategyEvidence(state, response))!.AsObject();
        return purpose + ":" + Hash(evidence);
    }

    // Exact receipt recovery reuses its original assessment. Only distinct issued
    // requests can demonstrate a repeated strategy on unchanged useful state.
    internal static string AssessAttempt(PlanningSession state, string purpose, JsonNode response, string requestId,
        string prefix = "", string? suffix = null, JsonNode? issuedSchema = null, JsonNode? strategyResponse = null, JsonNode? exactIssuedSchema = null)
    {
        var attempts = state.StrategyAttempts ??= [];
        var responseHash = Hash(response);
        var schema = issuedSchema?.DeepClone();
        if (schema is JsonObject annotated) { annotated.Remove("title"); annotated.Remove("$comment"); }
        var schemaHash = schema is null ? null : Hash(schema);
        var exactSchemaHash = exactIssuedSchema is null ? null : Hash(exactIssuedSchema);
        var kind = issuedSchema is null ? "strategy" : "invalid_response";
        if (attempts.SingleOrDefault(attempt => attempt["requestId"]?.ToString() == requestId) is { } recovered)
        {
            if (recovered["responseHash"]?.ToString() != responseHash || recovered["schemaHash"]?.ToString() != schemaHash ||
                recovered["kind"]?.ToString() != kind || recovered["exactSchemaHash"]?.ToString() != exactSchemaHash)
                throw new PlanningConflictException("The recovered strategy attempt does not match its exact received response and issued schema.");
            if (recovered["repeated"]?.GetValue<bool>() == true) Repeated(recovered);
            return recovered["identity"]!.ToString();
        }
        var strategy = Strategy(state, purpose, strategyResponse ?? response, out var evidence);
        var usefulState = Fingerprint(state);
        var ordinary = prefix + strategy + ":progress:" + usefulState + (schemaHash is null ? "" : ":schema:" + schemaHash);
        var identity = suffix is null ? ordinary : ordinary + ":" + suffix;
        var previous = Previous(identity, ordinary);
        var attempted = new JsonObject { ["identity"] = identity, ["phase"] = purpose, ["requestId"] = requestId,
            ["blockId"] = state.Construction?.Compact?.CurrentBlockId, ["usefulState"] = usefulState,
            ["strategy"] = evidence, ["kind"] = kind, ["responseHash"] = responseHash, ["schemaHash"] = schemaHash,
            ["exactSchemaHash"] = exactSchemaHash, ["repeated"] = previous is not null, ["previousRequestId"] = previous?["requestId"]?.ToString() };
        attempts.Add(attempted);
        if (previous is not null) Repeated(attempted, previous);
        return identity;

        JsonObject? Previous(string identity, string? ordinary = null) => attempts.FirstOrDefault(attempt =>
            attempt["requestId"]?.ToString() != requestId &&
            (attempt["identity"]?.ToString() == identity || ordinary is not null && attempt["identity"]?.ToString() == ordinary));
        void Repeated(JsonObject current, JsonObject? previous = null)
        {
            previous ??= attempts.SingleOrDefault(attempt => attempt["requestId"]?.ToString() == current["previousRequestId"]?.ToString());
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS",
                "The same canonical strategy was repeated on unchanged useful evidence and accepted behavior" +
                (schemaHash is null ? "." : " under the same issued response schema.") + " Current request: " + requestId +
                "; prior request: " + previous?["requestId"]?.ToString() +
                ". Exact arguments, bindings, obligations and available evidence remain in the strategy-attempt journal.",
                details: new JsonObject { ["previousAttempt"] = previous?.DeepClone(), ["currentAttempt"] = current.DeepClone() });
        }
    }

    private static JsonObject StrategyEvidence(PlanningSession state, JsonNode response)
    {
        var projected = response.DeepClone();
        var behavior = new Behavior(state.Plan ?? new(), state.Construction?.OpenScopes ?? []);
        var declarations = new List<(JsonObject Task, string Id, string Path)>();
        if (projected is JsonObject proposal && proposal["extension"] is JsonObject extension)
        {
            Tasks(extension["tasks"], "/tasks"); Tasks(extension["always"], "/always");
            if (extension["groups"] is JsonArray groups)
                for (var index = 0; index < groups.Count; index++)
                    if (groups[index] is JsonObject group) Scope(group["body"], "/groups/" + index + "/body");
        }
        var locals = declarations.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => (group.Count() == 1 ? "local:" : "duplicate:") + group.First().Path, StringComparer.Ordinal);
        var localTasks = declarations.ToDictionary(item => item.Task, item => locals[item.Id]);
        Normalize(projected);
        JsonNode? selected = null;
        if (state.Construction?.Compact?.CurrentStep is { } step)
        {
            selected = new JsonObject
            {
                ["scope"] = step.Scope, ["kind"] = step.Kind, ["requirements"] = Strings(step.RequirementIds.Order(StringComparer.Ordinal)),
                ["closeRequirements"] = Strings(step.CloseRequirementIds.Order(StringComparer.Ordinal)),
                ["inputs"] = JsonSerializer.SerializeToNode(step.Inputs, PlanningJsonContext.Default.ListTaskValue),
                ["dependencyTaskIds"] = Strings(step.DependencyTaskIds.Order(StringComparer.Ordinal)),
                ["operationIds"] = Strings(step.OperationIds.Order(StringComparer.Ordinal)), ["groupId"] = step.GroupId,
                ["outputNames"] = Strings(step.OutputNames.Order(StringComparer.Ordinal))
            };
            Normalize(selected, local: false);
        }
        var consumers = new JsonArray((state.Construction?.Compact?.SuspendedSteps ?? [])
            .Where(item => item.Status is "waiting" or "resuming").Select(item => (JsonNode)new JsonObject
            {
                ["requirements"] = Strings(item.Consumer.RequirementIds),
                ["outputs"] = Strings(item.Consumer.OutputNames),
                ["operations"] = Strings(item.Consumer.OperationIds),
                ["inputs"] = new JsonArray(item.Consumer.Inputs.Select(input =>
                    JsonSerializer.SerializeToNode(input, PlanningJsonContext.Default.TaskValue)).ToArray()),
                ["type"] = JsonSerializer.SerializeToNode(item.Prerequisite.ExpectedType, PlanningJsonContext.Default.TaskType),
                ["evidence"] = Strings(item.Prerequisite.EvidenceAddresses)
            }).ToArray());
        Normalize(consumers, local: false);
        return new JsonObject { ["action"] = projected, ["selected"] = selected, ["consumers"] = consumers };

        void Tasks(JsonNode? node, string path)
        {
            if (node is not JsonArray tasks) return;
            for (var index = 0; index < tasks.Count; index++)
            {
                if (tasks[index] is not JsonObject task || task["id"] is not JsonValue id || !id.TryGetValue<string>(out _)) continue;
                var location = path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                declarations.Add((task, id.ToString(), location));
                Scope(task["body"], location + "/body"); Scope(task["otherwise"], location + "/otherwise");
                if (task["branches"] is JsonArray branches)
                    for (var branch = 0; branch < branches.Count; branch++) Scope(branches[branch], location + "/branches/" + branch);
            }
        }
        void Scope(JsonNode? node, string path)
        { if (node is JsonObject scope) { Tasks(scope["tasks"], path + "/tasks"); Tasks(scope["always"], path + "/always"); } }
        string TaskIdentity(string id, bool local) => local && locals.TryGetValue(id, out var declared) ? declared : behavior.TaskIdentity(id);
        string ScopeIdentity(string path, bool local)
        {
            if (local && path.Split('/') is ["", "tasks", var id, ..] && locals.TryGetValue(id, out var declared))
                return declared + path[("/tasks/" + id).Length..];
            return behavior.ScopeIdentity(path, includeSequence: true);
        }
        static bool StringArray(JsonArray values) => values.All(value => value is JsonValue item && item.TryGetValue<string>(out _));
        void Normalize(JsonNode? node, bool local = true)
        {
            if (node is JsonArray array) { foreach (var child in array) Normalize(child, local); return; }
            if (node is not JsonObject value) return;
            if (value.ContainsKey("requirementIds") && value.ContainsKey("successCriteria"))
                foreach (var key in new[] { "informationNeeds", "expectedResults", "successCriteria" }) value.Remove(key);
            // Only navigation prose is removed. Typed literals (including Text)
            // and actual operation bindings remain part of the attempted strategy.
            foreach (var key in new[] { "objective", "reason", "expectedOutputs", "need", "id", "baseHash" })
                if (!(key == "objective" && value["kind"]?.ToString() == "transform" && value.ContainsKey("resultType"))) value.Remove(key);
            if (local && localTasks.TryGetValue(value, out var declaration)) value["id"] = declaration;
            foreach (var key in new[] { "inputTaskIds", "dependencyTaskIds", "dependsOn" })
                if (value[key] is JsonArray ids && StringArray(ids)) value[key] = Strings(ids.Select(id => TaskIdentity(id!.GetValue<string>(), local)).Order(StringComparer.Ordinal));
            foreach (var key in new[] { "requirementIds", "closeRequirementIds", "operationIds", "outputNames" })
                if (value[key] is JsonArray ids && StringArray(ids)) value[key] = Strings(ids.Select(id => id!.GetValue<string>()).Order(StringComparer.Ordinal));
            foreach (var key in new[] { "scope", "continueInScope" })
                if (value[key] is JsonValue scope && scope.TryGetValue<string>(out var path)) value[key] = ScopeIdentity(path, local);
            if (value["closeScopes"] is JsonArray scopes && StringArray(scopes)) value["closeScopes"] = Strings(scopes.Select(scope => ScopeIdentity(scope!.GetValue<string>(), local)));
            if (value["kind"]?.ToString() is "output" or "present" && value["source"] is JsonValue producer && producer.TryGetValue<string>(out var source))
                value["source"] = TaskIdentity(source, local);
            if (value["rollbackTask"] is JsonValue rollback && rollback.TryGetValue<string>(out var target)) value["rollbackTask"] = TaskIdentity(target, local);
            if (value["reselectStep"] is JsonValue reselection && reselection.TryGetValue<string>(out _)) value["reselectStep"] = "reselect";
            foreach (var child in value.Select(pair => pair.Value).ToArray()) Normalize(child, local);
        }
    }

    private static (HashSet<string> Evidence, HashSet<string> Behaviors, HashSet<string> Completed) Facts(PlanningSession state)
    {
        var evidence = (state.Construction?.Compact?.Progress?.Evidence ?? []).ToHashSet(StringComparer.Ordinal);
        evidence.UnionWith(SuppliedOriginals(state));
        foreach (var source in state.Construction?.Compact?.Sources ?? [])
            // Source coordinates are host-owned coverage of the unchanged original.
            // Identical text at two original positions still requires both pages.
            if (source.Indexed && source.Kind is "page" or "answer_page" or "revision_page")
                evidence.Add("original:" + source.Id + ":" + Hash(JsonValue.Create(source.Text)!));
        var selectedOperations = (state.Construction?.Compact?.CurrentStep?.OperationIds ?? [])
            .Concat(state.Plan is null ? [] : TaskPlanRevisions.Tasks(state.Plan).Select(task => task.Operation).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var capability in state.Discovery.Resolved.Concat(state.Catalog?.Capabilities ?? []))
            if (selectedOperations.Contains(TaskOperations.Describe(capability).Id))
                evidence.Add("contract:" + Hash(JsonSerializer.SerializeToNode(capability, PlanningJsonContext.Default.PlanningCapability)!));
        // New catalog rows alone are exploration. Only exact selected contracts
        // or deliberately retrieved missing originals can reset productive work.
        foreach (var entry in state.Construction?.Evidence ?? [])
            if (entry.Key.StartsWith("schema", StringComparison.Ordinal) || entry.Key.StartsWith("description:", StringComparison.Ordinal) ||
                entry.Key.StartsWith("requirement:", StringComparison.Ordinal) || entry.Key.StartsWith("source:", StringComparison.Ordinal))
                evidence.Add("retrieved:" + entry.Key + ":" + Hash(entry.Value));
        var plan = state.Construction?.Candidate is { Diagnostics.Count: 0 } candidate &&
            candidate.BaseHash == PlanningConstruction.Hash(state.Plan) && candidate.Hash == PlanningConstruction.Hash(candidate.Plan)
            ? candidate.Plan : state.Plan;
        var openScopes = plan is not null && ReferenceEquals(plan, state.Construction?.Candidate?.Plan)
            ? state.Construction!.Candidate!.OpenScopes : state.Construction?.OpenScopes ?? [];
        var behaviors = plan is null ? [] : new Behavior(plan, openScopes).Facts();
        var completed = (state.Construction?.Requirements ?? []).Where(requirement => requirement.Status == "closed")
            .Select(requirement => requirement.Id).ToHashSet(StringComparer.Ordinal);
        return (evidence, behaviors, completed);
    }

    private static IEnumerable<string> SuppliedOriginals(PlanningSession state)
    {
        var pages = (state.Construction?.Compact?.Sources ?? []).Where(source => source.Kind is "page" or "revision_page").ToArray();
        if (string.Concat(pages.Select(page => page.Text)) == state.Request.Prompt)
            foreach (var page in pages.Where(page => page.Version == PlanningGraphCompiler.Fingerprint(page.Text)))
                yield return OriginalIdentity(page.Text);
        foreach (var batch in state.AnswerHistory ?? [])
            foreach (var answer in batch.Answers)
            {
                var original = answer.Text ?? batch.Questions.SingleOrDefault(question => question.Id == answer.QuestionId)?
                    .Alternatives.SingleOrDefault(alternative => alternative.Id == answer.AlternativeId)?.Description;
                if (original is not null) yield return OriginalIdentity(original);
            }
    }

    private static string OriginalIdentity(string text) => "supplied_original:" + Hash(JsonValue.Create(text));

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string Hash(JsonNode? value) => PlanningGraphCompiler.Fingerprint(Canonical(value)?.ToJsonString() ?? "null");
    private static JsonNode? Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => value?.DeepClone()
    };

    private sealed class Behavior(TaskPlan plan, IEnumerable<string> openScopes)
    {
        private readonly TaskPlanSymbols _symbols = new(plan);
        private readonly HashSet<string> _openScopes = openScopes.ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        internal HashSet<string> Facts()
        {
            var facts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in _symbols.Tasks.Values.Where(task => !TransparentSequence(task.Task) && Productive(task.Task))
                .GroupBy(task => TaskIdentity(task.Task.Id), StringComparer.Ordinal))
                for (var occurrence = 0; occurrence < group.Count(); occurrence++)
                    facts.Add("task:" + group.Key + ":" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var output in plan.Root.Outputs) facts.Add("public:" + Hash(new JsonObject { ["name"] = output.Name, ["value"] = Value(output.Value) }));
            return facts;
        }

        internal string ScopeIdentity(string path, bool includeSequence = false)
        {
            if (path == "/root") return "/root";
            var scope = _symbols.Scopes.SingleOrDefault(item => item.Path == path);
            if (scope is null) return "unavailable_scope";
            if (!includeSequence && scope.Owner is { } wrapper && TransparentSequence(wrapper) && !_openScopes.Contains(path))
                return scope.Parent is null ? "/root" : ScopeIdentity(scope.Parent.Path);
            if (scope.Owner is { } owner)
                return Hash(new JsonObject { ["kind"] = owner.Kind, ["condition"] = Value(owner.Condition), ["items"] = Value(owner.Items),
                    ["edge"] = ReferenceEquals(scope.Source, owner.Body) ? "body" : ReferenceEquals(scope.Source, owner.Otherwise) ? "otherwise" :
                        "branch:" + owner.Branches.IndexOf(scope.Source).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["requires"] = Value(owner.Requires), ["after"] = Strings(owner.DependsOn.Select(TaskIdentity).Order(StringComparer.Ordinal)),
                    ["parallel"] = owner.Parallel, ["maxItems"] = owner.MaxItems, ["maxConcurrency"] = owner.MaxConcurrency,
                    ["parent"] = scope.Parent is null ? null : ScopeIdentity(scope.Parent.Path, includeSequence) });
            var group = plan.Groups.SingleOrDefault(item => ReferenceEquals(item.Body, scope.Source));
            return group is null ? "scope" : "group:" + Hash(JsonSerializer.SerializeToNode(group.Inputs, PlanningJsonContext.Default.ListTaskInput));
        }

        internal string TaskIdentity(string id)
        {
            if (_identities.TryGetValue(id, out var identity)) return identity;
            if (!_symbols.Tasks.TryGetValue(id, out var entry)) return "unavailable_producer";
            if (!_visiting.Add(id)) return "cyclic_producer";
            var task = entry.Task;
            var node = PlanningConstruction.TaskNode(task).AsObject();
            node.Remove("id"); node.Remove("scopes");
            // A transform objective is executable interpreter input. Other task
            // objectives are planning labels and cannot reset this progress guard.
            if (task.Kind != "transform") node.Remove("objective");
            node["inputs"] = Outputs(task.Inputs); node["outputs"] = Outputs(task.Outputs);
            node["requires"] = Value(task.Requires); node["condition"] = Value(task.Condition); node["items"] = Value(task.Items);
            node["dependsOn"] = Strings(task.DependsOn.Select(TaskIdentity).Order(StringComparer.Ordinal));
            node["body"] = Scope(task.Body); node["otherwise"] = Scope(task.Otherwise);
            node["branches"] = new JsonArray(task.Branches.Select(scope => (JsonNode?)Scope(scope)).ToArray());
            node["owner"] = ScopeIdentity(entry.Scope.Path);
            if (task.Group is { } groupId && plan.Groups.SingleOrDefault(group => group.Id == groupId) is { } group)
                node["group"] = new JsonObject { ["inputs"] = JsonSerializer.SerializeToNode(group.Inputs, PlanningJsonContext.Default.ListTaskInput), ["body"] = Scope(group.Body) };
            _visiting.Remove(id);
            return _identities[id] = Hash(node);
        }

        private JsonObject? Scope(TaskScope? scope) => scope is null ? null : new()
        {
            ["tasks"] = Strings(EffectiveTasks(scope.Tasks).Where(Productive).Select(task => TaskIdentity(task.Id))),
            ["always"] = Strings(EffectiveTasks(scope.Always).Where(Productive).Select(task => TaskIdentity(task.Id))), ["outputs"] = Outputs(scope.Outputs)
        };

        private static IEnumerable<PlanTask> EffectiveTasks(IEnumerable<PlanTask> tasks)
        {
            foreach (var task in tasks)
                if (TransparentSequence(task))
                    foreach (var child in EffectiveTasks(task.Body!.Tasks)) yield return child;
                else yield return task;
        }

        private JsonArray Outputs(IEnumerable<TaskOutput> outputs) => new(outputs.Select(output => (JsonNode)new JsonObject
            { ["name"] = output.Name, ["value"] = Value(output.Value) }).ToArray());

        private JsonNode? Value(TaskValue? value)
        {
            if (value is null) return null;
            var node = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!.AsObject();
            if (value.Kind is "output" or "present" && value.Source is { } source) node["source"] = TaskIdentity(source);
            node["items"] = new JsonArray(value.Items.Select(Value).ToArray()); node["members"] = Outputs(value.Members);
            return node;
        }

        private static bool TransparentSequence(PlanTask task) => task.Kind == "sequence" && task.Requires is null &&
            task.DependsOn.Count == 0 && task.Inputs.Count == 0 && task.Outputs.Count == 0 &&
            task.Body is { Outputs.Count: 0, Always.Count: 0 };

        private static bool Productive(PlanTask task) => task.Kind is not ("sequence" or "conditional" or "parallel" or "foreach") ||
            task.Kind == "sequence" && !TransparentSequence(task) ||
            task.Body is { } body && (body.Outputs.Count > 0 || TaskPlanRevisions.Tasks(body).Any(Productive)) ||
            task.Otherwise is { } otherwise && (otherwise.Outputs.Count > 0 || TaskPlanRevisions.Tasks(otherwise).Any(Productive)) ||
            task.Branches.Any(branch => branch.Outputs.Count > 0 || TaskPlanRevisions.Tasks(branch).Any(Productive));
    }
}
