using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static IEnumerable<string> PrerequisiteAddresses(PlanningSession state, IEnumerable<string> requirementIds)
    {
        var compact = State(state);
        var requirements = requirementIds.ToArray();
        return requirements.Select(id => "requirement:" + id)
            .Concat(requirements.SelectMany(id => compact.RequirementSources.GetValueOrDefault(id) ?? [])
                .Concat(compact.GlobalSourceIds).Select(id => "source:" + id))
            .Concat((state.Construction!.Evidence ?? []).Select(item => item.Key).Where(address =>
                address.StartsWith("schema:", StringComparison.Ordinal) || address.StartsWith("description:", StringComparison.Ordinal) ||
                address.StartsWith("contract:", StringComparison.Ordinal)))
            .Where(address => address.Length <= 2048).Distinct(StringComparer.Ordinal);
    }

    internal static void ConstrainSelectionEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var requirements = (context["requirements"] as JsonArray ?? []).Select(item => item!["id"]!.ToString())
            .Concat(EvidenceRequirementIds(state));
        var addresses = PrerequisiteAddresses(state, requirements).Where(IsOriginalAddress).ToHashSet(StringComparer.Ordinal);
        addresses.UnionWith(NavigationEvidence(new[] { "nextRequirements", "requirementsDirectory", "nextScopes", "scopes", "interfaces",
            "nextInterfaces", "publicOutputs", "rebuild", "reselection" }.Select(name => context[name])
            .Concat((state.Construction!.Evidence ?? []).Select(item => item.Value).Where(value => value is JsonObject or JsonArray))));
        if (UsesConvergentConstruction(state))
        {
            addresses.UnionWith(NavigationEvidence([context["continuation"]]));
            addresses.UnionWith(NavigationEvidence((context["continuation"]?["acceptedCheckpoints"] as JsonArray ?? []).Cast<JsonNode?>()));
            addresses.UnionWith(NavigationEvidence((context["scopes"] as JsonArray ?? []).Select(scope => scope?["acceptedExports"])));
        }
        ConstrainEvidenceChoices(state, schema, addresses, maximum: 4);
    }

    private static HashSet<string> StepOriginalEvidence(PlanningSession state)
    {
        var addresses = PrerequisiteAddresses(state, State(state).CurrentStep!.RequirementIds)
            .Where(IsOriginalAddress).ToHashSet(StringComparer.Ordinal);
        var citations = State(state).CurrentStep!.Prerequisites?.SelectMany(note => note.EvidenceAddresses) ?? [];
        addresses.UnionWith(citations.Intersect(PrerequisiteAddresses(state,
            state.Construction!.Requirements.Select(requirement => requirement.Id)), StringComparer.Ordinal).Where(IsOriginalAddress));
        return addresses;
    }

    internal static void ConstrainDraftEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        // Original requirement/source identities are already present in this
        // context. Contract addresses come only from host-owned view navigation.
        var addresses = StepOriginalEvidence(state);
        addresses.UnionWith(NavigationEvidence(new[] { "scopeEvidence", "interfacesDirectory", "reusableInterfaces", "taskEvidence", "scopeInterface",
            "operations", "companionOperations", "rebuild", "continuation" }.Select(name => context[name])
            .Append(context["taskEvidence"]?["scopeOwner"])
            .Concat((state.Construction!.Evidence ?? []).Select(item => item.Value).Where(value => value is JsonObject or JsonArray))));
        ConstrainEvidenceChoices(state, schema, addresses, maximum: HasEvidenceAdmissionLimit(state) ? 1 : 8);
    }

    internal static bool IsEvidenceAdmissionLimit(PlanningDiagnostic diagnostic) => diagnostic.Required &&
        diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT" && diagnostic.Location == "/evidenceRequests";

    private static bool HasEvidenceAdmissionLimit(PlanningSession state) => UsesVerifiedReview(state) &&
        PlanningConstructionPhasePolicies.IsFresh(state.Request) && state.Diagnostics.Any(IsEvidenceAdmissionLimit);

    internal static bool EvidenceAdmissionOnly(PlanningSession state) => HasEvidenceAdmissionLimit(state) &&
        state.Construction!.Candidate is null && state.Diagnostics.Where(diagnostic => diagnostic.Required).All(IsEvidenceAdmissionLimit);

    private static void ConstrainDiscoveryEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var addresses = StepOriginalEvidence(state);
        addresses.UnionWith(NavigationEvidence(new[] { context["descriptors"], context["providerRequests"], context["scopeOwner"] }
            .Concat((context["directoryEvidence"] as JsonObject ?? []).Select(item => item.Value))
            .Concat((context["evidence"] as JsonObject ?? []).Select(item => item.Value))));
        if (UsesConvergentConstruction(state))
            addresses.UnionWith(NavigationEvidence([context["describedOperations"]]));
        // DiscoveryEvidence only omits original descriptions when the existing
        // exact single-read knowledge guard permits them to be restored.
        var rereads = (context["evidence"] as JsonObject ?? []).Where(item =>
                item.Key.StartsWith("description:", StringComparison.Ordinal) &&
                item.Value?["textOmitted"]?.GetValue<bool>() == true && item.Value?["evidence"]?.ToString() == item.Key)
            .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        addresses.RemoveWhere(address => state.Construction!.EvidenceRequests.Contains(
            PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal));
        var maximum = State(state).PresentationVersion is 4 or 5 && state.Diagnostics.Any(diagnostic =>
            diagnostic.Required && diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT" && diagnostic.Location == "/evidenceRequests") ? 1 : 8;
        var alternatives = new JsonArray();
        if (addresses.Count > 0)
            alternatives.Add((JsonNode)PlanningSchemas.Array(PlanningSchemas.Enum(addresses.Order(StringComparer.Ordinal).ToArray()), 1, maximum));
        if (rereads.Length > 0)
            alternatives.Add((JsonNode)PlanningSchemas.Array(PlanningSchemas.Enum(rereads), 1, 1));
        alternatives.Add((JsonNode)PlanningSchemas.Type("null"));
        schema["properties"]!["evidenceRequests"] = alternatives.Count == 1 ? PlanningSchemas.Type("null") :
            new JsonObject { ["anyOf"] = alternatives };
    }

    private static bool IsOriginalAddress(string address) => address.StartsWith("requirement:", StringComparison.Ordinal) ||
        address.StartsWith("source:", StringComparison.Ordinal);

    private static void ConstrainEvidenceChoices(PlanningSession state, JsonObject schema, HashSet<string> addresses, int maximum)
    {
        // Retained evidence is already supplied, never a new request or progress.
        addresses.ExceptWith((state.Construction!.Evidence ?? []).Select(item => item.Key));
        schema["properties"]!["evidenceRequests"] = addresses.Count == 0 ? PlanningSchemas.Type("null") :
            PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses.Order(StringComparer.Ordinal).ToArray()), 1, maximum));
    }

    private static HashSet<string> NavigationEvidence(IEnumerable<JsonNode?> nodes)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node is JsonValue) Address(node); else Navigation(node);
        return addresses;

        void Address(JsonNode? node)
        {
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var address) && address.Length <= 2048 &&
                new[] { "source:", "source-description:", "requirement:", "input:", "output:", "choice:", "group:", "task:", "scope:", "scope-exports:",
                    "task-objective:", "page:", "schema:", "schema-page:", "port-page:", "description:", "contract:", "original:", "originalScope:", "rejected:" }
                    .Any(prefix => address.StartsWith(prefix, StringComparison.Ordinal)))
                addresses.Add(address);
        }
        void Navigation(JsonNode? node)
        {
            if (node is JsonArray list) { foreach (var item in list) Navigation(item); return; }
            if (node is not JsonObject value) return;
            foreach (var name in new[] { "address", "evidence", "next", "nextPorts", "objectiveEvidence", "remainingEvidence", "scopeEvidence",
                         "descriptionEvidence", "schemaEvidence", "directory", "inputDirectory", "outputDirectory", "outputSchemaDirectory", "schema" })
                Address(value[name]);
            // Traverse only metadata containers generated by the host. Never walk
            // original schemas, constraints, required sibling values, references,
            // bindings or free text: their property names are not link authority.
            foreach (var name in new[] { "items", "inputs", "outputs", "groups", "tasks", "ancestors", "selectedOutputs" })
                if (value[name] is JsonArray entries) foreach (var entry in entries) Navigation(entry);
            foreach (var name in new[] { "scopes", "omittedOptionalInputs", "omittedOptionalSiblings" })
                if (value[name] is JsonArray entries) foreach (var entry in entries) Address(entry);
        }
    }

    private static JsonObject TrimNavigation(JsonObject value)
    {
        foreach (var key in value.Where(p => p.Value is null or JsonArray { Count: 0 } ||
                     p.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length == 0)
                     .Select(p => p.Key).ToArray()) value.Remove(key);
        return value;
    }

    private static JsonObject? ScopeOwnerNavigation(PlanningSession state, TaskPlanSymbols? accepted = null)
    {
        if (!UsesVerifiedReview(state) || state.Plan is null) return null;
        accepted ??= new TaskPlanSymbols(state.Plan);
        var owner = accepted.Scopes.SingleOrDefault(scope => scope.Path == State(state).CurrentStep!.Scope)?.Owner;
        if (owner is null) return null;
        var entry = TrimNavigation(TaskNavigation(state, owner, accepted.Tasks[owner.Id].Scope.Path));
        entry["address"] = "task:" + owner.Id;
        return entry;
    }

    private static JsonObject TaskNavigation(PlanningSession state, PlanTask task, string scope)
    {
        var description = NavigationDescription(task.Objective);
        var entry = new JsonObject { ["task"] = task.Id, ["scope"] = scope, ["kind"] = task.Kind,
            ["objective"] = description, ["objectiveComplete"] = description == task.Objective,
            ["objectiveEvidence"] = description == task.Objective ? null : TaskObjectiveAddress(task, 0) };
        if (IsCompactPresentation(state) && PlanningConstructionPhasePolicies.IsFresh(state.Request))
        {
            if (task.Kind == "call" && task.Group is not null) entry["group"] = task.Group;
            if (task.Kind == "operation" && task.Operation is not null) entry["operation"] = task.Operation;
        }
        return entry;
    }

    private static void AddAcceptedOperationEvidence(PlanningSession state, JsonObject context, IEnumerable<JsonObject> entries)
    {
        if (!UsesConvergentConstruction(state) || state.Plan is null) return;
        var accepted = new TaskPlanSymbols(state.Plan);
        var rows = entries.ToArray();
        AddAcceptedProducerPorts(state, rows, ImportedProducerIds(context["interfaces"] as JsonArray ?? []));
        var added = new JsonObject();
        foreach (var entry in rows)
        {
            if (entry["task"]?.ToString() is not { } id || !accepted.Tasks.TryGetValue(id, out var site) ||
                site.Task.Kind != "operation" || entry["scope"]?.ToString() != site.Scope.Path) continue;
            var address = "task:" + id;
            // Retained evidence remains authoritative. These context-only originals
            // neither change the evidence journal nor establish retrieval progress.
            if (context["evidence"] is JsonObject retained && retained.ContainsKey(address)) continue;
            var original = PlanningConstruction.TaskNode(site.Task);
            original["scope"] = site.Scope.Path;
            original["phase"] = accepted.Phase(id);
            if (added.Count < 3)
            {
                added[address] = original;
                if (Encoding.UTF8.GetByteCount(added.ToJsonString()) <= 1024) continue;
                added.Remove(address);
            }
            // A partial projection must never occupy an original task address.
            // Keep whole omitted fields retrievable through the existing link.
            entry["omittedFields"] = new JsonArray(original.AsObject().Where(field =>
                field.Key is not ("id" or "kind" or "operation" or "scope" or "phase") &&
                !(field.Key == "objective" && entry["objectiveComplete"]?.GetValue<bool>() == true))
                .Select(field => (JsonNode?)JsonValue.Create(field.Key)).ToArray());
        }
        if (added.Count == 0) return;
        var evidence = (context["evidence"] ??= new JsonObject()).AsObject();
        foreach (var (address, original) in added) evidence[address] = original!.DeepClone();
    }

    private static HashSet<string> EvidenceRequirementIds(PlanningSession state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
        {
            var records = address.StartsWith("page:requirements:", StringComparison.Ordinal) ? value?["items"] as JsonArray
                : address.StartsWith("requirement:", StringComparison.Ordinal) ? value as JsonArray : null;
            if (records is null) continue;
            foreach (var record in records)
            {
                var id = record?["id"]?.ToString();
                if (id is not null && JsonNode.DeepEquals(record, RequirementNodes(state, [id]).FirstOrDefault())) ids.Add(id);
            }
        }
        return ids;
    }

    private static PlanningRequirement[] RequirementPage(PlanningSession state, int offset)
    {
        var outcomes = state.Requirements!.Outcomes;
        var first = outcomes[offset];
        if (!PlanningConstructionPhasePolicies.IsFresh(state.Request)) return [first];
        var page = OriginalPage(first.Id);
        if (page is null) return [first];
        var selected = new List<PlanningRequirement> { first };
        var text = first.Description;
        foreach (var requirement in outcomes.Skip(offset + 1).Take(7))
        {
            if (OriginalPage(requirement.Id) != page) break;
            var combined = text + requirement.Description;
            // Keep whole original obligations. The first, potentially indivisible
            // requirement is admitted by the complete-request ceiling as usual.
            if (OriginalPageLength(combined, 0) != combined.Length) break;
            selected.Add(requirement);
            text = combined;
        }
        return selected.ToArray();

        string? OriginalPage(string requirementId)
        {
            var compact = State(state);
            if (!compact.RequirementSources.TryGetValue(requirementId, out var sources) || sources.Count == 0) return null;
            string? parent = null;
            foreach (var id in sources)
            {
                var separator = id.LastIndexOf(":clause:", StringComparison.Ordinal);
                if (separator < 0) return null;
                var candidate = id[..separator];
                if (!compact.Sources.Any(source => source.Id == candidate &&
                        source.Kind is "page" or "answer_page" or "revision_page") ||
                    parent is not null && parent != candidate) return null;
                parent = candidate;
            }
            return parent;
        }
    }

    private static IEnumerable<string> NavigationPorts(PlanningSession state, PlanTask task)
    {
        if (task.Kind != "call") return PlanningRepairContext.Ports(task, state);
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        var group = plan?.Groups.SingleOrDefault(g => g.Id == task.Group);
        return group is null || (state.Construction!.Candidate?.OpenScopes ?? state.Construction.OpenScopes).Contains("/groups/" + group.Id + "/body", StringComparer.Ordinal)
            ? [] : group.Body.Outputs.Select(output => output.Name);
    }

    private static string TaskPortsVersion(IEnumerable<string> ports) => PlanningGraphCompiler.Fingerprint(new JsonArray(ports.Select(port => (JsonNode?)JsonValue.Create(port)).ToArray()).ToJsonString());
    private static string TaskPortsAddress(string id, IEnumerable<string> ports, int offset) => "page:task-ports:" + Uri.EscapeDataString(id) + "@" + TaskPortsVersion(ports) + ":" + offset.ToString(CultureInfo.InvariantCulture);
    private static string TaskObjectiveAddress(PlanTask task, int offset) => "task-objective:" + Uri.EscapeDataString(task.Id) + "@" +
        PlanningGraphCompiler.Fingerprint(task.Objective) + ":" + offset.ToString(CultureInfo.InvariantCulture);

    private static bool LoadTaskNavigationEvidence(PlanningSession state, string address, out JsonNode? value)
    {
        value = null;
        var objective = address.StartsWith("task-objective:", StringComparison.Ordinal);
        var ports = address.StartsWith("page:task-ports:", StringComparison.Ordinal);
        if (!objective && !ports) return false;
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        if (plan is null) return true;
        var tail = address[(objective ? "task-objective:".Length : "page:task-ports:".Length)..];
        var separator = tail.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(tail[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0) return true;
        var identity = tail[..separator];
        var versionSeparator = identity.LastIndexOf('@');
        if (versionSeparator <= 0) return true;
        var encoded = identity[..versionSeparator];
        var id = Uri.UnescapeDataString(encoded);
        if (Uri.EscapeDataString(id) != encoded || !new TaskPlanSymbols(plan).Tasks.TryGetValue(id, out var site)) return true;
        var task = site.Task;
        if (objective)
        {
            if (address != TaskObjectiveAddress(task, offset) || offset > task.Objective.Length) return true;
            var length = OriginalPageLength(task.Objective, offset);
            value = new JsonObject { ["task"] = task.Id, ["version"] = PlanningGraphCompiler.Fingerprint(task.Objective),
                ["offset"] = offset, ["totalCharacters"] = task.Objective.Length, ["text"] = task.Objective.Substring(offset, length),
                ["next"] = offset + length < task.Objective.Length ? TaskObjectiveAddress(task, offset + length) : null };
        }
        else
        {
            var names = NavigationPorts(state, task).ToArray();
            if (address != TaskPortsAddress(task.Id, names, offset)) return true;
            if (offset >= names.Length && offset != 0) return true;
            value = new JsonObject { ["task"] = task.Id, ["version"] = TaskPortsVersion(names), ["offset"] = offset, ["total"] = names.Length,
                ["ports"] = new JsonArray(names.Skip(offset).Take(8).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                ["next"] = offset + 8 < names.Length ? TaskPortsAddress(task.Id, names, offset + 8) : null };
        }
        return true;
    }
}
