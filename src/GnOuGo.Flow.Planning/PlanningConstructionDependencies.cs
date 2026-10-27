using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// A transient compiler-backed projection, retained only inside an existing review
// basis. It proves conservation of deterministic behavior, never source locality,
// successful execution, requirement completion, or authority to edit accepted work.
internal static class PlanningConstructionDependencies
{
    internal static JsonObject? Capture(PlanningSession state, TaskPlan plan, JsonObject subject,
        IReadOnlyCollection<string>? openScopes = null)
    {
        if (!PlanningCompactConstruction.UsesIncrementalReview(state) || state.Catalog is null || state.Construction is null)
            return null;
        var open = OpenScopes(state, plan, openScopes);
        if (open is null) return null;
        var symbols = new TaskPlanSymbols(plan);
        if (symbols.InvalidIds.Count != 0 || TaskPlanCompiler.ValidateConstruction(plan, state.Catalog, state.Request, open).Diagnostics.Count != 0)
            return null;
        var projection = new Projection(plan, symbols, open);
        // Historical proofs exclude unknown behavior everywhere. Fresh mixed
        // proofs keep it as an exact prefix, never infer effect independence.
        var pure = projection.PurePlan();
        JsonArray? contracts = null;
        if (!pure && (state.Construction.Compact?.PresentationVersion is not (10 or 11) ||
            !projection.ExactOpaquePrefix() || (contracts = OperationContracts(state, plan)) is null)) return null;
        if (!projection.Subject(subject)) return null;
        var result = new JsonObject
        {
            ["version"] = pure ? 1 : 2, ["class"] = pure ? "deterministic_behavior_not_completion" : "deterministic_behavior_with_exact_opaque_prefix",
            ["subject"] = subject.DeepClone(), ["authority"] = Authority(state),
            ["inputs"] = Hash(projection.PlanNode["inputs"]!), ["choices"] = Hash(projection.PlanNode["choices"]!),
            ["projection"] = projection.Result()
        };
        if (!pure) result["opaquePrefix"] = projection.OpaquePrefix(contracts!);
        return result;
    }

    internal static bool Preserved(PlanningSession state, TaskPlan plan, JsonObject proof,
        IReadOnlyCollection<string>? openScopes = null) => proof["subject"] is JsonObject subject &&
        Capture(state, plan, subject, openScopes) is { } current && Conserves(proof, current);

    // Callers bind both projections to their genuine candidate/basis/receipt.
    // There is deliberately no candidate hash here: unrelated trailing pure work
    // may change it, and still requires its own interaction/completion review.
    internal static bool Conserves(JsonObject origin, JsonObject target) =>
        BehaviorProof(origin) &&
        JsonNode.DeepEquals(origin, target);

    internal static bool BehaviorProof(JsonObject proof) =>
        proof["version"]?.ToString() == "1" && proof["class"]?.ToString() == "deterministic_behavior_not_completion" ||
        proof["version"]?.ToString() == "2" && proof["class"]?.ToString() == "deterministic_behavior_with_exact_opaque_prefix";

    // Only a separately reviewed transition may consume this exact-state proof.
    // External effects and inference are permitted here because no plan change is
    // ignored; this class must never establish source locality or independent reuse.
    internal static JsonObject? CaptureExactTransition(PlanningSession state, TaskPlan plan,
        IReadOnlyCollection<string>? openScopes = null)
    {
        if (!PlanningCompactConstruction.UsesIncrementalReview(state) || state.Catalog is null || state.Construction is null)
            return null;
        var open = OpenScopes(state, plan, openScopes);
        if (open is null || new TaskPlanSymbols(plan).InvalidIds.Count != 0 ||
            TaskPlanCompiler.ValidateConstruction(plan, state.Catalog, state.Request, open).Diagnostics.Count != 0) return null;
        var contracts = OperationContracts(state, plan);
        if (contracts is null) return null;
        return new JsonObject
        {
            ["version"] = 1, ["class"] = "exact_transition_not_independence", ["plan"] = PlanningConstruction.Hash(plan),
            ["openScopes"] = Strings(open.Order(StringComparer.Ordinal)), ["authority"] = Authority(state), ["contracts"] = contracts
        };
    }

