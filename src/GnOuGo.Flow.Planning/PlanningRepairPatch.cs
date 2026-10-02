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
    internal static bool Active(PlanningSession state) => state.Plan is not null && state.RevisionScope.Count > 0 && PlanningModelCalls.IsRepair(state);
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

    private static Site? FindSite(Dictionary<string, Site> index, string path)
    {
        if (index.TryGetValue(path, out var site)) return site;
        var split = path.LastIndexOf('/');
        return split > 0 && index.GetValueOrDefault(path[..split])?.Node is JsonObject parent && !parent.ContainsKey(path[(split + 1)..])
            ? new(null, parent, path[(split + 1)..], null, -1) : null;
    }

    internal static IReadOnlyList<Slot> Slots(PlanningSession state, JsonObject definitions, bool structural = true)
    {
        var plan = state.Plan!; var symbols = new TaskPlanSymbols(plan);
        var index = Index(JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!);
        var types = TaskPlanRevisions.ProducerConstraintSlots(plan); var slots = new List<Slot>();
        var structuralSlots = structural ? PlanningStructuralRepair.Slots(state, definitions) : [];
        foreach (var path in state.RevisionScope.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (path.EndsWith("/kind", StringComparison.Ordinal) && structuralSlots.Any(s => s.Kind == "task" && path == s.Location + "/kind")) continue;
            var kind = "value"; var schema = PlanningSchemas.Ref("value"); var actions = new List<string>();
            var site = FindSite(index, path);
            if (state.OutcomeVersion == 2 && path.Split('/') is ["", "outcomeBindings", var outcomeId] &&
                state.Requirements?.Outcomes.SingleOrDefault(o => o.Id == outcomeId) is { } outcome &&
                state.OutcomeBindings?.Count(b => b.OutcomeId == outcomeId) == 1)
            {
                kind = "outcome"; actions.Add("replace");
                var loops = symbols.Tasks.Values.Where(t => t.Task.Kind == "foreach").Select(t => t.Task.Id).ToArray();
                schema = PlanningSchemas.Object(("taskIds", PlanningSchemas.Array(PlanningSchemas.Enum(symbols.Tasks.Keys.Order(StringComparer.Ordinal).ToArray()))),
                    ("outputs", state.Plan!.Root.Outputs.Count == 0 ? PlanningSchemas.Array(PlanningSchemas.String(), 0, 0) : PlanningSchemas.Array(PlanningSchemas.Enum(state.Plan.Root.Outputs.Select(o => o.Name).ToArray()))),
                    ("forEachTaskId", outcome.Coverage == "each_item" && loops.Length > 0 ? PlanningSchemas.Enum(loops) : PlanningSchemas.Type("null")));
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
                            schema = state.OutcomeVersion == 2 ? PlanningBindingSchemas.For(port.Schema, definitions,
                                literalOnly: contracts[0].StepType == "agent.run" && name is "objective" or "capabilities" or "budget" or "verification" or "output_schema",
                                workspace: contracts[0].StepType == "agent.run" && name == "workspace") : PlanningSchemas.DomainValue(port.Schema, definitions);
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
                    "operation" => PlanningSchemas.Enum(state.Catalog!.Capabilities.Select(c => TaskOperations.Describe(c).Id)
                        .Concat(state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Operation?.Id).OfType<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                    "group" => PlanningSchemas.Enum(plan.Groups.Select(g => g.Id).ToArray()),
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
    }

    private static JsonObject FixedName(JsonObject schema, string property, string value)
    {
        if (schema["anyOf"] is JsonArray alternatives)
            foreach (var child in alternatives.OfType<JsonObject>()) FixedName(child, property, value);
        else schema["properties"]![property] = PlanningSchemas.Enum(value);
        return schema;
    }

    internal static JsonObject Schema(PlanningSession state, JsonObject template)
    {
        var definitions = template["$defs"]!.DeepClone().AsObject();
        var slots = Slots(state, definitions); var edits = new JsonArray();
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
        return schema;
    }

    internal static string Authority(PlanningSession state, int version = 1) => version == 5 ? Authority5(state) : version == 4 ? PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["baselineAuthority"] = Authority(state, 3), ["outcomeVersion"] = state.OutcomeVersion,
        ["outcomeBindings"] = JsonSerializer.SerializeToNode(state.OutcomeBindings, PlanningJsonContext.Default.ListPlanningOutcomeBinding)
    }.ToJsonString()) : version == 3 ? PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["baselineAuthority"] = Authority(state, 2),
        ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["clarifications"] = PlanningSchemas.Clarifications()
    }.ToJsonString()) : version == 2 ? PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["baselineAuthority"] = Authority(state),
        ["permissions"] = PermissionDescriptors(state),
        ["diagnostics"] = new JsonArray(state.Diagnostics.Where(d => d.Required).OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["location"] = d.Location }).ToArray())
    }.ToJsonString()) : PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["tenant"] = state.Request.TenantId, ["session"] = state.Request.SessionId,
        ["baseline"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
        ["scope"] = new JsonArray(state.RevisionScope.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog, PlanningJsonContext.Default.PlanningCatalog),
        ["resolved"] = JsonSerializer.SerializeToNode(state.Discovery.Resolved, PlanningJsonContext.Default.ListPlanningCapability),
        ["policy"] = JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["maxModelCalls"] = state.Request.MaxModelCalls, ["maxReplanAttempts"] = state.Request.MaxReplanAttempts,
        ["generation"] = JsonSerializer.SerializeToNode(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions),
        ["options"] = state.Request.Options.DeepClone()
    }.ToJsonString());

    private static string Authority5(PlanningSession state)
    {
        // A failed dispatch adds transport evidence, not edit permission. Preserve
        // the issued authority across that interruption, without changing versions 1–4.
        var stable = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        stable.Diagnostics.RemoveAll(d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE");
        return PlanningGraphCompiler.Fingerprint(new JsonObject
        {
            ["baselineAuthority"] = Authority(stable, 4), ["permissions"] = PermissionDescriptors(stable), ["contractVersion"] = 2
        }.ToJsonString());
    }

    private static JsonObject PermissionDescriptors(PlanningSession state)
    {
        // Version 2 authorities included all template definitions, including unreachable ones.
        // Recreate that exact template so historical pending requests retain their authority.
        var definitions = PlanningSchemas.FullProposal(state, compact: false, clarifications: false)["$defs"]!.AsObject();
        var slots = Slots(state, definitions);
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

    internal static void Verify(PlanningSession state, LLMRequest request)
    {
        var repair = RequestContext(request)["repair"];
        var version = repair?["version"]?.GetValue<int>();
        if (version is not (1 or 2 or 3 or 4 or 5) || repair!["authority"]?.ToString() != Authority(state, version.Value))
            throw new PlanningConflictException("The repair baseline, scope or contracts changed. The retained request cannot be rebased or redispatched.");
    }

    internal static TaskPlan Apply(PlanningSession state, RepairPatch patch, LLMRequest request)
        => Apply(state, patch, request, out _);

    internal static TaskPlan Apply(PlanningSession state, RepairPatch patch, LLMRequest request, out List<PlanningOutcomeBinding>? outcomeBindings)
    {
        Verify(state, request);
        outcomeBindings = state.OutcomeBindings is null ? null : JsonSerializer.SerializeToNode(state.OutcomeBindings, PlanningJsonContext.Default.ListPlanningOutcomeBinding)!.Deserialize(PlanningJsonContext.Default.ListPlanningOutcomeBinding);
        var definitions = PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject();
        var version = RequestContext(request)["repair"]!["version"]!.GetValue<int>();
        var slots = Slots(state, definitions, structural: version >= 2).ToDictionary(s => s.Id, StringComparer.Ordinal);
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
            if (slot.Kind == "outcome")
            {
                var id = slot.Location.Split('/')[2]; var replacement = edit.Value!.DeepClone().AsObject(); replacement["outcomeId"] = id;
                outcomeBindings![outcomeBindings.FindIndex(b => b.OutcomeId == id)] = replacement.Deserialize(PlanningJsonContext.Default.PlanningOutcomeBinding)!;
                continue;
            }
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
            else if (slot.Kind == "value" && site!.Node is JsonObject binding && binding.ContainsKey("name") && binding.ContainsKey("value")) binding["value"] = edit.Value?.DeepClone();
            else site!.Set(edit.Value?.DeepClone());
        }
        var candidate = tree.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var findings = TaskPlanRevisions.Validate(baseline, candidate, state.RevisionScope, state.Catalog).ToList();
        if (findings.Count > 0) throw new PlanningResponseException(findings);
        if (structural.Length > 0) PlanningStructuralRepair.Validate(state, candidate, structural.Select(e => slots[e.Slot]).ToArray());
        if (selected.Any(s => s.Kind == "outcome"))
        {
            var complete = new TaskPlanCompiler().Compile(candidate, state.Catalog!);
            var review = new PlanningSession { OutcomeVersion = state.OutcomeVersion, Plan = candidate, Catalog = state.Catalog,
                Requirements = state.Requirements, OutcomeBindings = outcomeBindings };
            var invalid = complete.Diagnostics.Concat(PlanningOutcomeValidation.Findings(review)).ToList();
            if (invalid.Count > 0) throw new PlanningResponseException(invalid);
        }
        if (JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)) &&
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.OutcomeBindings, PlanningJsonContext.Default.ListPlanningOutcomeBinding),
                JsonSerializer.SerializeToNode(outcomeBindings, PlanningJsonContext.Default.ListPlanningOutcomeBinding)))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The repair did not change an authorized semantic slot. The baseline and accounting are retained.");
        return candidate;
    }
}
