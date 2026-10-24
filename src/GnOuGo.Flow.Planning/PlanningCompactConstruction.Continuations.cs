using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static bool Active(PlanningConstructionSuspendedStep item) => item.Status is "waiting" or "resuming";
    private const string ContinuationValidation = "Model-authored navigation, not an original obligation or permission. Eligibility only allows proposing a binding; resumption still checks its exact checkpoint, source, contract, scope and retained type. Compatible types do not identify the intended producer.";

    internal static void ConstrainReselectionSchema(PlanningSession state, JsonObject schema)
    {
        if (!UsesConvergentConstruction(state) || schema["properties"]?["reselectStep"] is not JsonObject previous ||
            previous["type"]?.ToString() == "null") return;
        var alternatives = new JsonArray(PlanningSchemas.Object(("reason", Text(512)),
            ("disposition", PlanningSchemas.Enum("abandon")), ("prerequisite", PlanningSchemas.Type("null"))));
        var addresses = StepOriginalEvidence(state).Concat(PrerequisiteAddresses(state, [])
            .Where(address => !IsOriginalAddress(address))).Distinct(StringComparer.Ordinal).ToArray();
        var active = State(state).SuspendedSteps?.Count(Active) ?? 0;
        if (addresses.Length > 0 && (active < 4 || State(state).ResumingStepId is not null))
        {
            var definitions = schema["$defs"] as JsonObject ?? new JsonObject();
            if (schema["$defs"] is null) schema["$defs"] = definitions;
            var originals = IntentSchema(new())["$defs"]!.AsObject();
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
                { ["type"] = "continuationType", ["input"] = "continuationField", ["literal"] = "continuationLiteral" };
            foreach (var (oldName, name) in names)
            {
                var definition = originals[oldName]!.DeepClone().AsObject();
                foreach (var (_, node) in PlanningSchemaReferences.Walk(definition, "", 0))
                    if (node["$ref"]?.ToString() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal) &&
                        names.TryGetValue(reference[8..], out var replacement)) node["$ref"] = "#/$defs/" + replacement;
                definitions[name] = definition;
            }
            alternatives.Add((JsonNode)PlanningSchemas.Object(("reason", Text(512)), ("disposition", PlanningSchemas.Enum("suspend")),
                ("prerequisite", PlanningSchemas.Object(("need", Text(256)), ("expectedType", PlanningSchemas.Ref("continuationType")),
                    ("evidenceAddresses", PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, 4))))));
        }
        alternatives.Add((JsonNode)PlanningSchemas.Type("null"));
        schema["properties"]!["reselectStep"] = new JsonObject { ["anyOf"] = alternatives };
    }

    internal static PlanningConstructionReselection ReadReselection(PlanningSession state, JsonNode value, JsonNode issuedSchema)
    {
        var structured = ReselShapes(issuedSchema["properties"]?["reselectStep"], issuedSchema, new HashSet<string>(StringComparer.Ordinal))
            .Any(shape => shape["properties"]?["disposition"] is not null);
        if (!structured)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var reason))
                PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep", "Use the exact issued reselection contract.");
            return new() { Reason = value.GetValue<string>(), Disposition = "legacy" };
        }
        if (!UsesConvergentConstruction(state) || value is not JsonObject)
            PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep", "The captured presentation does not authorize structured reselection.");
        return value.Deserialize(PlanningJsonContext.Default.PlanningConstructionReselection)!;
    }

    private static IEnumerable<JsonObject> ReselShapes(JsonNode? node, JsonNode root, HashSet<string> visited)
    {
        if (node is not JsonObject obj) yield break;
        yield return obj;
        if (obj["$ref"]?.ToString() is { } reference && reference.StartsWith("#/", StringComparison.Ordinal) && visited.Add(reference))
            foreach (var shape in ReselShapes(PlanningSchemaReferences.Read(root, reference[1..]), root, visited)) yield return shape;
        if (obj["anyOf"] is JsonArray alternatives)
            foreach (var alternative in alternatives)
                foreach (var shape in ReselShapes(alternative, root, visited)) yield return shape;
    }

    internal static void Reselect(PlanningSession state, PlanningConstructionReselection decision)
    {
        if (decision.Disposition == "legacy") { Reselect(state, decision.Reason); return; }
        var construction = state.Construction!; var compact = State(state); var step = compact.CurrentStep;
        if (!UsesConvergentConstruction(state) || step is null || decision.Disposition is not ("suspend" or "abandon") ||
            string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Length > 512 ||
            (decision.Disposition == "suspend") != (decision.Prerequisite is not null))
            PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep", "Explicitly abandon this selection or suspend it for one evidenced typed prerequisite.");
        PlanningConstructionSuspendedStep? suspended = null;
        var resuming = compact.SuspendedSteps?.SingleOrDefault(item => item.Id == compact.ResumingStepId && Active(item));
        if (decision.Prerequisite is { } need)
        {
            if ((compact.SuspendedSteps?.Count(Active) ?? 0) - (resuming is null ? 0 : 1) >= 4)
                PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_LIMIT", "/reselectStep", "At most four consumers may be suspended in the current prerequisite chain.");
            var probe = CloneStep(step!);
            probe.Prerequisites = [new() { Need = need.Need, EvidenceAddresses = need.EvidenceAddresses.ToList() }];
            ValidatePrerequisites(state, probe);
            try { _ = TaskPlanCompiler.TypeSchema(need.ExpectedType); }
            catch (ArgumentException) { PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep/prerequisite/expectedType", "The prerequisite needs a valid existing business type."); }
            var needHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(need, PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite));
            if ((compact.SuspendedSteps ?? []).Any(item => Active(item) && item.Consumer.Scope == step!.Scope &&
                    item.Consumer.Objective == step.Objective && item.Consumer.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(step.RequirementIds) &&
                    PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(item.Prerequisite, PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite)) == needHash))
                PlanningConstruction.Fail("REPLAN_NO_PROGRESS", "/reselectStep", "The same consumer and typed prerequisite are already suspended.");
            suspended = new()
            {
                Id = "continuation_" + compact.RejectedSteps.Count,
                ParentId = resuming?.ParentId ?? compact.SuspendedSteps?.LastOrDefault(Active)?.Id,
                Consumer = CloneStep(step!),
                Prerequisite = JsonSerializer.Deserialize(JsonSerializer.Serialize(need, PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite),
                    PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite)!,
                RejectedStepIndex = compact.RejectedSteps.Count, SuspendedAtCheckpoint = construction.Checkpoints.Count,
                LastPrerequisiteCheckpoint = construction.Checkpoints.Count
            };
            foreach (var id in step!.RequirementIds.SelectMany(id => compact.RequirementSources.GetValueOrDefault(id) ?? []).Concat(compact.GlobalSourceIds).Distinct(StringComparer.Ordinal))
                suspended.SourceVersions[id] = compact.Sources.Single(source => source.Id == id).Version;
            foreach (var address in need.EvidenceAddresses) suspended.EvidenceHashes[address] = EvidenceHash(state, address);
            foreach (var capability in SelectedContracts(state))
            {
                var operation = TaskOperations.Describe(capability);
                suspended.ContractHashes[ContractIdentity(operation.Id, operation.Version)] = ContractHash(capability);
            }
            var symbols = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
            foreach (var id in step.InputTaskIds.Concat(step.DependencyTaskIds).Distinct(StringComparer.Ordinal))
                if (symbols.Tasks.TryGetValue(id, out var task)) suspended.DependencyHashes[id] = TaskHash(task.Task);
            if (step.GroupId is { } groupId) suspended.GroupHash = GroupInterface(state, groupId)?["hash"]?.ToString();
        }
        // The existing action owns validation, rejection history and correction accounting.
        Reselect(state, decision.Reason);
        compact.RejectedSteps[^1]["disposition"] = decision.Disposition;
        if (resuming is not null) resuming.Status = suspended is null ? "abandoned" : "resuspended";
        compact.ResumingStepId = null;
        if (suspended is not null) (compact.SuspendedSteps ??= []).Add(suspended);
    }

    internal static void ResumeStep(PlanningSession state, string continuationId, int producerCheckpoint, TaskValue binding)
    {
        var construction = state.Construction!; var compact = State(state);
        var item = compact.SuspendedSteps?.LastOrDefault(Active);
        if (!UsesConvergentConstruction(state) || state.PendingCall is not null || construction.Candidate is not null || compact.CurrentStep is not null ||
            item is null || item.Id != continuationId || item.Status != "waiting" || !ContinuationCurrent(state, item) ||
            compact.ReplacementScope is not null)
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_INVALID", "/resumeStep", "Resume only the current retained consumer after its prerequisite has been accepted.");
        var leaf = binding;
        while (leaf.Kind == "field" && leaf.Items.Count == 1) leaf = leaf.Items[0];
        if (leaf.Kind is not ("output" or "present") || leaf.Source is null || producerCheckpoint <= item!.SuspendedAtCheckpoint ||
            producerCheckpoint > construction.Checkpoints.Count)
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_INVALID", "/resumeStep/binding", "Select an exact accepted producer from a checkpoint after suspension.");
        var checkpoint = construction.Checkpoints[producerCheckpoint - 1];
        var symbols = new TaskPlanSymbols(state.Plan!);
        var producerExists = symbols.Tasks.TryGetValue(leaf.Source!, out var producer);
        if (checkpoint.Sequence != producerCheckpoint || checkpoint.InvalidatedTaskIds.Contains(leaf.Source!, StringComparer.Ordinal) || !producerExists ||
            !checkpoint.TaskIds.Contains(leaf.Source!, StringComparer.Ordinal) && !checkpoint.ClosedScopes.Any(path =>
                symbols.Scopes.Any(scope => scope.Path == path && scope.Owner?.Id == leaf.Source)))
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_INVALID", "/resumeStep/producerCheckpoint", "The selected checkpoint must own the accepted producer or its completed container boundary.");
        var imported = CloneStep(item!.Consumer); imported.Inputs = [binding];
        var interfaces = ImportedInterfaces(state, imported);
        if (!PlanningContractCompatibility.Fits(interfaces[0]!["schema"]!.AsObject(), TaskPlanCompiler.TypeSchema(item.Prerequisite.ExpectedType)))
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_INVALID", "/resumeStep/binding", "The accepted binding does not satisfy the retained prerequisite type.");
        var resumed = CloneStep(item.Consumer);
        if (!resumed.Inputs.Any(value => JsonNode.DeepEquals(PlanningConstruction.Node(value, PlanningJsonContext.Default.TaskValue),
                PlanningConstruction.Node(binding, PlanningJsonContext.Default.TaskValue)))) resumed.Inputs.Add(binding);
        if (resumed.Inputs.Count > 8)
            PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_LIMIT", "/resumeStep/binding", "The resumed consumer exceeds its existing eight selected-input bound.");
        resumed.Prerequisites ??= [];
        if (!resumed.Prerequisites.Any(need => need.Need == item.Prerequisite.Need && need.EvidenceAddresses.SequenceEqual(item.Prerequisite.EvidenceAddresses)))
            resumed.Prerequisites.Add(new() { Need = item.Prerequisite.Need, EvidenceAddresses = item.Prerequisite.EvidenceAddresses.ToList() });
        // Validate on an isolated session because exact cached evidence may have left the current presentation.
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var retained = compact.RejectedSteps[item.RejectedStepIndex]["evidence"] as JsonObject;
        probe.Construction!.Evidence = retained?.DeepClone().AsObject();
        ValidateSelection(probe, resumed);
        resumed.Id = "step_" + (compact.NextStep + 1);
        resumed.PendingDiscovery = null;
        compact.NextStep++; compact.CurrentStep = resumed; compact.ResumingStepId = item.Id;
        construction.Evidence = probe.Construction.Evidence;
        compact.PendingCommitments.Clear();
        RetainPrerequisiteEvidence(state);
        item.DependencyHashes[leaf.Source!] = TaskHash(producer.Task);
        item.Status = "resuming"; item.LastPrerequisiteCheckpoint = producerCheckpoint;
        state.Diagnostics.Clear();
    }

    internal static JsonObject? ContinuationContext(PlanningSession state)
    {
        if (!UsesConvergentConstruction(state)) return null;
        var active = State(state).SuspendedSteps?.Where(Active).ToArray() ?? [];
        if (active.Length == 0) return null;
        var item = active[^1];
        var symbols = new TaskPlanSymbols(state.Plan ?? new());
        var checkpoints = state.Construction!.Checkpoints.Where(checkpoint => checkpoint.Sequence > item.SuspendedAtCheckpoint &&
            (checkpoint.TaskIds.Any(id => !checkpoint.InvalidatedTaskIds.Contains(id, StringComparer.Ordinal) && symbols.Tasks.ContainsKey(id)) ||
                checkpoint.ClosedScopes.Any(path => !state.Construction.OpenScopes.Contains(path, StringComparer.Ordinal) &&
                    symbols.Scopes.Any(scope => scope.Path == path && scope.Owner is { } owner &&
                        !checkpoint.InvalidatedTaskIds.Contains(owner.Id, StringComparer.Ordinal))))).ToArray();
        var page = state.Construction.Evidence?.LastOrDefault(evidence => evidence.Key.StartsWith("page:checkpoints:", StringComparison.Ordinal)).Value;
        var requested = (page?["items"] as JsonArray ?? []).Select(entry => checkpoints.SingleOrDefault(checkpoint =>
            entry?["sequence"]?.GetValue<int>() == checkpoint.Sequence && entry["hash"]?.ToString() == checkpoint.PlanHash))
            .OfType<PlanningConstructionCheckpoint>().Take(4);
        var available = requested.Concat(checkpoints.TakeLast(4)).DistinctBy(checkpoint => checkpoint.Sequence).ToArray();
        return new JsonObject
        {
            ["activeCount"] = active.Length, ["id"] = item.Id, ["status"] = item.Status,
            ["scope"] = item.Consumer.Scope, ["requirementIds"] = new JsonArray(item.Consumer.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["objective"] = item.Consumer.Objective, ["prerequisite"] = PlanningConstruction.Node(item.Prerequisite, PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite),
            ["evidence"] = "rejected:" + item.RejectedStepIndex,
            ["canAttemptResume"] = item.Status == "waiting" && ContinuationCurrent(state, item) && item.LastPrerequisiteCheckpoint > item.SuspendedAtCheckpoint,
            ["basis"] = "model_authored_navigation", ["bindingValidation"] = ContinuationValidation,
            ["directory"] = item.LastPrerequisiteCheckpoint > item.SuspendedAtCheckpoint ? "page:checkpoints:" + item.SuspendedAtCheckpoint : null,
            ["acceptedCheckpoints"] = new JsonArray(available.Select(checkpoint => (JsonNode)new JsonObject
                { ["sequence"] = checkpoint.Sequence, ["evidence"] = "page:checkpoints:" + (checkpoint.Sequence - 1) }).ToArray())
        };
    }

    private static JsonObject? DraftContinuationContext(PlanningSession state)
    {
        if (!UsesConvergentConstruction(state) || State(state).SuspendedSteps?.LastOrDefault(Active) is not { } item ||
            !ContinuationCurrent(state, item)) return null;
        // Only the current prerequisite chain is relevant. Keep its exact proposed
        // need distinct from original obligations and from the current draft.
        var value = new JsonObject { ["id"] = item.Consumer.Id, ["scope"] = item.Consumer.Scope,
            ["objective"] = item.Consumer.Objective,
            ["requirementIds"] = new JsonArray(item.Consumer.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        var original = value.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(original);
        return new JsonObject
        {
            ["id"] = item.Id, ["status"] = item.Status, ["basis"] = "model_authored_navigation",
            ["bindingValidation"] = ContinuationValidation,
            ["consumer"] = new JsonObject { ["hash"] = PlanningGraphCompiler.Fingerprint(original), ["bytes"] = bytes,
                ["omitted"] = bytes > 1024, ["value"] = bytes <= 1024 ? value : null },
            // The proposed type is mandatory context, never a cropped schema.
            // Existing admission rejects the full request if it exceeds its ceiling.
            ["prerequisite"] = PlanningConstruction.Node(item.Prerequisite, PlanningJsonContext.Default.PlanningConstructionTypedPrerequisite),
            // This existing address retrieves the consumer's rejection, not the
            // separately retained typed prerequisite. Do not promise otherwise.
            ["evidence"] = "rejected:" + item.RejectedStepIndex, ["evidenceContent"] = "consumer_reselection"
        };
    }

    internal static void ReconcileContinuations(PlanningSession state)
    {
        if (!UsesConvergentConstruction(state) || state.PendingCall is not null) return;
        var compact = State(state);
        foreach (var item in compact.SuspendedSteps ?? [])
        {
            if (Active(item) && (!ContinuationCurrent(state, item) || item.ParentId is { } parent &&
                    compact.SuspendedSteps!.Any(ancestor => ancestor.Id == parent && ancestor.Status is "invalidated" or "abandoned")))
            {
                item.Status = "invalidated";
                if (compact.ResumingStepId == item.Id) compact.ResumingStepId = null;
            }
            else if (item.Status == "resuming" && compact.ResumingStepId == item.Id && compact.CurrentStep is null)
            {
                // Rollback clears the tentative selection, but an independent
                // accepted producer remains eligible for explicit selection.
                item.Status = "waiting";
                compact.ResumingStepId = null;
            }
        }
    }

    private static void AcceptContinuation(PlanningSession state)
    {
        if (!UsesConvergentConstruction(state)) return;
        var compact = State(state); var count = state.Construction!.Checkpoints.Count;
        if (compact.ResumingStepId is { } id && compact.SuspendedSteps?.SingleOrDefault(item => item.Id == id && item.Status == "resuming") is { } resumed)
            resumed.Status = "completed";
        compact.ResumingStepId = null;
        foreach (var item in compact.SuspendedSteps ?? [])
            if (Active(item))
            {
                if (item.Consumer.RequirementIds.All(id => state.Construction.Requirements.Single(requirement => requirement.Id == id).Status == "closed"))
                    item.Status = "requirements_closed";
                else item.LastPrerequisiteCheckpoint = count;
            }
        ReconcileContinuations(state);
    }

    private static bool ContinuationCurrent(PlanningSession state, PlanningConstructionSuspendedStep item)
    {
        var compact = State(state); var construction = state.Construction!;
        if (!construction.OpenScopes.Contains(item.Consumer.Scope, StringComparer.Ordinal) ||
            item.Consumer.RequirementIds.Any(id => construction.Requirements.All(requirement => requirement.Id != id)) ||
            item.Consumer.RequirementIds.All(id => construction.Requirements.Single(requirement => requirement.Id == id).Status == "closed") ||
            item.SourceVersions.Any(source => compact.Sources.SingleOrDefault(current => current.Id == source.Key) is not { } current ||
                current.Version != source.Value || PlanningGraphCompiler.Fingerprint(current.Text) != source.Value)) return false;
        var symbols = new TaskPlanSymbols(state.Plan ?? new());
        if (item.DependencyHashes.Any(dependency => !symbols.Tasks.TryGetValue(dependency.Key, out var task) || TaskHash(task.Task) != dependency.Value)) return false;
        if (item.GroupHash is { } groupHash && (item.Consumer.GroupId is not { } groupId ||
            state.Plan?.Groups.SingleOrDefault(group => group.Id == groupId) is not { } group ||
            PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(group, PlanningJsonContext.Default.TaskGroup)!.ToJsonString()) != groupHash)) return false;
        try
        {
            foreach (var (identity, hash) in item.ContractHashes)
            {
                var matches = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
                    .Where(capability => { var operation = TaskOperations.Describe(capability); return ContractIdentity(operation.Id, operation.Version) == identity; })
                    .ToArray();
                if (matches.Select(ContractHash).Distinct(StringComparer.Ordinal).Count() != 1) return false;
                matches = matches.DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
                if (matches.Length != 1 || ContractHash(matches[0]) != hash) return false;
                var operation = TaskOperations.Describe(matches[0]);
                _ = ExactContract(state, operation.Id);
            }
            foreach (var (address, hash) in item.EvidenceHashes)
            {
                if (address.StartsWith("requirement:", StringComparison.Ordinal) || address.StartsWith("source:", StringComparison.Ordinal))
                { if (EvidenceHash(state, address) != hash) return false; }
                else
                {
                    var retained = compact.RejectedSteps[item.RejectedStepIndex]["evidence"]?[address];
                    if (retained is null || PlanningGraphCompiler.Fingerprint(retained.ToJsonString()) != hash) return false;
                    var current = LoadContractEvidence(state, address, recordSchemaSelection: false);
                    if (address.StartsWith("contract:", StringComparison.Ordinal)
                        ? current?["hash"]?.ToString() != retained["hash"]?.ToString()
                        : !JsonNode.DeepEquals(retained, current)) return false;
                }
            }
        }
        catch (PlanningResponseException) { return false; }
        return true;
    }

    private static string EvidenceHash(PlanningSession state, string address)
    {
        JsonNode? evidence;
        if (address.StartsWith("requirement:", StringComparison.Ordinal))
        {
            var requirement = state.Requirements!.Outcomes.SingleOrDefault(requirement => requirement.Id == address[12..]);
            evidence = requirement is null ? null : new JsonObject { ["id"] = requirement.Id, ["original"] = requirement.Description };
        }
        else if (address.StartsWith("source:", StringComparison.Ordinal)) evidence = State(state).Sources.Any(source => source.Id == address[7..])
            ? Sources(state, [address[7..]]).Single() : null;
        else evidence = state.Construction!.Evidence?[address];
        if (evidence is null) PlanningConstruction.Fail("CONSTRUCTION_CONTINUATION_INVALID", "/reselectStep/prerequisite", "Exact prerequisite evidence is unavailable.");
        return PlanningGraphCompiler.Fingerprint(evidence!.ToJsonString());
    }

    private static string TaskHash(PlanTask task) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask));
    private static PlanningConstructionStep CloneStep(PlanningConstructionStep step) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep), PlanningJsonContext.Default.PlanningConstructionStep)!;
}
