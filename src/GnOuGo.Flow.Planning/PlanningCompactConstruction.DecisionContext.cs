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
            PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose)) return request;

        IEnumerable<string> selectedRequirements = purpose == SelectPurpose ? SelectionRequirementIds(state) : State(state).CurrentStep!.RequirementIds;
        var selected = selectedRequirements.Concat(EvidenceRequirementIds(state)).ToHashSet(StringComparer.Ordinal);
        var originals = context["requirements"] as JsonArray ?? [];
        var omitted = originals.OfType<JsonObject>().Where(row => !selected.Contains(row["id"]!.ToString())).ToArray();
        if (omitted.Length == 0) return request;
        context["requirements"] = new JsonArray(originals.OfType<JsonObject>().Where(row => selected.Contains(row["id"]!.ToString()))
            .Select(row => (JsonNode)row.DeepClone()).ToArray());
        context["omittedRequirementCount"] = omitted.Length;
        context["requirementsDirectory"] = "page:requirements:0";
        var next = state.Requirements!.Outcomes.FindIndex(requirement => requirement.Id == omitted[0]["id"]!.ToString());
        context["nextRequirements"] = "page:requirements:" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // No opportunistic filling or clipping: globals, retrieved originals and
        // the entire producer/contract core remain unchanged. If this mandatory
        // request is too large, ordinary admission records and rejects it.
        return Render();

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

    internal static JsonArray OriginalRequirementRows(PlanningSession state)
    {
        var rows = RequirementNodes(state, state.Requirements!.Outcomes.Select(requirement => requirement.Id));
        foreach (var row in rows.OfType<JsonObject>())
        {
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

    private static IEnumerable<TaskValue> ProducerReferences(JsonObject row) =>
        (row["binding"] ?? row["value"])?.Deserialize(PlanningJsonContext.Default.TaskValue) is { } value
            ? TaskPlanCompiler.Values(value).Where(item => item.Kind is "output" or "present" && item.Source is not null)
            : [];
}
