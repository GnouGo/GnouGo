using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

// Private wire types. Payload types are fixed by each issued slot's strict schema,
// then deserialized into the existing TaskPlan contracts. This is not a plan IR.
internal sealed class PlanningRepairResponse
{
    public List<PlanningDiscoveryRequest>? DiscoveryRequests { get; set; }
    public RepairPatch? Patch { get; set; }
    public List<PlanningQuestion>? Clarifications { get; set; }
}
internal sealed class RepairPatch { public List<RepairEdit> Edits { get; set; } = []; }
internal sealed class RepairEdit
{
    public string Slot { get; set; } = "";
    public string Action { get; set; } = "";
    public JsonNode? Value { get; set; }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanningRepairResponse))]
[JsonSerializable(typeof(RepairPatch))]
internal partial class RepairJsonContext : JsonSerializerContext;

internal static class PlanningRepairPatch
{
    internal sealed record Slot(string Id, string Location, string Kind, JsonObject ValueSchema, string[] Actions);
    internal static bool Active(PlanningSession state) => state.Plan is not null && state.RevisionScope.Count > 0 && (state.EditablePaths is not null || PlanningModelCalls.IsRepair(state));
    internal static bool Issued(JsonObject? schema) => schema?["properties"] is JsonObject properties && properties.ContainsKey("patch");

    // Display paths are resolved through unambiguous semantic identities, never
    // interpreted as model-supplied JSON pointers or arbitrary array indexes.
    internal sealed record Site(JsonNode? Node, JsonObject? Owner, string? Key, JsonArray? List, int Index)
    {
        internal void Set(JsonNode? value) { if (Owner is not null) Owner[Key!] = value; else List![Index] = value; }
        internal void Remove() { if (List is not null) List.RemoveAt(Index); else Owner!.Remove(Key!); }
    }
    internal static Dictionary<string, Site> Index(JsonNode tree)
    {
        var found = new Dictionary<string, Site>(StringComparer.Ordinal); var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Site site, string path)
        {
            if (!found.TryAdd(path, site)) ambiguous.Add(path);
            if (site.Node is JsonObject obj)
                foreach (var (key, value) in obj) Visit(new(value, obj, key, null, -1), path + "/" + key);
            else if (site.Node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Visit(new(array[i], null, null, array, i), TaskPlanRevisions.Element(path, array[i], i));
        }
        Visit(new(tree, null, null, null, -1), "");
        foreach (var path in ambiguous) found.Remove(path);
        return found;
    }

    internal static Site? FindSite(Dictionary<string, Site> index, string path)
    {
        if (index.TryGetValue(path, out var site)) return site;
        var split = path.LastIndexOf('/');
        return split > 0 && index.GetValueOrDefault(path[..split])?.Node is JsonObject parent && !parent.ContainsKey(path[(split + 1)..])
            ? new(null, parent, path[(split + 1)..], null, -1) : null;
    }

