using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // Context and grammar share one displayed page; the compiler establishes
    // lexical availability. New fragment references retain ordinary TaskValue grammar.
    private static void ConstrainSelectionBindings(PlanningSession state, JsonObject page, JsonObject schema)
    {
        var plan = state.Plan ?? new TaskPlan { Inputs = state.Requirements!.Inputs!.ToList() };
        var symbols = new TaskPlanSymbols(plan);
        var offered = new Dictionary<string, HashSet<string?>>(StringComparer.Ordinal);
        void Collect(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Collect(child); return; }
            if (node is not JsonObject obj) return;
            var id = (obj["task"] ?? obj["id"])?.ToString();
            if (id is not null && symbols.Tasks.ContainsKey(id))
            {
                if (!offered.TryGetValue(id, out var ports)) offered[id] = ports = new(StringComparer.Ordinal);
                if (obj["wholeResult"]?.GetValue<bool>() == true) ports.Add(null);
                foreach (var port in obj["ports"] as JsonArray ?? [])
                    if (port is JsonValue value && value.TryGetValue<string>(out var name)) ports.Add(name);
                foreach (var output in obj["outputs"] as JsonArray ?? [])
                    if (output is JsonObject declaration && declaration["name"]?.ToString() is { } name) ports.Add(name);
            }
            foreach (var (_, child) in obj) Collect(child);
        }
        Collect(page["interfaces"]); Collect(page["evidence"]); Collect(page["requirements"]);
        // An exact contract port page has an operation identity, not a task ID.
        // Join it only to already displayed producers of that original operation.
        foreach (var (address, node) in page["evidence"] as JsonObject ?? [])
        {
            if (!address.StartsWith("port-page:", StringComparison.Ordinal) || node?["direction"]?.ToString() != "/output") continue;
            if (!JsonNode.DeepEquals(node, LoadContractEvidence(state, address, recordSchemaSelection: false)))
                PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The displayed original port page is stale.");
            foreach (var producer in offered.Where(pair => symbols.Tasks[pair.Key].Task.Kind == "operation" &&
                symbols.Tasks[pair.Key].Task.Operation == node!["id"]!.ToString()))
                foreach (var port in node!["items"]!.AsArray()) producer.Value.Add(port!["name"]!.ToString());
        }
        var definitions = new JsonObject(); schema["$defs"] = definitions;
        var serial = 0;
        var cached = new Dictionary<(string Scope, int Checkpoint), JsonObject>();
        JsonObject Imports(string scope, PlanningConstructionCheckpoint? owner = null)
        {
            if (cached.TryGetValue((scope, owner?.Sequence ?? 0), out var prior)) return prior.DeepClone().AsObject();
            var name = "selected_import_" + serial++;
            var values = new List<TaskValue>();
            foreach (var (id, ports) in offered.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (owner is not null && (!CheckpointProducerCurrent(state, owner, id) || owner.InvalidatedTaskIds.Contains(id) ||
                    !owner.TaskIds.Contains(id) && !owner.ClosedScopes.Any(path => symbols.Scopes.Any(site => site.Path == path && site.Owner?.Id == id)))) continue;
                values.AddRange(ports.Order(StringComparer.Ordinal).Select(port => new TaskValue { Kind = "output", Source = id, Port = port }));
                values.Add(new() { Kind = "present", Source = id });
            }
            if (owner is null)
            {
                values.AddRange((ScopeGroup(state, scope)?.Inputs ?? plan.Inputs).Select(input => new TaskValue { Kind = "input", Source = input.Name }));
                values.AddRange(plan.Choices.Where(choice => choice.Alternatives.Any(alternative => alternative.Id == (choice.Selected ?? choice.Recommended)))
                    .Select(choice => new TaskValue { Kind = "choice", Source = choice.Id }));
                for (var boundary = symbols.Scopes.SingleOrDefault(boundary => boundary.Path == scope); boundary is not null; boundary = boundary.Parent)
                    if (boundary.Owner?.Kind == "foreach")
                    { values.Add(new() { Kind = "item" }); values.Add(new() { Kind = "index" }); break; }
            }
            if (values.Count == 0) return PlanningSchemas.Array(PlanningSchemas.String(), 0, 0);
            var probe = PlanningConstruction.Clone(plan);
            foreach (var choice in probe.Choices.Where(choice => choice.Selected is null)) choice.Selected = choice.Recommended;
            var verified = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog ?? new(), state.Request,
                state.Construction!.OpenScopes.ToHashSet(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<TaskValue>> { [scope] = values });
            var available = verified.Interfaces.GetValueOrDefault(scope) ?? [];
            var branches = new JsonArray();
            foreach (var group in available.Select(item => item.Value).Where(value => value.Kind == "output").GroupBy(value => value.Source))
            {
                var names = group.Where(value => value.Port is not null).Select(value => value.Port!).Distinct().ToArray();
                var port = names.Length == 0 ? PlanningSchemas.Type("null") : group.Any(value => value.Port is null)
                    ? PlanningSchemas.Nullable(PlanningSchemas.Enum(names)) : PlanningSchemas.Enum(names);
                branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("output")),
                    ("source", PlanningSchemas.Enum(group.Key!)), ("port", port)));
            }
            foreach (var kind in new[] { "present", "input", "choice" })
            {
                var sources = available.Select(item => item.Value).Where(value => value.Kind == kind).Select(value => value.Source!).Distinct().ToArray();
                if (sources.Length > 0) branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum(kind)), ("source", PlanningSchemas.Enum(sources))));
            }
            foreach (var kind in new[] { "item", "index" })
                if (available.Any(item => item.Value.Kind == kind)) branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum(kind))));
            if (branches.Count == 0) return PlanningSchemas.Array(PlanningSchemas.String(), 0, 0);
            branches.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("field")), ("port", Text(96)),
                ("items", PlanningSchemas.Array(PlanningSchemas.Ref(name), 1, 1))));
            definitions[name] = new JsonObject { ["anyOf"] = branches };
            var result = PlanningSchemas.Array(PlanningSchemas.Ref(name), 0, 8);
            cached[(scope, owner?.Sequence ?? 0)] = result;
            return result.DeepClone().AsObject();
        }
        var steps = schema["properties"]!["step"]!["anyOf"]?.AsArray();
        var variants = steps?.FirstOrDefault(node => node?["anyOf"] is JsonArray)?["anyOf"]?.AsArray();
        if (variants is not null)
        {
            var narrowed = new JsonArray();
            foreach (var branch in variants)
                foreach (var scope in branch!["properties"]!["scope"]!["enum"]!.AsArray().Select(item => item!.ToString()))
                {
                    var selected = branch.DeepClone(); selected["properties"]!["scope"] = PlanningSchemas.Enum(scope);
                    selected["properties"]!["inputs"] = Imports(scope); narrowed.Add(selected);
                }
            var container = steps!.First(node => node?["anyOf"] is JsonArray)!;
            container["anyOf"] = narrowed;
        }
        if (ContinuationContext(state) is { } continuation)
        {
            var choices = new JsonArray();
            if (continuation["canAttemptResume"]!.GetValue<bool>())
                foreach (var entry in continuation["acceptedCheckpoints"]!.AsArray())
                {
                    var number = entry!["sequence"]!.GetValue<int>();
                    var bindings = Imports(continuation["scope"]!.ToString(), state.Construction!.Checkpoints[number - 1]);
                    if (bindings["items"]?["$ref"] is not { } reference) continue;
                    var checkpoint = PlanningSchemas.Type("integer"); checkpoint["enum"] = new JsonArray(number);
                    choices.Add((JsonNode)PlanningSchemas.Object(("continuationId", PlanningSchemas.Enum(continuation["id"]!.ToString())),
                        ("producerCheckpoint", checkpoint), ("binding", new JsonObject { ["$ref"] = reference.DeepClone() })));
                }
            schema["properties"]!["resumeStep"] = choices.Count == 0 ? PlanningSchemas.Type("null") :
                PlanningSchemas.Nullable(new JsonObject { ["anyOf"] = choices });
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("resumeStep"));
        }
    }
}
