using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static LLMRequest Request(PlanningSession state, string instructions, JsonObject context, JsonObject schema, string purpose)
    {
        if (!UsesDecisionView(state) || purpose is not (SelectPurpose or DiscoveryPurpose or PlanningConstruction.DraftPurpose or "replan"))
            return Request(instructions, context, schema, purpose);
        // Typed corrections reuse a review-shaped context, but remain author
        // decisions. Install originals before measuring that complete request.
        context["requirements"] = OriginalRequirementRows(state);
        context["globalConstraints"] = OriginalGlobalRows(state);
        var request = Render();
        // The response grammar, model settings and request envelope participate
        // in admission. Prompt-only estimates cannot choose a safe projection.
        if (ModelRequestAdmission.EstimateTokens(PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt,
                request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens)) <=
            PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose)) return Display(request);

        IEnumerable<string> selectedRequirements = purpose == SelectPurpose ? SelectionRequirementIds(state) : State(state).CurrentStep!.RequirementIds;
        var selected = selectedRequirements.Concat(EvidenceRequirementIds(state)).ToHashSet(StringComparer.Ordinal);
        var originals = context["requirements"] as JsonArray ?? [];
        var omitted = originals.OfType<JsonObject>().Where(row => !selected.Contains(row["id"]!.ToString())).ToArray();
        if (omitted.Length == 0) return Display(request);
        context["requirements"] = new JsonArray(originals.OfType<JsonObject>().Where(row => selected.Contains(row["id"]!.ToString()))
            .Select(row => (JsonNode)row.DeepClone()).ToArray());
        context["omittedRequirementCount"] = omitted.Length;
        context["requirementsDirectory"] = "page:requirements:0";
        var next = state.Requirements!.Outcomes.FindIndex(requirement => requirement.Id == omitted[0]["id"]!.ToString());
        context["nextRequirements"] = "page:requirements:" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // No opportunistic filling or clipping: globals, retrieved originals and
        // the entire producer/contract core remain unchanged. If this mandatory
        // request is too large, ordinary admission records and rejects it.
        return Display(Render());

        LLMRequest Display(LLMRequest chosen)
        {
            // Choose originals and evidence authority using the unchanged full
            // view first. Sharing is only a final fresh author presentation.
            if (!UsesVerifiedProgress(state) || !PlanningConstructionPhasePolicies.IsFresh(state.Request)) return chosen;
            var displayed = CompactAcceptedProgressContext(context);
            if (!ReferenceEquals(displayed, context)) chosen.Prompt = instructions + "\n" + PlanningJsonTransport.Prompt(displayed);
            return chosen;
        }

        LLMRequest Render()
        {
            var response = schema.DeepClone().AsObject();
            if (response["properties"] is JsonObject properties && properties.ContainsKey("evidenceRequests"))
            {
                if (purpose == SelectPurpose) ConstrainSelectionEvidence(state, context, response);
                else if (purpose == DiscoveryPurpose) ConstrainDiscoveryEvidence(state, context, response);
                else ConstrainDraftEvidence(state, context, response);
            }
            return Request(instructions, context, response, purpose);
        }
    }

    internal static JsonObject CompactAcceptedProgressContext(JsonObject context)
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject? displayed = null;
        var requirements = context["requirements"] as JsonArray ?? [];
        for (var requirement = 0; requirement < requirements.Count; requirement++)
        {
            var items = requirements[requirement]?["acceptedProgress"]?["items"] as JsonArray ?? [];
            for (var item = 0; item < items.Count; item++)
            {
                if (items[item] is not JsonObject row || row["sequence"] is null || row["evidence"] is null || row["detailsPointer"] is not null) continue;
                var exact = PlanningJsonTransport.Prompt(row);
                var pointer = $"/requirements/{requirement}/acceptedProgress/items/{item}";
                if (first.TryAdd(exact, pointer)) continue;
                var reference = new JsonObject
                {
                    ["sequence"] = row["sequence"]!.DeepClone(), ["evidence"] = row["evidence"]!.DeepClone(),
                    ["detailsPointer"] = first[exact]
                };
                if (Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(reference)) >= Encoding.UTF8.GetByteCount(exact)) continue;
                displayed ??= context.DeepClone().AsObject();
                displayed["requirements"]![requirement]!["acceptedProgress"]!["items"]![item] = reference;
            }
        }
        return displayed ?? context;
    }

    internal static JsonArray OriginalRequirementRows(PlanningSession state)
    {
        var rows = RequirementNodes(state, state.Requirements!.Outcomes.Select(requirement => requirement.Id));
        var accepted = UsesAcceptedProgress(state) && state.Plan is not null ? new TaskPlanSymbols(state.Plan) : null;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (accepted is not null) row["acceptedProgress"] = AcceptedRequirementProgress(state, accepted, row["id"]!.ToString());
            var sources = row["sources"]!.AsArray().OfType<JsonObject>().ToArray();
            var originals = sources.Select(source => State(state).Sources.Single(value => value.Id == source["id"]!.ToString())).ToArray();
            // Indexed requirements already contain the exact concatenated clause,
            // including its continuations. Do not repeat that text in normal rows.
            // A differently worded retained requirement still needs its originals.
            if (string.Concat(originals.Select(source => source.Text)) == row["original"]!.ToString()) continue;
            for (var index = 0; index < sources.Length; index++) sources[index]["text"] = originals[index].Text;
        }
        return rows;
    }

    private static JsonObject AcceptedRequirementProgress(PlanningSession state, TaskPlanSymbols accepted, string requirementId)
    {
        var construction = state.Construction!;
        var requirement = construction.Requirements.Single(item => item.Id == requirementId);
        var checkpoints = construction.Checkpoints.Select((checkpoint, index) => (checkpoint, index)).Where(item =>
            requirement.Checkpoints.Contains(item.checkpoint.Sequence) && item.checkpoint.RequirementIds.Contains(requirementId, StringComparer.Ordinal)).ToArray();
        return new()
        {
            // Associations describe accepted contributions, never fulfillment or
            // execution. Current requirement status remains the separate claim.
            ["basis"] = "accepted_checkpoint_associations_not_fulfillment", ["checkpointCount"] = checkpoints.Length,
            ["directory"] = checkpoints.Length > 4 ? "page:checkpoints:0" : null,
            ["items"] = new JsonArray(checkpoints.TakeLast(4).Select(item =>
            {
                var checkpoint = item.checkpoint;
                var tasks = checkpoint.TaskIds.Where(id => !checkpoint.InvalidatedTaskIds.Contains(id, StringComparer.Ordinal) && accepted.Tasks.ContainsKey(id))
                    .Distinct(StringComparer.Ordinal).ToArray();
                var scope = accepted.Scopes.SingleOrDefault(scope => scope.Path == checkpoint.Step?.Scope ||
                    checkpoint.Step is null && scope.Path == checkpoint.Fragment["scope"]?.ToString());
                var exports = scope is null ? [] : (checkpoint.Fragment["outputs"] as JsonArray ?? []).OfType<JsonObject>()
                    .Where(output => scope.Source.Outputs.Any(binding => JsonNode.DeepEquals(output,
                        PlanningConstruction.Node(binding, PlanningJsonContext.Default.TaskOutput)))).ToArray();
                return (JsonNode)new JsonObject
                {
                    ["sequence"] = checkpoint.Sequence, ["evidence"] = "page:checkpoints:" + item.index,
                    ["scope"] = scope?.Path, ["scopeOpen"] = scope is null ? null : JsonValue.Create(construction.OpenScopes.Contains(scope.Path, StringComparer.Ordinal)),
                    ["taskCount"] = tasks.Length, ["tasks"] = new JsonArray(tasks.Take(4).Select(id =>
                    {
                        var site = accepted.Tasks[id];
                        var entry = TaskNavigation(state, site.Task, site.Scope.Path);
                        var open = accepted.Scopes.Count(boundary => boundary.Owner?.Id == id && construction.OpenScopes.Contains(boundary.Path, StringComparer.Ordinal));
                        // A call's result directory comes from the accepted group,
                        // never a currently proposed group's tentative exports.
                        var ports = (site.Task.Kind == "call"
                            ? state.Plan!.Groups.Where(group => group.Id == site.Task.Group &&
                                !construction.OpenScopes.Contains("/groups/" + group.Id + "/body", StringComparer.Ordinal))
                                .SelectMany(group => group.Body.Outputs.Select(output => output.Name))
                            : PlanningRepairContext.Ports(site.Task, state)).ToArray();
                        entry["evidence"] = "task:" + id;
                        entry["openResultScopeCount"] = open;
                        entry["portCount"] = ports.Length;
                        entry["ports"] = new JsonArray(ports.Take(8).Select(port => (JsonNode?)JsonValue.Create(port)).ToArray());
                        entry["nextPorts"] = ports.Length > 8 ? TaskPortsAddress(id, ports, 8) : null;
                        return (JsonNode)TrimNavigation(entry);
                    }).ToArray()),
                    ["outputCount"] = exports.Length,
                    ["outputs"] = new JsonArray(exports.Take(4).Select(output => (JsonNode)new JsonObject
                    {
                        ["name"] = output["name"]!.DeepClone(), ["scope"] = scope!.Path,
                        ["evidence"] = ScopeInterfaceAddress(scope.Path, false, ScopeInterfaceVersion(state.Plan!, construction.OpenScopes, scope.Path), 0)
                    }).ToArray())
                };
            }).ToArray())
        };
    }

    internal static JsonArray OriginalGlobalRows(PlanningSession state) => Sources(state, State(state).GlobalSourceIds);

    internal static void AddDecisionContext(PlanningSession state, JsonObject context, bool review, bool discovery = false)
    {
        if (!UsesDecisionView(state)) return;
        discovery &= !review;
        if (!review)
        {
            context["requirements"] = OriginalRequirementRows(state);
            context["globalConstraints"] = OriginalGlobalRows(state);
        }
        // Review windows own their exact original rows. The semantic producer core
        // is common to every window and is never truncated to make a request fit.
        if (state.Plan is null) return;
        if (context["interfaces"] is not JsonArray)
            context["interfaces"] = State(state).CurrentStep is { } step
                ? ImportedInterfaces(state, step) : InterfaceDirectory(state, null, State(state).InterfaceOffset);
        var rows = context["interfaces"]!.AsArray().OfType<JsonObject>().ToArray();
        var accepted = new TaskPlanSymbols(state.Plan);
        var bindings = rows.SelectMany(ProducerReferences).ToArray();
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var ids = ProducerReferences(row).Select(value => value.Source!);
            var producers = new JsonArray();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                if (!accepted.Tasks.TryGetValue(id, out var site) || !supplied.Add(id)) continue;
                var producer = new JsonObject
                {
                    ["id"] = id, ["scope"] = site.Scope.Path, ["phase"] = accepted.Phase(id),
                    ["evidence"] = "task:" + id, ["task"] = PlanningConstruction.TaskNode(site.Task)
                };
                if (site.Task.Kind == "operation" && site.Task.Operation is { } operation)
                {
                    var capability = ExactContract(state, operation);
                    var contract = TaskOperations.Describe(capability);
                    var identity = ContractIdentity(contract.Id, contract.Version);
                    var selected = bindings.Where(binding => binding.Source == id).Select(binding => binding.Port)
                        .Distinct(StringComparer.Ordinal).Select(port => port is null ? "/output" :
                            PortPointer("/output", contract.Outputs.Single(output => output.Name == port).Path)).ToArray();
                    producer["contract"] = new JsonObject
                    {
                        ["id"] = contract.Id, ["version"] = contract.Version, ["hash"] = ContractHash(capability),
                        ["description"] = contract.Description, ["effect"] = capability.EffectKind,
                        ["descriptionEvidence"] = "description:" + identity + ":0",
                        ["selectedInputs"] = new JsonArray(contract.Inputs.Where(port => site.Task.Inputs.Any(input => input.Name == port.Name))
                            .Select(port => (JsonNode)ExactSchemaView(capability, PortPointer("/input", port.Path))).ToArray()),
                        ["outputs"] = CompletePortDirectory(contract.Outputs, identity, "/output"),
                        ["inputDirectory"] = "schema-page:" + identity + ":/input:0",
                        ["outputDirectory"] = "port-page:" + identity + ":/output:0",
                        ["selectedOutputs"] = new JsonArray(selected.Select(pointer => (JsonNode)(discovery
                            ? new JsonObject { ["pointer"] = pointer, ["schemaEvidence"] = "schema:" + identity + ":" + pointer }
                            : ExactSchemaView(capability, pointer))).ToArray()),
                        ["artifacts"] = TaskOperations.ArtifactPorts(capability)
                    };
                    CompactProducerSchemaMetadata(producer["contract"]!.AsObject(), UsesVerifiedProgress(state));
                    if (discovery)
                    {
                        // These are provisional navigation bindings, not actual
                        // consumers. Keep resolved interfaces, exact arguments,
                        // input constraints and normative prose; original output
                        // closures remain retrievable through every port's links.
                        var view = producer["contract"]!.AsObject();
                        view["selectedOutputSchemasOmitted"] = selected.Length > 0;
                        foreach (var port in contract.Outputs)
                            if (port.Schema["description"] is { } description)
                                view["outputs"]!.AsArray().Single(row => row!["name"]!.ToString() == port.Name)!["description"] = description.DeepClone();
                    }
                    // The mandatory producer view already carries these exact port
                    // identities. Keep the task's navigation links and provenance,
                    // but do not repeat an identical directory in a second row.
                    if (context["taskEvidence"]?["items"] is JsonArray navigation)
                        foreach (var entry in navigation.OfType<JsonObject>())
                            if (entry["task"]?.ToString() == id && entry["scope"]?.ToString() == site.Scope.Path &&
                                entry["phase"]?.ToString() == accepted.Phase(id) && entry["operation"]?.ToString() == contract.Id &&
                                entry["operationVersion"]?.ToString() == contract.Version &&
                                entry["contractHash"]?.ToString() == producer["contract"]!["hash"]!.ToString() &&
                                entry["outputs"] is JsonArray ports && JsonNode.DeepEquals(ports, producer["contract"]!["outputs"]))
                                entry.Remove("outputs");
                }
                else if (site.Task.Kind == "call" && state.Plan.Groups.SingleOrDefault(group => group.Id == site.Task.Group) is { } group)
                {
                    // Preserve the declared group input/export boundary, including
                    // its exact value expressions, rather than implying capture.
                    producer["group"] = PlanningConstruction.Node(group, PlanningJsonContext.Default.TaskGroup);
                }
                producers.Add((JsonNode)producer);
            }
            if (producers.Count > 0) row["producers"] = producers;
        }
    }

    internal static void CompactProducerSchemaMetadata(JsonObject contract, bool shareConstraints = false)
    {
        var views = new[] { "selectedInputs", "selectedOutputs" }.SelectMany(name =>
            (contract[name] as JsonArray ?? []).OfType<JsonObject>()).Where(view => view["schema"] is not null).ToArray();
        var identity = new[] { "id", "version", "hash" };
        if (views.Length == 0 || identity.Any(key => contract[key] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text)) ||
            views.Any(view => identity.Any(key => !JsonNode.DeepEquals(view[key], contract[key])))) return;
        if (shareConstraints)
            foreach (var direction in new[] { "selectedInputs", "selectedOutputs" })
            {
                var selected = (contract[direction] as JsonArray ?? []).OfType<JsonObject>().ToArray();
                if (selected.Length < 2 || selected.Any(view => view["schema"] is null)) continue;
                var sharedName = direction + "SharedSchemaMetadata";
                if (contract[sharedName] is not null) continue;
                var shared = new JsonObject();
                foreach (var key in new[] { "ancestors", "referenceClosure" })
                    if (selected[0][key] is { } original && selected.All(view =>
                        string.Equals(view[key]?.ToJsonString(), original.ToJsonString(), StringComparison.Ordinal)))
                        shared[key] = original.DeepClone();
                // Direction-specific inheritance applies only to these embedded
                // views. Original coordinates, schemas and retrieval stay exact.
                var size = System.Text.Encoding.UTF8.GetByteCount(shared.ToJsonString());
                if (shared.Count > 0 && selected.Length * (size - 1) > size + sharedName.Length + 4)
                {
                    contract[sharedName] = shared;
                    foreach (var view in selected)
                        foreach (var key in shared.Select(item => item.Key)) view.Remove(key);
                }
            }
        // These views are embedded under this exact contract. Their schema paths,
        // coupled ancestor constraints and reference closures remain unchanged.
        if (views[0]["view"] is JsonValue description && description.TryGetValue<string>(out _) &&
            views.All(view => JsonNode.DeepEquals(view["view"], description)) &&
            (contract["selectedSchemaView"] is null || JsonNode.DeepEquals(contract["selectedSchemaView"], description)))
        {
            contract["selectedSchemaView"] = description.DeepClone();
            foreach (var view in views) view.Remove("view");
        }
        foreach (var view in views)
            foreach (var key in identity) view.Remove(key);
    }

    private static IEnumerable<TaskValue> ProducerReferences(JsonObject row) =>
        (row["binding"] ?? row["value"])?.Deserialize(PlanningJsonContext.Default.TaskValue) is { } value
            ? TaskPlanCompiler.Values(value).Where(item => item.Kind is "output" or "present" && item.Source is not null)
            : [];
}