    internal static IReadOnlyList<Slot> Slots(PlanningSession state, JsonObject definitions, bool structural = true, bool scopedDependencies = true, int version = 8)
    {
        var plan = state.Plan!; var symbols = new TaskPlanSymbols(plan);
        var index = Index(JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!);
        var types = TaskPlanRevisions.ProducerConstraintSlots(plan); var slots = new List<Slot>();
        var explicitRevision = version >= 8 && state.EditablePaths is not null;
        var exportRepair = TaskPlanRevisions.Exports(plan, explicitRevision ? [] : state.RevisionScope);
        var sources = TaskPlanCompiler.RepairSources(plan, state.Catalog!);
        var structuralSlots = structural && !explicitRevision ? PlanningStructuralRepair.Slots(state, definitions) : [];
        foreach (var path in state.RevisionScope.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (path.EndsWith("/kind", StringComparison.Ordinal) && structuralSlots.Any(s => s.Kind == "task" && path == s.Location + "/kind")) continue;
            var kind = "value"; var schema = PlanningSchemas.Ref("value"); var actions = new List<string>();
            var site = FindSite(index, path);
            if (explicitRevision)
            {
                kind = symbols.Values.ContainsKey(path) ? "value" : "field";
                schema = path.Split('/') is ["", "tasks", _, "inputs"]
                    ? PlanningSchemas.Array(PlanningSchemas.Ref("output")) : PlanningSchemas.Ref("value");
                actions.Add("replace");
            }
            else if (exportRepair.Additions.TryGetValue(path, out var export))
            {
                kind = "binding"; actions.Add("add");
                if (export.Value is { } forwarding) schema = Exact(forwarding, definitions);
                else
                {
                    var contract = sources.GetValueOrDefault(export.ProducerScope.Path + "/outputs")?.FirstOrDefault(s => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(s.Value, PlanningJsonContext.Default.TaskValue), JsonSerializer.SerializeToNode(export.Producer, PlanningJsonContext.Default.TaskValue)))?.Schema;
                    if (contract is null) throw new WorkflowRuntimeException("REPAIR_SCOPE_INVALID", "The missing conditional export has no authoritative producer contract.");
                    schema = RepairValue(contract, export.Scope.Path + "/outputs");
                }
            }
            else if (exportRepair.Consumers.TryGetValue(path, out var reference) == true)
            { actions.Add("replace"); schema = Exact(reference, definitions); }
            else if (version >= 8 && TaskPlanRevisions.ReferenceRepairs(state, path) is { } references)
            {
                if (references.Count == 0) throw new WorkflowRuntimeException("REVISION_REQUIRED", "The referenced producer has no compatible visible result; explicitly revise the dependency.", details: new JsonObject { ["location"] = path });
                kind = "reference"; actions.Add("replace");
                schema = references.Count == 1 ? Exact(references[0], definitions) : new JsonObject { ["anyOf"] = new JsonArray(references.Select(v => (JsonNode)Exact(v, definitions)).ToArray()) };
            }
            else if (symbols.Values.ContainsKey(path) && site is not null)
            {
                var owned = TaskPlanRevisions.OwnedInput(symbols, path, state.Catalog);
                if (owned is not null)
                {
                    var parts = path.Split('/'); var task = symbols.Tasks[parts[2]].Task;
                    if (TaskPlanRevisions.RemovableInput(task, parts[4], state.Catalog)) actions.Add("remove");
                    else if (PlanningCapabilityArguments.WithoutOwned(owned, parts[4], symbols.Values[path].Value) is not null) actions.Add("remove_owned");
                }
                else
                {
                    actions.Add("replace");
                    if (path.Split('/') is ["", "tasks", var id, "inputs", var name] && symbols.Tasks.TryGetValue(id, out var task))
                    {
                        var contracts = state.Catalog!.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Task.Operation).ToArray();
                        if (contracts.Length == 1 && TaskOperations.Describe(contracts[0]).Inputs.SingleOrDefault(p => p.Name == name) is { } port)
                            schema = RepairValue(port.Schema, path,
                                literalOnly: TaskPlanCompiler.LiteralScopeInput(contracts[0].StepType, port.Path[0]),
                                workspace: TaskPlanCompiler.FixedWorkspaceInput(contracts[0].StepType, port.Path[0]));
                        if (TaskPlanRevisions.RemovableInput(task.Task, name, state.Catalog)) actions.Add("remove");
                    }
                }
            }
            else if (types.Contains(path) && site is not null)
            {
                var leaf = path.Split('/')[^1]; kind = "type";
                schema = leaf switch
                {
                    "nullable" or "required" => PlanningSchemas.Type("boolean"),
                    "enum" => PlanningSchemas.Array(PlanningSchemas.String(), 1, 256),
                    "kind" => PlanningSchemas.Enum("string", "integer", "number", "boolean", "array", "object"),
                    "default" => PlanningSchemas.Nullable(PlanningSchemas.Ref("literal")),
                    "fields" => PlanningSchemas.Array(PlanningSchemas.Ref("resultField"), 1),
                    "resultType" => PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("fields", PlanningSchemas.Array(PlanningSchemas.Ref("resultField"), 1))),
                    _ => PlanningSchemas.Ref("resultType")
                };
                actions.Add("replace");
            }
            else if (site?.Node is JsonObject declaration && declaration.ContainsKey("name") && declaration.ContainsKey("required"))
            { kind = "input"; schema = FixedName(definitions["input"]!.DeepClone().AsObject(), "name", declaration["name"]!.ToString()); actions.Add("replace"); }
            else if (path.Split('/') is ["", "choices", var choice] && site?.Node is JsonObject)
            { kind = "choice"; schema = FixedName(definitions["choice"]!.DeepClone().AsObject(), "id", choice); actions.Add("replace"); }
            else if (symbols.Scopes.Any(s => path == s.Path + "/outputs") && site?.Node is JsonArray)
            { kind = "exports"; schema = PlanningSchemas.Array(PlanningSchemas.Ref("output"), 1); actions.Add("add"); }
            else if (path.Split('/') is ["", "tasks", var id, "inputs", var name] && symbols.Tasks.TryGetValue(id, out var task) && task.Task.Inputs.All(i => i.Name != name) && !name.Contains('/'))
            { kind = "binding"; actions.Add("add"); }
            else if (symbols.Scopes.Any(s => s.Owner?.Kind == "conditional" && path.StartsWith(s.Path + "/outputs/", StringComparison.Ordinal) &&
                s.Source.Outputs.All(o => path != s.Path + "/outputs/" + o.Name)) && site is null)
            { kind = "binding"; actions.Add("add"); }
            else if (path.Split('/') is ["", "tasks", var ownerId, var field] && symbols.Tasks.ContainsKey(ownerId) && site is not null)
            {
                kind = "field";
                schema = field switch
                {
                    "objective" => PlanningSchemas.Ref("goal"),
                    "operation" => PlanningSchemas.Enum(state.Catalog!.Capabilities.Concat(state.Discovery.Resolved)
                        .Where(c => state.Catalog.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id) && TaskOperations.Validate(c).Count == 0)
                        .Select(c => TaskOperations.Describe(c).Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                    "group" => PlanningSchemas.Enum(plan.Groups.Select(g => g.Id).ToArray()),
                    "dependsOn" when scopedDependencies => symbols.DependencyTargets(ownerId) is { Count: > 0 } targets
                        ? PlanningSchemas.Array(PlanningSchemas.Enum(targets.ToArray()), 0, targets.Count)
                        : PlanningSchemas.Array(PlanningSchemas.Ref("id"), 0, 0),
                    "dependsOn" => PlanningSchemas.Ref("identities"),
                    "maxItems" => PlanningSchemas.Integer(1, 10000), "maxConcurrency" => PlanningSchemas.Integer(1, 100),
                    _ => schema
                };
                if (field is "objective" or "operation" or "group" or "dependsOn" or "maxItems" or "maxConcurrency" or "condition" or "items") actions.Add("replace");
            }
            if (actions.Count == 0 || schema["enum"] is JsonArray { Count: 0 })
                throw new WorkflowRuntimeException("REPAIR_SCOPE_INVALID", "The diagnosed repair slot cannot be resolved safely. Explicitly revise the requirements; no broader repair was authorized.",
                    details: new JsonObject { ["location"] = path });
            slots.Add(new("s" + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), path, kind, schema, actions.ToArray()));
        }
        foreach (var slot in structuralSlots)
        {
            // Factor the payload outside the patch envelope without weakening its contract.
            var name = "repairStructural" + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            definitions[name] = slot.ValueSchema.DeepClone();
            slots.Add(slot with { ValueSchema = PlanningSchemas.Ref(name) });
        }
        return slots.Select((s, i) => s with { Id = "s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();

        JsonObject RepairValue(JsonObject contract, string location, bool literalOnly = false, bool workspace = false)
        {
            var literal = PlanningBindingSchemas.For(contract, definitions, literalOnly: true);
            var alternatives = new JsonArray(literal);
            if (!literalOnly)
                foreach (var source in sources.GetValueOrDefault(location) ?? [])
                    if ((!workspace || source.Value.Kind == "output") && PlanningGraphValidation.TypesFit(source.Schema, contract))
                        alternatives.Add((JsonNode)Exact(source.Value, definitions));
            if (!literalOnly && !workspace && (contract["type"]?.ToString() == "string" || contract["type"] is JsonArray types && types.Any(t => t?.ToString() == "string")) && symbols.Values.TryGetValue(location, out var original))
                alternatives.Add((JsonNode)Exact(new() { Kind = "json", Items = [original.Value] }, definitions));
            return alternatives.Count == 1 ? literal.DeepClone().AsObject() : new() { ["anyOf"] = alternatives };
        }
    }

    private static JsonObject FixedName(JsonObject schema, string property, string value)
    {
        if (schema["anyOf"] is JsonArray alternatives)
            foreach (var child in alternatives.OfType<JsonObject>()) FixedName(child, property, value);
        else schema["properties"]![property] = PlanningSchemas.Enum(value);
        return schema;
    }

    internal static JsonObject Schema(PlanningSession state, JsonObject template, int version = 8)
    {
        var definitions = template["$defs"]!.DeepClone().AsObject();
        var slots = Slots(state, definitions, version: version); var edits = new JsonArray();
        foreach (var slot in slots)
            foreach (var action in slot.Actions)
                edits.Add((JsonNode)(action is "remove" or "remove_owned" or "remove_forwarder"
                    ? PlanningSchemas.Object(("slot", PlanningSchemas.Enum(slot.Id)), ("action", PlanningSchemas.Enum(action)))
                    : PlanningSchemas.Object(("slot", PlanningSchemas.Enum(slot.Id)), ("action", PlanningSchemas.Enum(action)), ("value", slot.ValueSchema.DeepClone().AsObject()))));
        var schema = PlanningSchemas.Object(("discoveryRequests", template["properties"]!["discoveryRequests"]!.DeepClone().AsObject()),
            ("patch", PlanningSchemas.Nullable(PlanningSchemas.Object(("edits", PlanningSchemas.Array(new JsonObject { ["anyOf"] = edits }, 0, slots.Count))))));
        if (template["properties"]?["clarifications"] is not null)
        {
            schema["properties"]!["clarifications"] = PlanningSchemas.Nullable(PlanningSchemas.Clarifications());
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("clarifications"));
        }
        schema["$defs"] = definitions;
        // Unreachable task/plan definitions must not leak back into repair responses.
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.ToString().StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    var name = reference.ToString()[8..];
                    if (used.Add(name)) Visit(definitions[name]);
                }
                foreach (var (key, value) in obj) if (key != "$defs") Visit(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(schema);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(key);
        return PlanningSchemas.Compact(schema);
    }

    internal static string Authority(PlanningSession state, bool scopedDependencies = true, int version = 8)
    {
        var authority = new JsonObject
        {
        ["version"] = version, ["intentVersion"] = state.IntentVersion,
        ["tenant"] = state.Request.TenantId, ["session"] = state.Request.SessionId,
        ["baseline"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
        ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["scope"] = new JsonArray(state.RevisionScope.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["permissions"] = PermissionDescriptors(state, scopedDependencies, version),
        ["diagnostics"] = new JsonArray(state.Diagnostics.Where(d => d.Required && d.Code is not ("MODEL_DISPATCH_UNVERIFIABLE" or "LLM_BUDGET_UNVERIFIABLE" or "PLANNING_HOST_FAILURE"))
            .OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["location"] = d.Location }).ToArray()),
        ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog, PlanningJsonContext.Default.PlanningCatalog),
        ["resolved"] = JsonSerializer.SerializeToNode(state.Discovery.Resolved, PlanningJsonContext.Default.ListPlanningCapability),
        ["policy"] = JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["maxModelCalls"] = state.Request.MaxModelCalls, ["maxReplanAttempts"] = state.Request.MaxReplanAttempts,
        ["generation"] = JsonSerializer.SerializeToNode(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions),
        ["options"] = state.Request.Options.DeepClone(), ["clarifications"] = PlanningSchemas.Clarifications()
        };
        if (version >= 8) authority["editablePaths"] = state.EditablePaths is null ? null : new JsonArray(state.EditablePaths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        return PlanningGraphCompiler.Fingerprint(authority.ToJsonString());
    }

    private static JsonObject PermissionDescriptors(PlanningSession state, bool scopedDependencies, int version)
    {
        var definitions = PlanningSchemas.FullProposal(state, compact: false, clarifications: false, scopeGuidance: scopedDependencies)["$defs"]!.AsObject();
        var slots = Slots(state, definitions, scopedDependencies: scopedDependencies, version: version);
        return new() { ["definitions"] = definitions.DeepClone(), ["slots"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject
        { ["id"] = s.Id, ["location"] = s.Location, ["kind"] = s.Kind, ["schema"] = s.ValueSchema.DeepClone(),
            ["actions"] = new JsonArray(s.Actions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) }).ToArray()) };
    }

    internal static JsonObject RequestContext(LLMRequest request)
    {
        var start = request.Prompt.IndexOf("\n{", StringComparison.Ordinal);
        if (start < 0 || JsonNode.Parse(request.Prompt[(start + 1)..]) is not JsonObject context) throw new PlanningConflictException("The issued repair context is unavailable.");
        return context;
    }

    internal static bool Verify(PlanningSession state, LLMRequest request)
    {
        var repair = RequestContext(request)["repair"];
        var version = repair?["version"]?.GetValue<int>();
        if (state.IntentVersion == 2 && version == 8)
        {
            if (state.EditablePaths is not null)
            {
                TaskPlanRevisions.ValidateEditablePaths(state, state.EditablePaths);
                if (!state.EditablePaths.SequenceEqual(state.RevisionScope, StringComparer.Ordinal))
                    throw new PlanningConflictException("The explicit revision authority changed.");
            }
            if (repair!["authority"]?.ToString() == Authority(state)) return true;
        }
        if (state.IntentVersion == 2 && version == 7 && state.EditablePaths is null)
        {
            if (repair!["authority"]?.ToString() == Authority(state, version: 7)) return true;
            // Already-issued version-7 requests keep their original schemas and authority.
            // This compatibility path never issues a new unrestricted dependency slot.
            if (repair["authority"]?.ToString() == Authority(state, scopedDependencies: false, version: 7)) return false;
        }
        throw new PlanningConflictException("The repair baseline, scope or contracts changed. The retained request cannot be rebased or redispatched.");
    }

    internal static TaskPlan Apply(PlanningSession state, RepairPatch patch, LLMRequest request)
    {
        var scopedDependencies = Verify(state, request);
        var version = RequestContext(request)["repair"]!["version"]!.GetValue<int>();
        var definitions = PlanningSchemas.FullProposal(state, compact: false, scopeGuidance: scopedDependencies)["$defs"]!.AsObject();
        var slots = Slots(state, definitions, scopedDependencies: scopedDependencies, version: version).ToDictionary(s => s.Id, StringComparer.Ordinal);
        // Recovery and direct callers both enforce the exact issued response schema.
        var response = new JsonObject { ["discoveryRequests"] = null, ["patch"] = JsonSerializer.SerializeToNode(patch, RepairJsonContext.Default.RepairPatch) };
        if (request.StructuredOutputSchema?["properties"]?["clarifications"] is not null) response["clarifications"] = null;
        // Removal actions omit the payload in the wire contract.
        foreach (var edit in response["patch"]!["edits"]!.AsArray())
            if (edit!["action"]?.ToString() is "remove" or "remove_owned" or "remove_forwarder") edit.AsObject().Remove("value");
        if (PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!).Count > 0)
            throw new PlanningResponseException([new("REVISION_SCOPE_CHANGED", "/patch/edits", "The patch does not satisfy its issued schema.")]);
        var selected = new List<Slot>();
        foreach (var edit in patch.Edits)
        {
            if (!slots.TryGetValue(edit.Slot, out var slot) || !slot.Actions.Contains(edit.Action, StringComparer.Ordinal))
                throw new PlanningResponseException([new("REVISION_SCOPE_CHANGED", "/patch/edits", "Only issued typed repair slots and actions are permitted.")]);
            if (selected.Any(s => s.Id == slot.Id || s.Location.StartsWith(slot.Location + "/", StringComparison.Ordinal) || slot.Location.StartsWith(s.Location + "/", StringComparison.Ordinal)))
                throw new PlanningResponseException([new("REVISION_SCOPE_CHANGED", slot.Location, "Duplicate or overlapping repair edits are not permitted.")]);
            selected.Add(slot);
        }
        var before = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
        var structural = patch.Edits.Where(e => slots[e.Slot].Kind is "prerequisites" or "task" or "forwarder").ToArray();
        var baseline = before.DeepClone().Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        foreach (var edit in structural) PlanningStructuralRepair.Apply(baseline, slots[edit.Slot], edit);
        var tree = JsonSerializer.SerializeToNode(baseline, PlanningJsonContext.Default.TaskPlan)!; var symbols = new TaskPlanSymbols(baseline);
        foreach (var edit in patch.Edits.Except(structural))
        {
            var slot = slots[edit.Slot];
            var index = Index(tree); var site = FindSite(index, slot.Location);
            if (edit.Action == "remove") site!.Remove();
            else if (edit.Action == "remove_owned")
            {
                var owned = TaskPlanRevisions.OwnedInput(symbols, slot.Location, state.Catalog)!;
                var value = PlanningCapabilityArguments.WithoutOwned(owned, slot.Location.Split('/')[4], symbols.Values[slot.Location].Value)!;
                site!.Node!["value"] = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue);
            }
            else if (slot.Kind == "exports")
                foreach (var output in edit.Value!.AsArray()) site!.Node!.AsArray().Add(output!.DeepClone());
            else if (edit.Action == "add")
            {
                var split = slot.Location.LastIndexOf('/');
                index[slot.Location[..split]].Node!.AsArray().Add((JsonNode)new JsonObject { ["name"] = slot.Location[(split + 1)..], ["value"] = edit.Value?.DeepClone() });
            }
            else if (slot.Kind is "value" or "reference" && site!.Node is JsonObject binding && binding.ContainsKey("name") && binding.ContainsKey("value")) binding["value"] = edit.Value?.DeepClone();
            else site!.Set(edit.Value?.DeepClone());
        }
        var candidate = tree.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var findings = TaskPlanRevisions.Validate(baseline, candidate, state.RevisionScope, state.Catalog, version >= 8 ? state.EditablePaths : null).ToList();
        var candidateSymbols = new TaskPlanSymbols(candidate);
        foreach (var slot in slots.Values.Where(s => s.Kind == "reference"))
            if (!candidateSymbols.Values.TryGetValue(slot.Location, out var revisedValue) ||
                !TaskPlanRevisions.ReferenceRepairs(state, slot.Location)!.Any(v => JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(v, PlanningJsonContext.Default.TaskValue),
                    JsonSerializer.SerializeToNode(revisedValue.Value, PlanningJsonContext.Default.TaskValue))))
                findings.Add(new("REVISION_SCOPE_CHANGED", slot.Location, "Repair only the diagnosed producer reference; preserve its surrounding binding and business literals."));
        if (scopedDependencies)
        {
            var revised = new TaskPlanSymbols(candidate);
            foreach (var (id, original) in symbols.Tasks.Where(t => state.RevisionScope.Contains("/tasks/" + t.Key + "/dependsOn")))
                if (revised.Tasks.TryGetValue(id, out var updated))
                {
                    var eligible = symbols.DependencyTargets(id);
                    if (updated.Task.DependsOn.Distinct(StringComparer.Ordinal).Count() != updated.Task.DependsOn.Count ||
                        updated.Task.DependsOn.Any(d => !eligible.Contains(d, StringComparer.Ordinal)) ||
                        original.Task.DependsOn.Where(d => eligible.Contains(d, StringComparer.Ordinal)).Except(updated.Task.DependsOn, StringComparer.Ordinal).Any())
                        findings.Add(new("REVISION_SCOPE_CHANGED", "/tasks/" + id + "/dependsOn", "Use eligible same-scope dependencies and preserve every unaffected edge; duplicate, cyclic and cross-phase dependencies are not repair targets."));
                }
        }
        if (findings.Count > 0) throw new PlanningResponseException(findings);
        if (structural.Length > 0) PlanningStructuralRepair.Validate(state, candidate, structural.Select(e => slots[e.Slot]).ToArray());
        if (JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The repair did not change an authorized semantic slot. The baseline and accounting are retained.");
        var checkedPlan = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        foreach (var choice in checkedPlan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var complete = new TaskPlanCompiler().Compile(checkedPlan, state.Catalog!);
        if (complete.Diagnostics.Count > 0) throw new PlanningResponseException(complete.Diagnostics.ToList());
        return candidate;
    }
    internal static JsonObject Exact(TaskValue value, JsonObject? definitions = null)
    {
        if (definitions is null || value.Items.Count + value.Members.Count == 0)
            return Shape(PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue))!);
        // Factor composite values through a bounded vocabulary of only this repaired
        // value's leaves. Recursive wire factoring avoids provider nesting limits;
        // atomic revision validation still preserves exact member identity/order.
        var name = "repairValue" + definitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        definitions[name] = new JsonObject { ["anyOf"] = new JsonArray(TaskPlanCompiler.Values(value)
            .Select(v => Shape(PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(v, PlanningJsonContext.Default.TaskValue))!, name))
            .DistinctBy(s => s.ToJsonString()).Select(s => (JsonNode)s).ToArray()) };
        return PlanningSchemas.Ref(name);
    }

    private static JsonObject Shape(JsonNode value, string? recursive = null, bool nested = false) => value switch
    {
        JsonObject obj when nested && recursive is not null && obj.ContainsKey("kind") => PlanningSchemas.Ref(recursive),
        JsonObject obj => PlanningSchemas.Object(obj.Select(p => (p.Key, p.Value is null ? PlanningSchemas.Type("null") : Shape(p.Value, recursive, true))).ToArray()),
        JsonArray array => array.Count == 0 ? PlanningSchemas.Array(PlanningSchemas.Ref("value"), 0, 0) :
            PlanningSchemas.Array(new JsonObject { ["anyOf"] = new JsonArray(array.Select(v => (JsonNode)Shape(v!, recursive, true)).ToArray()) }, array.Count, array.Count),
        _ => new() { ["type"] = value.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", _ => "boolean" }, ["enum"] = new JsonArray(value.DeepClone()) }
    };
}