    private static JsonArray? OperationContracts(PlanningSession state, TaskPlan plan)
    {
        var contracts = new JsonArray();
        foreach (var operation in TaskPlanRevisions.Tasks(plan).Where(task => task.Kind == "operation")
            .Select(task => task.Operation).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var matches = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved)
                .Where(capability => TaskOperations.Describe(capability).Id == operation).ToArray();
            // Do not hide conflicting normative descriptions behind identity dedup.
            var hashes = matches.Select(capability => Hash(PlanningConstruction.Node(capability,
                PlanningJsonContext.Default.PlanningCapability)!)).Distinct(StringComparer.Ordinal).ToArray();
            if (hashes.Length != 1 || matches.Select(capability => (capability.Id, capability.Version)).Distinct().Count() != 1 ||
                !state.Catalog.AllowedStepTypes.Contains(matches[0].StepType) ||
                state.Catalog.Policy.DeniedCapabilityIds.Contains(matches[0].Id) || TaskOperations.Validate(matches[0]).Count != 0) return null;
            contracts.Add((JsonNode)new JsonObject { ["id"] = operation, ["version"] = matches[0].Version, ["hash"] = hashes[0] });
        }
        return contracts;
    }

    internal static bool PreservedExactTransition(PlanningSession state, TaskPlan plan, JsonObject proof,
        IReadOnlyCollection<string>? openScopes = null) => proof["version"]?.ToString() == "1" &&
        proof["class"]?.ToString() == "exact_transition_not_independence" &&
        CaptureExactTransition(state, plan, openScopes) is { } current && JsonNode.DeepEquals(proof, current);

    private static HashSet<string>? OpenScopes(PlanningSession state, TaskPlan plan, IReadOnlyCollection<string>? supplied)
    {
        if (supplied is not null) return new(supplied, StringComparer.Ordinal);
        var hash = PlanningConstruction.Hash(plan);
        if (state.Construction!.Candidate is { } candidate && candidate.Hash == hash && PlanningConstruction.Hash(candidate.Plan) == hash)
            return new(candidate.OpenScopes, StringComparer.Ordinal);
        return state.Plan is not null && PlanningConstruction.Hash(state.Plan) == hash
            ? new(state.Construction.OpenScopes, StringComparer.Ordinal) : null;
    }

    private static string Authority(PlanningSession state) => Hash(new JsonObject
    {
        ["tenant"] = state.Request.TenantId, ["session"] = state.Request.SessionId,
        ["prompt"] = state.Request.Prompt, ["options"] = state.Request.Options.DeepClone(),
        ["requestPolicy"] = PlanningConstruction.Node(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["catalogPolicy"] = PlanningConstruction.Node(state.Catalog!.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["declaredInputs"] = PlanningConstruction.Node(state.Requirements, PlanningJsonContext.Default.PlanningRequirements)?["inputs"]?.DeepClone(),
        ["declaredOutputs"] = PlanningConstruction.Node(state.Requirements, PlanningJsonContext.Default.PlanningRequirements)?["outputs"]?.DeepClone(),
        ["stepContracts"] = state.Catalog.StepContracts.DeepClone(), ["allowedSteps"] = Strings(state.Catalog.AllowedStepTypes),
        ["originals"] = new JsonArray(state.Construction!.Compact!.Sources.Select(source => (JsonNode)new JsonObject
        {
            ["id"] = source.Id, ["version"] = source.Version, ["kind"] = source.Kind, ["text"] = source.Text,
            ["continuationOf"] = source.ContinuationOf, ["interfaceKind"] = source.InterfaceKind
        }).ToArray())
    });

    private static string Hash(JsonNode node) => PlanningGraphCompiler.Fingerprint(node.ToJsonString());
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private sealed class Projection(TaskPlan plan, TaskPlanSymbols symbols, HashSet<string> open)
    {
        internal JsonObject PlanNode { get; } = PlanningConstruction.Node(plan, PlanningJsonContext.Default.TaskPlan)!.AsObject();
        private readonly Dictionary<string, JsonObject> _tasks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> _ancestors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> _scopes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pureGroups = new(StringComparer.Ordinal);
        private readonly HashSet<string> _checkingGroups = new(StringComparer.Ordinal);
        private readonly HashSet<string> _addingGroups = new(StringComparer.Ordinal);
        private bool _valid = true;
        private JsonNode? _anchor;
        private readonly HashSet<string> _opaque = new(StringComparer.Ordinal);

        internal bool PurePlan() => PureScope(plan.Root) && plan.Groups.All(group => PureGroup(group.Id));

        internal bool ExactOpaquePrefix()
        {
            // Only sequential root leaves may be opaque. Nothing can be inserted
            // ahead of them or overlap them through a branch, loop or cleanup.
            var prefix = plan.Root.Tasks.TakeWhile(task => task.Kind is "operation" or "transform").ToArray();
            if (prefix.Length == 0 || prefix.Any(task => task.Body is not null || task.Otherwise is not null || task.Branches.Count != 0 ||
                    task.Requires is not null || task.Condition is not null || task.Each is not null || task.Parallel) ||
                !plan.Root.Tasks.Skip(prefix.Length).All(PureTask) || !plan.Root.Always.All(PureTask) ||
                !plan.Root.Outputs.All(output => PureValue(output.Value)) || !plan.Groups.All(group => PureGroup(group.Id))) return false;
            _opaque.UnionWith(prefix.Select(task => task.Id));
            return true;
        }

        internal JsonObject OpaquePrefix(JsonArray contracts) => new()
        {
            ["tasks"] = new JsonArray(plan.Root.Tasks.Take(_opaque.Count).Select((task, index) => (JsonNode)new JsonObject
                { ["id"] = task.Id, ["position"] = index, ["hash"] = Hash(PlanningConstruction.Node(task, PlanningJsonContext.Default.PlanTask)!) }).ToArray()),
            ["rootOpen"] = open.Contains("/root"), ["contracts"] = contracts,
            ["cleanup"] = Hash(PlanNode["root"]!["always"]!)
        };

        private bool PureScope(TaskScope scope) => scope.Tasks.Concat(scope.Always).All(PureTask) &&
            scope.Outputs.All(output => PureValue(output.Value));

        private bool PureTask(PlanTask task) => task.Kind is "value" or "sequence" or "conditional" or "parallel" or "foreach" or "call" &&
            task.Operation is null && task.Mode is null && task.Each is null && task.ResultType is null &&
            TaskPlanCompiler.Values(task).All(PureValue) &&
            (task.Body is null || PureScope(task.Body)) && (task.Otherwise is null || PureScope(task.Otherwise)) &&
            task.Branches.All(PureScope) && (task.Kind != "call" || task.Group is { } group && PureGroup(group));

        private bool PureGroup(string id)
        {
            if (_pureGroups.Contains(id)) return true;
            if (!_checkingGroups.Add(id)) return false;
            var matches = plan.Groups.Where(group => group.Id == id).ToArray();
            var result = matches.Length == 1 && matches[0].Inputs.All(input => input.Default is null || PureValue(input.Default)) &&
                PureScope(matches[0].Body);
            _checkingGroups.Remove(id);
            if (result) _pureGroups.Add(id);
            return result;
        }

        private static bool PureValue(TaskValue value) => TaskPlanCompiler.Values(value).All(item => item.Kind is
            "null" or "string" or "number" or "boolean" or "object" or "array" or "input" or "output" or "present" or
            "item" or "index" or "choice" or "arithmetic" or "predicate" or "json" or "flatten" or "lookup" or "field");

        internal bool Subject(JsonObject subject)
        {
            foreach (var opaque in plan.Root.Tasks.Take(_opaque.Count)) AddTask(opaque.Id);
            var kind = subject["kind"]?.ToString();
            switch (kind)
            {
                case "output" when subject.Count == 2 && Text(subject, "name") is { } name:
                    var outputs = plan.Root.Outputs.Where(output => output.Name == name).ToArray();
                    if (outputs.Length != 1) return false;
                    _anchor = PlanningConstruction.Node(outputs[0].Value, PlanningJsonContext.Default.TaskValue);
                    Values(outputs[0].Value); Context(symbols.Scopes.Single(scope => scope.Path == "/root"));
                    break;
                case "task" when subject.Count == 2 && Text(subject, "id") is { } id:
                    if (_opaque.Contains(id)) return false;
                    AddTask(id); break;
                case "group" when subject.Count == 2 && Text(subject, "id") is { } group:
                    AddGroup(group); break;
                case "scope" when subject.Count == 2 && Text(subject, "path") is { } path:
                    var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
                    if (scope is null) return false;
                    ClosedScope(scope); break;
                default: return false;
            }
            return _valid;
        }

        private static string? Text(JsonObject node, string key) => node[key] is JsonValue value &&
            value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

        private void Values(TaskValue value, bool opaqueContext = false)
        {
            foreach (var item in TaskPlanCompiler.Values(value))
                if (item.Kind is "output" or "present")
                {
                    if (item.Source is null || !opaqueContext && _opaque.Contains(item.Source)) _valid = false;
                    else AddTask(item.Source);
                }
        }

        private void AddTask(string id)
        {
            if (_tasks.ContainsKey(id) || !_valid) return;
            if (!symbols.Tasks.TryGetValue(id, out var site)) { _valid = false; return; }
            var task = site.Task; var scope = site.Scope; var phase = symbols.Phase(id);
            var siblings = phase == "always" ? scope.Source.Always : scope.Source.Tasks;
            _tasks[id] = new JsonObject
            {
                ["id"] = id, ["scope"] = scope.Path, ["phase"] = phase, ["position"] = siblings.IndexOf(task),
                ["hash"] = Hash(PlanningConstruction.Node(task, PlanningJsonContext.Default.PlanTask)!)
            };
            // An opaque predecessor is an exact order/failure dependency, never
            // a source of deterministic input values, guards or presence facts.
            foreach (var value in TaskPlanCompiler.Values(task)) Values(value, _opaque.Contains(id));
            foreach (var dependency in task.DependsOn) AddTask(dependency);
            // All preceding executions can prevent this subject from being reached.
            // Their literal/objective/guard/failure behavior is therefore conserved.
            foreach (var prior in siblings.TakeWhile(sibling => sibling.Id != id)) AddTask(prior.Id);
            if (phase == "always") foreach (var prior in scope.Source.Tasks) AddTask(prior.Id);
            Context(scope);
            foreach (var child in symbols.Scopes.Where(child => child.Owner == task)) ClosedScope(child);
            if (task.Kind == "call" && task.Group is { } group) AddGroup(group);
        }

        private void Context(TaskPlanSymbols.Scope scope)
        {
            if (_scopes.ContainsKey(scope.Path) || !_valid) return;
            _scopes[scope.Path] = new JsonObject
            {
                ["path"] = scope.Path, ["parent"] = scope.Parent?.Path, ["owner"] = scope.Owner?.Id,
                ["cleanup"] = Strings(scope.Source.Always.Select(task => task.Id))
            };
            foreach (var cleanup in scope.Source.Always) AddTask(cleanup.Id);
            if (scope.Owner is { } owner) Ancestor(owner, scope);
            if (scope.Parent is { } parent) Context(parent);
            else if (scope.Path != "/root")
            {
                var group = plan.Groups.SingleOrDefault(group => "/groups/" + group.Id + "/body" == scope.Path);
                if (group is null) _valid = false;
                else AddGroup(group.Id);
            }
        }

        private void Ancestor(PlanTask owner, TaskPlanSymbols.Scope child)
        {
            if (_ancestors.ContainsKey(child.Path) || !_valid) return;
            var site = symbols.Tasks[owner.Id];
            var phase = symbols.Phase(owner.Id);
            var siblings = phase == "always" ? site.Scope.Source.Always : site.Scope.Source.Tasks;
            var header = PlanningConstruction.Node(owner, PlanningJsonContext.Default.PlanTask)!.AsObject();
            // The entered branch/body is an unfinished boundary, not an available
            // result of its owner. Preserve its owner controls and every other
            // branch exactly without requiring this ancestor body to be closed.
            // The selected producer's own children still go through ClosedScope.
            if (ReferenceEquals(owner.Body, child.Source)) header["body"] = null;
            else if (ReferenceEquals(owner.Otherwise, child.Source)) header["otherwise"] = null;
            else
            {
                var index = owner.Branches.IndexOf(child.Source);
                if (index < 0) { _valid = false; return; }
                header["branches"]![index] = null;
            }
            _ancestors[child.Path] = new JsonObject
            {
                ["id"] = owner.Id, ["scope"] = site.Scope.Path, ["phase"] = phase,
                ["position"] = siblings.IndexOf(owner), ["context"] = child.Path, ["hash"] = Hash(header)
            };
            foreach (var value in TaskPlanCompiler.Values(owner)) Values(value);
            foreach (var dependency in owner.DependsOn) AddTask(dependency);
            foreach (var prior in siblings.TakeWhile(sibling => sibling.Id != owner.Id)) AddTask(prior.Id);
            if (phase == "always") foreach (var prior in site.Scope.Source.Tasks) AddTask(prior.Id);
            Context(site.Scope);
        }

        private void ClosedScope(TaskPlanSymbols.Scope scope)
        {
            if (!_valid) return;
            if (open.Contains(scope.Path)) { _valid = false; return; }
            Context(scope);
            _scopes[scope.Path]["closed"] = true;
            // Entire closed boundaries include exports, normal/cleanup order and
            // every child, including behavior not selected by a caller's port.
            _scopes[scope.Path]["hash"] = Hash(PlanningConstruction.Node(scope.Source, PlanningJsonContext.Default.TaskScope)!);
            foreach (var task in scope.Source.Tasks.Concat(scope.Source.Always)) AddTask(task.Id);
            foreach (var output in scope.Source.Outputs) Values(output.Value);
        }

        private void AddGroup(string id)
        {
            if (_groups.ContainsKey(id) || !_valid || !_addingGroups.Add(id)) return;
            var groups = plan.Groups.Where(group => group.Id == id).ToArray();
            var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == "/groups/" + id + "/body");
            if (groups.Length != 1 || scope is null || !PureGroup(id)) { _valid = false; return; }
            var node = PlanNode["groups"]!.AsArray().Single(node => node?["id"]?.ToString() == id)!;
            _groups[id] = Hash(node);
            ClosedScope(scope);
            _addingGroups.Remove(id);
        }

        internal JsonObject Result() => new()
        {
            ["anchor"] = _anchor?.DeepClone(),
            ["tasks"] = new JsonArray(_tasks.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)
                .Concat(_ancestors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
                .Select(row => (JsonNode)row).ToArray()),
            ["groups"] = new JsonObject(_groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
            ["scopes"] = new JsonArray(_scopes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (JsonNode)pair.Value).ToArray())
        };
    }
}
