using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static GnOuGo.Flow.Core.Runtime.LLMRequest RepairRequest(PlanningSession state, PlanningSession authority, string? guidance = null)
    {
        var schema = PlanningSchemas.Proposal(authority);
        var fresh = PlanningConstructionPhasePolicies.IsFresh(state.Request);
        var singleView = !fresh && state.Construction is { Version: 2, Compact.PresentationVersion: 2 };
        schema["properties"]!["evidenceRequests"] = PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.String(), 1, singleView ? 1 : 8));
        schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("evidenceRequests"));
        if (state.Construction!.Version == 2)
        {
            schema["properties"]!["reselectStep"] = state.Construction.Candidate?.AuthorizedPaths is null
                ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["maxLength"] = 512 }) : PlanningSchemas.Type("null");
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("reselectStep"));
        }
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(schema);
        return Request(state, (guidance is null ? "" : guidance + "\n") + PlanningPrompt.RepairInstructions +
            (fresh
                ? "\nRequest up to eight related exact evidence addresses as the only action. The complete follow-up must fit its captured phase ceiling; evidence grants no edit authority."
                : singleView
                ? "\nRequest one exact original evidence address per call, with patch, discoveryRequests and clarifications null; evidence grants no additional edit authority."
                : "\nRequest original evidence using evidenceRequests with patch, discoveryRequests and clarifications null; evidence grants no additional edit authority.") +
            (state.Construction.Version == 2 ? " If the diagnosed tentative task kind or operation cannot implement the original requirement, reselectStep may give a concrete reason as the only action. It preserves accepted work and the selected scope/requirements; explicit revision paths cannot be expanded." : ""),
            _ => projected, state.Construction!.Limits.DraftOutputTokens, review: true,
            enrich: context => context["repair"] = PlanningRepairContext.Build(authority)).Request;
    }

    internal static void LoadEvidence(PlanningSession state, List<string> requests, string? descriptionReadKey = null,
        Func<PlanningSession, LLMRequest>? followup = null, string? purpose = null, JsonNode? issuedDiscoverySchema = null)
    {
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request))
        {
            // Resolve and admit on a clone: a rejected batch must not retain a
            // schema selection, page cursor or successful-read fingerprint.
            var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
                PlanningJsonContext.Default.PlanningSession)!;
            // First reads in a coherent batch retain the same issued knowledge
            // identity as singleton discovery reads. Later rereads still need
            // the existing singleton-only growth proof.
            var descriptionKeys = issuedDiscoverySchema is not null && requests.Count > 1
                ? requests.Where(address => address.StartsWith("description:", StringComparison.Ordinal))
                    .Select(address => PlanningCompactConstruction.DescriptionReadKey(probe, issuedDiscoverySchema,
                        new JsonObject { ["evidenceRequests"] = new JsonArray(address) })).OfType<string>().ToArray()
                : [];
            LoadEvidenceCore(probe, requests, descriptionReadKey);
            foreach (var key in descriptionKeys)
                if (!probe.Construction!.EvidenceRequests.Contains(key, StringComparer.Ordinal)) probe.Construction.EvidenceRequests.Add(key);
            ClearResolvedEvidenceDiagnostics(probe);
            if (followup is not null)
            {
                if (purpose is null) throw new ArgumentException("Evidence admission requires the follow-up purpose.", nameof(purpose));
                AdmitEvidenceFollowup(state, followup(probe), purpose);
            }
            var target = state.Construction!; var accepted = probe.Construction!;
            target.Compact = accepted.Compact;
            target.Evidence = accepted.Evidence;
            target.EvidenceRequests = accepted.EvidenceRequests;
            target.TaskCursor = accepted.TaskCursor; target.ScopeCursor = accepted.ScopeCursor; target.RequirementCursor = accepted.RequirementCursor;
            ClearResolvedEvidenceDiagnostics(state);
            return;
        }
        if (state.Construction!.Version != 2) { LoadEvidenceCore(state, requests); return; }
        var construction = state.Construction;
        var compact = JsonSerializer.Deserialize(JsonSerializer.Serialize(construction.Compact, PlanningJsonContext.Default.PlanningCompactConstructionState),
            PlanningJsonContext.Default.PlanningCompactConstructionState);
        var taskCursor = construction.TaskCursor; var scopeCursor = construction.ScopeCursor; var requirementCursor = construction.RequirementCursor;
        try { LoadEvidenceCore(state, requests, descriptionReadKey); }
        catch
        {
            construction.Compact = compact;
            construction.TaskCursor = taskCursor; construction.ScopeCursor = scopeCursor; construction.RequirementCursor = requirementCursor;
            throw;
        }
    }

    private static void ClearResolvedEvidenceDiagnostics(PlanningSession state) => state.Diagnostics.RemoveAll(d =>
        (d.Location == "/evidenceRequests" || d.Location.StartsWith("/evidenceRequests/", StringComparison.Ordinal)) &&
        d.Code is "CONSTRUCTION_EVIDENCE_UNKNOWN" or "CONSTRUCTION_EVIDENCE_INVALID" or "CONSTRUCTION_EVIDENCE_LIMIT");

    internal static void AdmitEvidenceFollowup(PlanningSession state, LLMRequest request, string purpose)
    {
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).MaxInputTokens;
        var estimate = ModelRequestAdmission.EstimateTokens(request);
        var measured = PlanningRequestMeasurements.Measure(request, purpose, limit, state.Construction!.Compact?.CurrentStep?.Id);
        if (purpose == IntermediatePurpose) measured.BaselineHash ??= state.Construction.Candidate?.BaseHash;
        var measurements = state.RequestMeasurements ??= [];
        var measurement = measurements.SingleOrDefault(item => item.RequestHash == measured.RequestHash);
        if (measurement is null) { measurement = measured; measurements.Add(measurement); }
        if (estimate > limit)
        {
            measurement.Admission = "rejected_before_dispatch";
            Fail("CONSTRUCTION_EVIDENCE_LIMIT", "/evidenceRequests",
                $"The complete prospective {purpose} request needs {estimate} estimated input tokens; its captured ceiling is {limit}. Request a smaller coherent batch or exact separable view.");
        }
        var violations = request.StructuredOutputSchema is { } schema
            ? PlanningContractValidation.ValidateSchema(schema, strict: true)
            : ["The follow-up must retain its structured response schema."];
        if (violations.Count != 0)
        {
            measurement.Admission = "rejected_before_dispatch";
            Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests",
                "The prospective follow-up schema is invalid: " + string.Join("; ", violations));
        }
    }

    internal static string EvidenceBatchFingerprint(PlanningSession state, IEnumerable<string> requests) =>
        PlanningGraphCompiler.Fingerprint(Hash(state.Plan) + ":" + state.Construction!.Candidate?.Hash + ":" +
            string.Join('\n', requests.Order(StringComparer.Ordinal)));

    private static void LoadEvidenceCore(PlanningSession state, List<string> requests, string? descriptionReadKey = null)
    {
        var construction = state.Construction!;
        var fresh = PlanningConstructionPhasePolicies.IsFresh(state.Request);
        if (requests.Count is < 1 or > 8 || requests.Distinct(StringComparer.Ordinal).Count() != requests.Count)
            Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Request one to eight distinct original evidence identities.");
        var key = EvidenceBatchFingerprint(state, requests);
        if (descriptionReadKey is not null && (requests.Count != 1 || !requests[0].StartsWith("description:", StringComparison.Ordinal) ||
            !descriptionReadKey.StartsWith("description-read:" + key + ":", StringComparison.Ordinal)))
            Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Description-read authority must match the exact single evidence address.");
        if (construction.EvidenceRequests.Contains(descriptionReadKey ?? key, StringComparer.Ordinal))
            Fail("REPLAN_NO_PROGRESS", "/evidenceRequests", descriptionReadKey is null
                ? "This evidence batch was already supplied for the same candidate."
                : "This evidence batch was already supplied for the same candidate and retained knowledge.");
        var members = requests.Select(r => EvidenceBatchFingerprint(state, [r])).ToArray();
        if (fresh && descriptionReadKey is null &&
            (members.All(m => construction.EvidenceRequests.Contains(m, StringComparer.Ordinal)) ||
             requests.Where((r, i) => r.StartsWith("description:", StringComparison.Ordinal) &&
                 construction.EvidenceRequests.Contains(members[i], StringComparer.Ordinal)).Any()))
            Fail("REPLAN_NO_PROGRESS", "/evidenceRequests",
                "Regrouping supplied evidence is not progress. An original description reread requires its exact captured knowledge authority.");
        var plan = construction.Candidate?.Plan ?? state.Plan ?? new TaskPlan();
        var symbols = new TaskPlanSymbols(plan);
        // Explicitly requested originals remain available for this decision.
        // Admission checks the whole union on the clone before it is accepted.
        var result = fresh && construction.Evidence is { } retained ? retained.DeepClone().AsObject() : new JsonObject();
        foreach (var request in requests)
        {
            var split = request.IndexOf(':');
            if (split <= 0 || request.Length > 2048) Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Use a declared evidence identity.");
            var kind = request[..split]; var id = request[(split + 1)..];
            JsonNode? value = null;
            if (construction.Version == 2 && PlanningCompactConstruction.LoadEvidence(state, request, out value))
            {
                if (value is null) Fail("CONSTRUCTION_EVIDENCE_UNKNOWN", "/evidenceRequests", "The exact requested evidence is unavailable: " + request);
                result[request] = value;
                continue;
            }
            switch (kind)
            {
                case "invalidation" when int.TryParse(id, out var sequence):
                    var invalidation = construction.Invalidations.SingleOrDefault(i => i.Sequence == sequence);
                    if (invalidation is not null) value = new JsonObject
                    {
                        ["sequence"] = invalidation.Sequence, ["beforeHash"] = invalidation.BeforeHash, ["afterHash"] = invalidation.AfterHash,
                        ["reason"] = invalidation.Reason, ["taskCount"] = invalidation.Tasks.Count,
                        ["tasks"] = new JsonArray(invalidation.Tasks.Take(8).Select(TaskNode).ToArray()),
                        ["scopes"] = new JsonArray(invalidation.Scopes.Take(8).Select(s => (JsonNode)new JsonObject { ["path"] = s.Path,
                            ["tasks"] = new JsonArray(s.Tasks.Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["taskCount"] = s.Tasks.Count,
                            ["always"] = new JsonArray(s.Always.Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["alwaysCount"] = s.Always.Count }).ToArray()),
                        ["originalEvidence"] = "original:" + sequence + ":<task-id>"
                    };
                    break;
                case "original":
                    var address = id.Split(':', 2);
                    if (address.Length == 2 && int.TryParse(address[0], out var eventId) &&
                        construction.Invalidations.SingleOrDefault(i => i.Sequence == eventId) is { } recorded && recorded.Tasks.SingleOrDefault(t => t.Id == address[1]) is { } original)
                    {
                        value = TaskNode(original);
                        var placement = recorded.Scopes.Single(s => s.Tasks.Contains(original.Id, StringComparer.Ordinal) || s.Always.Contains(original.Id, StringComparer.Ordinal));
                        value["scope"] = placement.Path; value["phase"] = placement.Always.Contains(original.Id, StringComparer.Ordinal) ? "always" : "tasks";
                        value["contextRole"] = "invalidated_original";
                    }
                    break;
                case "originalScope":
                    var scopeAddress = id.Split(':', 2);
                    if (scopeAddress.Length == 2 && int.TryParse(scopeAddress[0], out var originalEvent) &&
                        construction.Invalidations.SingleOrDefault(i => i.Sequence == originalEvent) is { } scopeEvent &&
                        scopeEvent.Scopes.SingleOrDefault(s => s.Path == scopeAddress[1]) is { } originalScope)
                        value = new JsonObject { ["path"] = originalScope.Path, ["outputs"] = Node(originalScope.Outputs, PlanningJsonContext.Default.ListTaskOutput),
                            ["tasks"] = new JsonArray(originalScope.Tasks.Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["taskCount"] = originalScope.Tasks.Count,
                            ["always"] = new JsonArray(originalScope.Always.Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["alwaysCount"] = originalScope.Always.Count,
                            ["originalEvidence"] = "original:" + originalEvent + ":<task-id>" };
                    break;
                case "task":
                    if (symbols.Tasks.TryGetValue(id, out var task))
                    { value = TaskNode(task.Task); value["scope"] = task.Scope.Path; value["phase"] = symbols.Phase(id); }
                    else if (construction.RebuildTasks.FirstOrDefault(t => t.Id == id) is { } invalidated)
                    { value = TaskNode(invalidated); value["contextRole"] = "invalidated_original"; }
                    break;
                case "requirement":
                    var requirement = state.Requirements?.Outcomes.FirstOrDefault(r => r.Id == id);
                    if (requirement is not null) value = Node(new PlanningRequirements { Summary = state.Requirements!.Summary,
                        Outcomes = [requirement] }, PlanningJsonContext.Default.PlanningRequirements);
                    break;
                case "scope":
                    var scope = symbols.Scopes.SingleOrDefault(s => s.Path == id);
                    if (scope is not null) value = new JsonObject
                    {
                        ["path"] = scope.Path, ["parent"] = scope.Parent?.Path, ["owner"] = scope.Owner?.Id,
                        ["open"] = (construction.Candidate?.OpenScopes ?? construction.OpenScopes).Contains(scope.Path, StringComparer.Ordinal),
                        ["outputs"] = Node(scope.Source.Outputs, PlanningJsonContext.Default.ListTaskOutput),
                        ["taskCount"] = scope.Source.Tasks.Count, ["alwaysCount"] = scope.Source.Always.Count,
                        ["tasks"] = new JsonArray(scope.Source.Tasks.Take(8).Select(t => TaskNode(t)).ToArray()),
                        ["always"] = new JsonArray(scope.Source.Always.Take(8).Select(t => TaskNode(t)).ToArray()),
                        ["continuation"] = "Use page:tasks:<offset> for the complete task directory; scope children retain their original identities."
                    };
                    if (fresh && value is not null && id == "/root")
                        value["inputs"] = Node(plan.Inputs, PlanningJsonContext.Default.ListTaskInput);
                    if (construction.Version == 2 && value is not null && PlanningCompactConstruction.ScopeGroup(state, id) is { } scopeGroup)
                        value["group"] = PlanningCompactConstruction.GroupInterface(state, scopeGroup.Id);
                    break;
                case "contract":
                    var contracts = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved)
                        .Where(c => TaskOperations.Describe(c).Id == id).DistinctBy(c => (c.Id, c.Version)).ToArray();
                    if (contracts.Length == 1) value = ContractNode(contracts[0]);
                    break;
                case "page":
                    var page = id.Split(':');
                    if (page.Length != 2 || !int.TryParse(page[1], out var offset) || offset < 0) break;
                    switch (page[0])
                    {
                        case "tasks" when offset < symbols.Tasks.Count:
                            value = new JsonObject { ["offset"] = offset, ["total"] = symbols.Tasks.Count,
                                ["items"] = new JsonArray(symbols.Tasks.Values.Skip(offset).Take(8).Select(t => TaskNode(t.Task)).ToArray()) };
                            construction.TaskCursor = offset; break;
                        case "requirements" when offset < state.Requirements?.Outcomes.Count:
                            value = Node(new PlanningRequirements { Summary = state.Requirements.Summary,
                                Outcomes = state.Requirements.Outcomes.Skip(offset).Take(8).ToList() }, PlanningJsonContext.Default.PlanningRequirements);
                            construction.RequirementCursor = offset; break;
                        case "answers" when offset < state.AnswerHistory?.Count:
                            value = new JsonObject { ["offset"] = offset, ["total"] = state.AnswerHistory.Count,
                                ["items"] = Node(state.AnswerHistory.Skip(offset).Take(3).ToList(), PlanningJsonContext.Default.ListPlanningAnswerBatch) }; break;
                        case "scopes" when offset < construction.OpenScopes.Count:
                            value = new JsonArray(construction.OpenScopes.Skip(offset).Take(8).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                            construction.ScopeCursor = offset; break;
                    }
                    break;
            }
            if (value is null) Fail("CONSTRUCTION_EVIDENCE_UNKNOWN", "/evidenceRequests", "The requested original evidence does not exist or is ambiguous: " + request);
            result[request] = value;
        }
        if (!construction.EvidenceRequests.Contains(key, StringComparer.Ordinal)) construction.EvidenceRequests.Add(key);
        if (fresh)
            foreach (var member in members)
                if (!construction.EvidenceRequests.Contains(member, StringComparer.Ordinal)) construction.EvidenceRequests.Add(member);
        if (descriptionReadKey is not null) construction.EvidenceRequests.Add(descriptionReadKey);
        construction.Evidence = result;
    }

    internal static bool Owns(PlanningConstructionCandidate candidate, string path) =>
        candidate.TaskIds.Any(id => path == "/tasks/" + id || path.StartsWith("/tasks/" + id + "/", StringComparison.Ordinal)) ||
        candidate.Extension.Outputs.Any(o => path == candidate.Extension.Scope + "/outputs/" + o.Name ||
            path.StartsWith(candidate.Extension.Scope + "/outputs/" + o.Name + "/", StringComparison.Ordinal));

    internal static void RefreshCandidate(PlanningSession state)
    {
        var candidate = state.Construction!.Candidate!;
        if (candidate.AuthorizedPaths is not null) return;
        var before = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
        var after = new TaskPlanSymbols(candidate.Plan);
        var scope = after.Scopes.Single(s => s.Path == candidate.Extension.Scope).Source;
        var original = before.Scopes.Single(s => s.Path == candidate.Extension.Scope).Source;
        candidate.Extension.Tasks = scope.Tasks.Where(t => !before.Tasks.ContainsKey(t.Id)).ToList();
        candidate.Extension.Always = scope.Always.Where(t => !before.Tasks.ContainsKey(t.Id)).ToList();
        candidate.Extension.Outputs = scope.Outputs.Skip(original.Outputs.Count).ToList();
        candidate.Extension.Groups = candidate.Plan.Groups.Where(g => state.Plan?.Groups.All(b => b.Id != g.Id) != false).ToList();
        candidate.Extension.Choices = candidate.Plan.Choices.Where(c => state.Plan?.Choices.All(b => b.Id != c.Id) != false).ToList();
        candidate.TaskIds = after.Tasks.Keys.Except(before.Tasks.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (candidate.TaskIds.Count > state.Construction.Limits.MaxTasksPerDraft)
            Fail("CONSTRUCTION_DRAFT_LIMIT", candidate.Extension.Scope, "Structural repair exceeded the captured draft task ceiling.");
        candidate.ImportedTaskIds = candidate.TaskIds.SelectMany(id => References(after.Tasks[id].Task))
            .Concat(candidate.Extension.Outputs.SelectMany(o => TaskPlanCompiler.Values(o.Value)).Where(v => v.Kind is "output" or "present" && v.Source is not null).Select(v => v.Source!))
            .Where(id => !candidate.TaskIds.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
    }

    internal static void PreserveAccepted(PlanningSession state, TaskPlan candidate)
    {
        if (state.Plan is null) return;
        if (state.Construction?.Candidate?.AuthorizedPaths is { } authorized)
        {
            var diagnostics = TaskPlanRevisions.Validate(state.Plan, candidate, authorized, state.Catalog, authorized).ToList();
            if (diagnostics.Count > 0) throw new PlanningResponseException(diagnostics.ToList());
            return;
        }
        var before = new TaskPlanSymbols(state.Plan); var after = new TaskPlanSymbols(candidate);
        if (!JsonNode.DeepEquals(Node(state.Plan.Inputs, PlanningJsonContext.Default.ListTaskInput), Node(candidate.Inputs, PlanningJsonContext.Default.ListTaskInput)))
            Fail("REVISION_SCOPE_CHANGED", "/inputs", "Construction must preserve accepted input contracts.");
        foreach (var (id, previous) in before.Tasks)
        {
            if (!after.Tasks.TryGetValue(id, out var current) || previous.Scope.Path != current.Scope.Path ||
                !JsonNode.DeepEquals(TaskNode(previous.Task), TaskNode(current.Task)))
                Fail("REVISION_SCOPE_CHANGED", "/tasks/" + id, "The candidate changed an accepted task outside its extension.");
        }
        foreach (var scope in before.Scopes)
        {
            var current = after.Scopes.SingleOrDefault(s => s.Path == scope.Path)?.Source;
            var rebuilding = state.Construction!.RebuildTasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (current is null || !scope.Source.Tasks.Select(t => t.Id).SequenceEqual(current.Tasks.Where(t => !rebuilding.Contains(t.Id) || scope.Source.Tasks.Any(old => old.Id == t.Id)).Take(scope.Source.Tasks.Count).Select(t => t.Id)) ||
                !scope.Source.Always.Select(t => t.Id).SequenceEqual(current.Always.Where(t => !rebuilding.Contains(t.Id) || scope.Source.Always.Any(old => old.Id == t.Id)).Take(scope.Source.Always.Count).Select(t => t.Id)) ||
                !JsonNode.DeepEquals(Node(scope.Source.Outputs, PlanningJsonContext.Default.ListTaskOutput), Node(current.Outputs.Take(scope.Source.Outputs.Count).ToList(), PlanningJsonContext.Default.ListTaskOutput)))
                Fail("REVISION_SCOPE_CHANGED", scope.Path, "Construction must preserve accepted ordering, exports and cleanup.");
        }
        foreach (var choice in state.Plan.Choices)
            if (!JsonNode.DeepEquals(Node(choice, PlanningJsonContext.Default.PlanningChoice), Node(candidate.Choices.SingleOrDefault(c => c.Id == choice.Id), PlanningJsonContext.Default.PlanningChoice)))
                Fail("REVISION_SCOPE_CHANGED", "/choices/" + choice.Id, "Construction must preserve accepted decisions.");
        foreach (var group in state.Plan.Groups)
        {
            var current = candidate.Groups.SingleOrDefault(g => g.Id == group.Id);
            if (current is null || !JsonNode.DeepEquals(Node(group.Inputs, PlanningJsonContext.Default.ListTaskInput), Node(current.Inputs, PlanningJsonContext.Default.ListTaskInput)))
                Fail("REVISION_SCOPE_CHANGED", "/groups/" + group.Id, "Construction must preserve accepted group interfaces.");
        }
    }

    internal static bool TryPointer(JsonNode root, string pointer, out JsonNode? value)
    {
        value = root;
        if (!pointer.StartsWith('/') || pointer.Length > 2048) return false;
        foreach (var encoded in pointer[1..].Split('/'))
        {
            for (var i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (++i == encoded.Length || encoded[i] is not ('0' or '1'))) return false;
            var part = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value is JsonObject obj && obj.TryGetPropertyValue(part, out var next)) value = next;
            else if (value is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count &&
                index.ToString(System.Globalization.CultureInfo.InvariantCulture) == part) value = array[index];
            else return false;
        }
        return true;
    }
}
