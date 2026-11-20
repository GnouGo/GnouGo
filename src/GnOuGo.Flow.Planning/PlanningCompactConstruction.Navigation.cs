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
            .Where(address => address.Length <= 2048 || PlanningTokenThresholds.OptimizationAdvisory(state.Request)).Distinct(StringComparer.Ordinal);
    }

    internal static void ConstrainSelectionEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var requirements = (context["requirements"] as JsonArray ?? []).Select(item => item!["id"]!.ToString())
            .Concat(EvidenceRequirementIds(state));
        var addresses = PrerequisiteAddresses(state, requirements).Where(IsOriginalAddress).ToHashSet(StringComparer.Ordinal);
        addresses.UnionWith(ProgressEvidence(state, context));
        addresses.UnionWith(NavigationEvidence(state, new[] { "nextRequirements", "requirementsDirectory", "nextScopes", "scopes", "interfaces",
            "nextInterfaces", "publicOutputs", "rebuild", "reselection", "reselectionHistory" }.Select(name => context[name])
            .Concat((state.Construction!.Evidence ?? []).Select(item => item.Value).Where(value => value is JsonObject or JsonArray))));
        {
            addresses.UnionWith(NavigationEvidence(state, [context["interfacesDirectory"], context["continuation"]]));
            addresses.UnionWith(NavigationEvidence(state, (context["continuation"]?["acceptedCheckpoints"] as JsonArray ?? []).Cast<JsonNode?>()));
            addresses.UnionWith(NavigationEvidence(state, (context["scopes"] as JsonArray ?? []).Select(scope => scope?["acceptedExports"])));
        }
        {
            addresses.UnionWith(NavigationEvidence(state, [context["knownOperations"]]));
            addresses.UnionWith(NavigationEvidence(state, (context["requirements"] as JsonArray ?? []).Select(row => row?["acceptedProgress"])));
        }
        if (context["knownOperations"] is JsonObject directory && directory["next"]?.ToString() is { } next && context["evidence"]?[next] is JsonObject shown &&
            JsonNode.DeepEquals(shown, LoadKnownOperationPage(state, next)))
            addresses.Remove(next);
        if (State(state).CurrentStep is null) addresses.RemoveWhere(address => !AvailableBeforeSelection(state, address));
        ConstrainEvidenceChoices(state, schema, addresses, maximum: 4);
    }

    private static bool AvailableBeforeSelection(PlanningSession state, string address)
    {
        var separator = address.IndexOf(':');
        if (separator < 0) return true;
        var kind = address[..separator];
        if (kind is not ("description" or "contract" or "schema" or "schema-page" or "port-page")) return true;
        var rest = address[(separator + 1)..];
        var end = rest.IndexOf(':');
        var identity = end < 0 ? rest : rest[..end];
        var at = identity.IndexOf('@');
        if (at <= 0 || at == identity.Length - 1) return false;
        var operation = Uri.UnescapeDataString(identity[..at]);
        var version = Uri.UnescapeDataString(identity[(at + 1)..]);
        if (identity != ContractIdentity(operation, version)) return false;
        var tail = end < 0 ? "" : rest[(end + 1)..];
        try { return CanInspectProducerContractEvidence(state, ExactContract(state, operation, version), kind, tail); }
        catch (PlanningResponseException) { return false; }
    }

    private static void AddRequirementNavigation(PlanningSession state, JsonObject context)
    {
        if (!state.Requirements!.Outcomes.Any(requirement =>
                !State(state).CurrentStep!.RequirementIds.Contains(requirement.Id, StringComparer.Ordinal))) return;
        var evidence = state.Construction!.Evidence;
        if (evidence?.ContainsKey("page:requirements:0") != true)
            context["requirementsDirectory"] = "page:requirements:0";
        var next = RequirementPage(state, 0).Length;
        if (next < state.Requirements.Outcomes.Count && evidence?.ContainsKey("page:requirements:" + next) != true)
            context["nextRequirements"] = "page:requirements:" + next;
        // Requested pages retain their exact next links. Existing evidence
        // navigation follows them without another cursor or repeat-read rule.
    }

    private static HashSet<string> StepOriginalEvidence(PlanningSession state)
    {
        var addresses = PrerequisiteAddresses(state, State(state).CurrentStep!.RequirementIds)
            .Where(IsOriginalAddress).ToHashSet(StringComparer.Ordinal);
        var citations = CurrentPrerequisiteAddresses(state);
        addresses.UnionWith(citations.Intersect(PrerequisiteAddresses(state,
            state.Construction!.Requirements.Select(requirement => requirement.Id)), StringComparer.Ordinal).Where(IsOriginalAddress));
        return addresses;
    }

    internal static void ConstrainDraftEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        // Original requirement/source identities are already present in this
        // context. Contract addresses come only from host-owned view navigation.
        var addresses = StepOriginalEvidence(state);
        addresses.UnionWith(PrerequisiteAddresses(state, (context["requirements"] as JsonArray ?? []).Select(row => row!["id"]!.ToString())).Where(IsOriginalAddress));
        addresses.UnionWith(NavigationEvidence(state, [context["requirementsDirectory"], context["nextRequirements"]]));
        addresses.UnionWith(NavigationEvidence(state, new[] { "scopeEvidence", "interfacesDirectory", "reusableInterfaces", "taskEvidence", "scopeInterface",
            "operations", "companionOperations", "rebuild", "continuation", "reselection" }.Select(name => context[name])
            .Append(context["taskEvidence"]?["scopeOwner"])
            .Concat((state.Construction!.Evidence ?? []).Select(item => item.Value).Where(value => value is JsonObject or JsonArray))));
        addresses.UnionWith(NavigationEvidence(state, [context["interfaces"]]));
        addresses.UnionWith(NavigationEvidence(state, [context["repair"]?["contracts"]]));
        addresses.UnionWith(NavigationEvidence(state, (context["requirements"] as JsonArray ?? []).Select(row => row?["acceptedProgress"])));
        var findingEvidence = HybridWorkflowPlanner.BusinessReviewReconsiderationEvidence(state);
        if (findingEvidence.Length > 0)
            context["findingEvidence"] = new JsonArray(findingEvidence.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray());
        foreach (var global in (context["globalConstraints"] as JsonArray ?? []).OfType<JsonObject>())
            RemoveDisplayedClause(state, addresses, global, global["text"]);
        RemoveDisplayedSelectedOutputs(state, context, addresses);
        var visibleTaskLinks = NavigationEvidence(state, [context["taskEvidence"]]);
        if (context["scopeContext"] is JsonObject scope && scope["path"]?.ToString() is { } path &&
            context["scopeEvidence"]?.ToString() == "scope:" + path && scope["taskCount"]?.GetValue<int>() > 0)
        {
            if (CompleteScopePage(scope))
                addresses.Remove("scope:" + path);
            RemoveDisplayedTasks(scope);
        }
        if (context["evidence"] is JsonObject evidence)
        {
            TaskPlanSymbols? accepted = null;
            foreach (var (address, value) in evidence)
            {
                if (address.StartsWith("scope:", StringComparison.Ordinal) && value is JsonObject loaded &&
                    loaded["path"]?.ToString() is { } loadedPath && address == "scope:" + loadedPath &&
                    JsonNode.DeepEquals(loaded, PlanningConstruction.ScopeEvidence(state, loadedPath)))
                {
                    RemoveDisplayedTasks(loaded);
                    if (state.Construction.Evidence?.ContainsKey(address) != true && CompleteScopePage(loaded))
                        addresses.Remove(address);
                }
                if (!address.StartsWith("task:", StringComparison.Ordinal) || value is not JsonObject declaration ||
                    state.Construction.Candidate is not null || state.Plan is null || !PlanningTokenThresholds.OptimizationAdvisory(state.Request) ||
                    declaration["id"]?.ToString() is not { } id || address != "task:" + id ||
                    !addresses.Contains(address) || state.Construction.Evidence?.ContainsKey(address) == true) continue;
                accepted ??= new TaskPlanSymbols(state.Plan);
                if (!accepted.Tasks.ContainsKey(id)) continue;
                var original = PlanningConstruction.LocatedTaskNode(accepted, id);
                if (JsonNode.DeepEquals(value, original) && ChildrenReachable(declaration)) addresses.Remove(address);
            }
        }
        ConstrainEvidenceChoices(state, schema, addresses, maximum: HasEvidenceAdmissionLimit(state) ? 1 : 8,
            supportedRereads: findingEvidence);

        void RemoveDisplayedTasks(JsonObject displayed)
        {
            foreach (var task in displayed["tasks"]!.AsArray().Concat(displayed["always"]!.AsArray()).OfType<JsonObject>())
            {
                if (task["id"]?.ToString() is not { } id) continue;
                if (ChildrenReachable(task)) addresses.Remove("task:" + id);
                if (task["objective"] is JsonValue objective && objective.TryGetValue<string>(out var text))
                {
                    var prefix = TaskObjectivePrefix(id, text);
                    addresses.RemoveWhere(address => address.StartsWith(prefix, StringComparison.Ordinal));
                }
            }
        }

        bool ChildrenReachable(JsonObject task) => task["scopes"] is not JsonArray children || children.All(child =>
            visibleTaskLinks.Contains("scope:" + child) || child?.ToString() is { } path && context["evidence"]?["scope:" + path] is JsonObject shown &&
            JsonNode.DeepEquals(shown, PlanningConstruction.ScopeEvidence(state, path)));
    }

    internal static bool IsEvidenceAdmissionLimit(PlanningDiagnostic diagnostic) => diagnostic.Required &&
        diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT" && diagnostic.Location == "/evidenceRequests";

    private static bool HasEvidenceAdmissionLimit(PlanningSession state) => state.Diagnostics.Any(IsEvidenceAdmissionLimit);

    internal static bool EvidenceAdmissionOnly(PlanningSession state) => HasEvidenceAdmissionLimit(state) &&
        state.Construction!.Candidate is null && state.Diagnostics.Where(diagnostic => diagnostic.Required).All(IsEvidenceAdmissionLimit);

    private static void RemoveDisplayedSelectedOutputs(PlanningSession state, JsonObject context, HashSet<string> addresses)
    {
        var selected = State(state).CurrentStep?.SchemaPointers;
        if (selected is not { Count: > 0 }) return;
        var citations = CurrentPrerequisiteAddresses(state);
        foreach (var operation in (context["operations"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (operation["selectedOutputs"] is not JsonArray outputs || operation["id"]?.ToString() is not { } id ||
                operation["version"]?.ToString() is not { } version) continue;
            var identity = ContractIdentity(id, version);
            if (!ContractEvidenceCurrent(state, "contract:" + identity, operation)) continue;
            foreach (var view in outputs.OfType<JsonObject>())
            {
                if (view["pointer"]?.ToString() is not { } pointer) continue;
                var address = "schema:" + identity + ":" + pointer;
                if (!addresses.Contains(address) || !selected.Contains(address) || citations.Contains(address)) continue;
                var original = ExactSchemaView(ExactContract(state, id, version), pointer);
                // Compact selected views inherit only these identity, annotation and constraint fields.
                if (original.All(field => JsonNode.DeepEquals(field.Value, view.ContainsKey(field.Key) ? view[field.Key] : field.Key switch
                    {
                        "id" or "version" or "hash" => operation[field.Key],
                        "view" => operation["selectedSchemaView"],
                        "ancestors" or "referenceClosure" => operation["selectedOutputsSharedSchemaMetadata"]?[field.Key],
                        _ => null
                    })))
                    addresses.Remove(address);
            }
        }
    }

    private static void ConstrainDiscoveryEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var addresses = StepOriginalEvidence(state);
        {
            addresses.UnionWith(PrerequisiteAddresses(state, (context["requirements"] as JsonArray ?? []).Select(row => row!["id"]!.ToString())).Where(IsOriginalAddress));
            addresses.UnionWith(NavigationEvidence(state, [context["interfaces"], context["continuation"]]));
            addresses.UnionWith(NavigationEvidence(state, (context["continuation"]?["acceptedCheckpoints"] as JsonArray ?? []).Cast<JsonNode?>()));
        }
        addresses.UnionWith(NavigationEvidence(state, [context["requirementsDirectory"], context["nextRequirements"]]));
        addresses.UnionWith(NavigationEvidence(state, (context["requirements"] as JsonArray ?? []).Select(row => row?["acceptedProgress"])));
        addresses.UnionWith(NavigationEvidence(state, new[] { context["descriptors"], context["providerRequests"], context["scopeOwner"] }
            .Concat((context["directoryEvidence"] as JsonObject ?? []).Select(item => item.Value))
            .Concat((context["evidence"] as JsonObject ?? []).Select(item => item.Value))));
        addresses.UnionWith(NavigationEvidence(state, [context["describedOperations"], context["knownOperations"], context["reselection"]]));
        var cited = CurrentPrerequisiteAddresses(state);
        if (state.Discovery.Pages.Count > 0 && CanReselectDiscovery(state) && CompleteProviderMenu(state, context) is { } menu)
        {
            var inspected = state.Discovery.Pages.Select(page => page.SourceId).ToHashSet(StringComparer.Ordinal);
            // The complete canonical menu includes all actions and the versioned
            // original text. Keep the read when it can still unlock reselection.
            foreach (var row in menu.OfType<JsonObject>())
                if (row["descriptionComplete"]?.GetValue<bool>() == true &&
                    inspected.Contains(row["sourceId"]!.ToString()) && row["descriptionEvidence"]?.ToString() is { } address &&
                    !cited.Contains(address))
                    addresses.Remove(address);
        }
        // Clause evidence contains exactly its identity, version and original text.
        // Keep these identities available for citations even when no read is needed.
        foreach (var global in (context["globalConstraints"] as JsonArray ?? []).OfType<JsonObject>())
            RemoveDisplayedClause(state, addresses, global, global["text"], cited);
        foreach (var requirement in (context["requirements"] as JsonArray ?? []).OfType<JsonObject>())
            if (requirement["sources"] is JsonArray { Count: 1 } sources && sources[0] is JsonObject source)
                RemoveDisplayedClause(state, addresses, source, requirement["original"], cited);
        // DiscoveryEvidence only omits original descriptions when the existing
        // exact single-read knowledge guard permits them to be restored.
        var rereads = (context["evidence"] as JsonObject ?? []).Where(item =>
                item.Key.StartsWith("description:", StringComparison.Ordinal) &&
                item.Value?["textOmitted"]?.GetValue<bool>() == true && item.Value?["evidence"]?.ToString() == item.Key)
            .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        addresses.RemoveWhere(address => state.Construction!.EvidenceRequests.Contains(
            PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal));
        var maximum = state.Diagnostics.Any(diagnostic =>
            diagnostic.Required && diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT" && diagnostic.Location == "/evidenceRequests") ? 1 : 8;
        var alternatives = new JsonArray();
        if (addresses.Count > 0)
            alternatives.Add((JsonNode)PlanningSchemas.Array(PlanningSchemas.Enum(addresses.Order(StringComparer.Ordinal).ToArray()), 1, PlanningOptimizationThresholds.Maximum(state, maximum)));
        if (rereads.Length > 0)
            alternatives.Add((JsonNode)PlanningSchemas.Array(PlanningSchemas.Enum(rereads), 1, 1));
        alternatives.Add((JsonNode)PlanningSchemas.Type("null"));
        schema["properties"]!["evidenceRequests"] = alternatives.Count == 1 ? PlanningSchemas.Type("null") :
            new JsonObject { ["anyOf"] = alternatives };

    }

    private static void RemoveDisplayedClause(PlanningSession state, HashSet<string> addresses, JsonObject row, JsonNode? text,
        IReadOnlySet<string>? keep = null)
    {
        if (text is not JsonValue value || !value.TryGetValue<string>(out var displayed) || row["id"]?.ToString() is not { } id ||
            row["evidence"]?.ToString() is not { } address || address != "source:" + id || keep?.Contains(address) == true || !addresses.Contains(address)) return;
        var original = State(state).Sources.SingleOrDefault(clause => clause.Id == id);
        if (original is not null && original.Version == row["version"]?.ToString() && original.Text == displayed) addresses.Remove(address);
    }

    private static bool IsOriginalAddress(string address) => address.StartsWith("requirement:", StringComparison.Ordinal) ||
        address.StartsWith("source:", StringComparison.Ordinal);

    private static void ConstrainEvidenceChoices(PlanningSession state, JsonObject schema, HashSet<string> addresses, int maximum,
        IEnumerable<string>? supportedRereads = null)
    {
        // Retained evidence is already supplied, never a new request or progress.
        addresses.ExceptWith((state.Construction!.Evidence ?? []).Select(item => item.Key));
        if (supportedRereads is not null) addresses.UnionWith(supportedRereads);
        schema["properties"]!["evidenceRequests"] = addresses.Count == 0 ? PlanningSchemas.Type("null") :
            PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses.Order(StringComparer.Ordinal).ToArray()), 1, PlanningOptimizationThresholds.Maximum(state, maximum)));
    }

    private static HashSet<string> NavigationEvidence(PlanningSession state, IEnumerable<JsonNode?> nodes) =>
        NavigationEvidence(nodes, PlanningTokenThresholds.OptimizationAdvisory(state.Request));

    internal static HashSet<string> NavigationEvidence(IEnumerable<JsonNode?> nodes, bool advisoryOptimization = false)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node is JsonValue) Address(node); else Navigation(node);
        return addresses;

        void Address(JsonNode? node)
        {
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var address) &&
                (address.Length <= 2048 || advisoryOptimization) &&
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
            foreach (var name in new[] { "items", "inputs", "outputs", "groups", "tasks", "ancestors", "selectedOutputs", "selectedInputs", "producers", "directoryPages" })
                if (value[name] is JsonArray entries) foreach (var entry in entries) Navigation(entry);
            if (value["contract"] is JsonObject contract) Navigation(contract);
            if (value["omittedOptionalInputs"] is JsonArray optionalInputs)
                foreach (var entry in optionalInputs) Navigation(entry);
            foreach (var name in new[] { "scopes", "omittedOptionalSiblings" })
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
        if (state.Plan is null) return null;
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
            ["objective"] = description, [ObjectiveTextCompleteField(state)] = description == task.Objective,
            ["objectiveEvidence"] = description == task.Objective ? null : TaskObjectiveAddress(task, 0) };
        {
            if (task.Kind == "call" && task.Group is not null) entry["group"] = task.Group;
            if (task.Kind == "operation" && task.Operation is not null) entry["operation"] = task.Operation;
        }
        return entry;
    }

    private static string ObjectiveTextCompleteField(PlanningSession state) =>
        "objectiveTextComplete";

    private static void AddAcceptedTaskPreviews(PlanningSession state, JsonObject context, IEnumerable<JsonObject> entries,
        PlanningConstructionCheckpoint? continued = null)
    {
        if (state.Plan is null) return;
        var accepted = new TaskPlanSymbols(state.Plan);
        var rows = entries.ToArray();
        AddAcceptedProducerPorts(state, rows, ImportedProducerIds(context["interfaces"] as JsonArray ?? []));
        var maximumBytes = continued is null ? 1024 : ContinuedScopePreviewBytes - InspectedTaskBytes(state.Construction?.Evidence);
        if (continued is not null)
            rows = rows.OrderByDescending(row => continued.TaskIds.Contains(row["task"]!.ToString(), StringComparer.Ordinal))
                .ThenByDescending(row => row["phase"]?.ToString() == "always").ToArray();
        var shownScope = continued?.Step is { } step ? context["scopeContext"] ?? context["evidence"]?["scope:" + step.Scope] : null;
        var added = new JsonObject();
        foreach (var entry in rows)
        {
            if (entry["task"]?.ToString() is not { } id || !accepted.Tasks.TryGetValue(id, out var site) ||
                site.Task.Kind != "operation" && (continued?.TaskIds.Contains(id, StringComparer.Ordinal) != true ||
                    entry["phase"]?.ToString() == "always" && entry["scopeCount"]?.GetValue<int>() is not > 0) ||
                entry["scope"]?.ToString() != site.Scope.Path) continue;
            var address = "task:" + id;
            // Retained evidence remains authoritative. These context-only originals
            // neither change the evidence journal nor establish retrieval progress.
            if (context["evidence"] is JsonObject retained && retained.ContainsKey(address)) continue;
            var original = PlanningConstruction.TaskNode(site.Task);
            var phase = accepted.Phase(id);
            if (shownScope?["path"]?.ToString() == site.Scope.Path && shownScope[phase] is JsonArray displayed &&
                displayed.Any(task => JsonNode.DeepEquals(task, original))) continue;
            original["scope"] = site.Scope.Path;
            original["phase"] = phase;
            if (added.Count < 3)
            {
                added[address] = original;
                if (Encoding.UTF8.GetByteCount(added.ToJsonString()) <= maximumBytes) continue;
                added.Remove(address);
            }
            // A partial projection must never occupy an original task address.
            // Keep whole omitted fields retrievable through the existing link.
            entry["omittedFields"] = new JsonArray(original.AsObject().Where(field =>
                field.Key is not ("id" or "kind" or "operation" or "scope" or "phase") &&
                !(field.Key == "objective" && entry[ObjectiveTextCompleteField(state)]?.GetValue<bool>() == true))
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
        var page = OriginalPage(first.Id);
        if (page is null) return [first];
        var selected = new List<PlanningRequirement> { first };
        var text = first.Description;
        foreach (var requirement in outcomes.Skip(offset + 1).Take(7))
        {
            if (OriginalPage(requirement.Id) is null) break;
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
    private static string TaskObjectivePrefix(string id, string objective) =>
        "task-objective:" + Uri.EscapeDataString(id) + "@" + PlanningGraphCompiler.Fingerprint(objective) + ":";
    private static string TaskObjectiveAddress(PlanTask task, int offset) =>
        TaskObjectivePrefix(task.Id, task.Objective) + offset.ToString(CultureInfo.InvariantCulture);

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
