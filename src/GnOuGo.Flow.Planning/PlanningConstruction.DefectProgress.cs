using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    // These are bounded observations in the existing review journal, not repair
    // authority or a proof that the advisory business finding is true.
    private const int DefectSnapshotBytes = 1024;

    internal static JsonObject? DefectWitness(PlanningSession state, JsonObject context, JsonArray findings)
    {
        if (state.Construction!.Candidate is not { } candidate || findings.Count == 0 ||
            PlanningOptimizationThresholds.Exceeded(state, "business_review", "finding_count", findings.Count, 3)) return null;
        var entries = new JsonArray();
        foreach (var finding in findings.OfType<JsonObject>())
        {
            if (finding["evidence"] is not JsonArray { Count: 2 } evidence ||
                !HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, finding)) continue;
            var obligation = evidence[0]!.ToString(); var implementation = evidence[1]!.ToString();
            // A correctable location can be more precise than its supporting
            // task, but only within that exact concrete implementation.
            var location = finding["location"]!.ToString();
            var affected = location.StartsWith(implementation + "/", StringComparison.Ordinal) ? location : implementation;
            if (!TryPointer(context, obligation, out var original) || !TryPointer(context, affected, out var before)) continue;
            var obligations = (finding["obligationPointers"] as JsonArray ?? new JsonArray(obligation))
                .Select(item => item!.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            if (obligations.Any(pointer => !(pointer.StartsWith("/requirements/", StringComparison.Ordinal) || pointer.StartsWith("/globalConstraints/", StringComparison.Ordinal)) ||
                !TryPointer(context, pointer, out _))) continue;
            var selector = DefectSelector(context, affected);
            var entry = new JsonObject
            {
                ["kind"] = finding["kind"]!.DeepClone(), ["location"] = location,
                ["obligation"] = DefectSnapshot(original), ["obligationPointer"] = obligation,
                ["implementationPointer"] = affected, ["selector"] = selector,
                ["before"] = DefectSnapshot(DefectProjection(before)),
                ["explanation"] = finding["explanation"]?.DeepClone(), ["uncertainty"] = finding["uncertainty"]?.DeepClone(),
                ["evidenceAddresses"] = DefectAddresses(context, obligations, affected)
            };
            if (obligations.Length > 1)
                entry["obligations"] = new JsonArray(obligations.Select(pointer =>
                {
                    TryPointer(context, pointer, out var value);
                    return (JsonNode)new JsonObject { ["pointer"] = pointer, ["snapshot"] = DefectSnapshot(value) };
                }).ToArray());
            entry["id"] = DefectHash(entry);
            entries.Add((JsonNode)entry);
        }
        if (entries.Count == 0) return null;
        return new JsonObject
        {
            ["version"] = 1, ["baseline"] = candidate.BaseHash, ["candidate"] = candidate.Hash,
            ["candidateIdentity"] = FailureIdentity(state, candidate), ["contextHash"] = DefectHash(context),
            ["editAuthority"] = DefectSnapshot(new JsonObject
            {
                ["basis"] = candidate.AuthorizedPaths is null ? "tentative_fragment" : "explicit_revision",
                ["scope"] = candidate.Extension.Scope, ["taskIds"] = Set(candidate.TaskIds),
                ["authorizedPaths"] = candidate.AuthorizedPaths is null ? null : Set(candidate.AuthorizedPaths)
            }),
            ["sourceHash"] = DefectSources(state), ["claimHash"] = DefectClaims(state, candidate),
            ["evidenceHashes"] = DefectEvidenceHashes(context), ["findings"] = entries
        };
    }

    internal static void CarryDefectWitnesses(PlanningSession state, PlanningConstructionCandidate? previous,
        PlanningConstructionCandidate next)
    {
        // Carry only this replacement chain or the explicitly resumed consumer.
        // Acceptance ends a replacement chain; old pointers remain historical.
        var reviews = previous?.Reviews ?? ReplacementDefectReviews(state);
        foreach (var retained in reviews.Where(review => review["defectWitness"] is JsonObject || review["technicalRejections"] is JsonObject || review["resolvedWitnesses"] is JsonArray))
            if (!next.Reviews.Any(review => JsonNode.DeepEquals(review, retained))) next.Reviews.Add(retained.DeepClone().AsObject());
    }

    internal static List<JsonObject> ReplacementDefectReviews(PlanningSession state)
    {
        if (state.Construction?.Compact is not { CurrentStep: { } step } compact) return [];
        JsonObject? rejected = null;
        if (compact.ResumingStepId is { } id)
        {
            var resumed = compact.SuspendedSteps?.SingleOrDefault(item => item.Id == id && item.Status == "resuming");
            if (resumed is not null && resumed.RejectedStepIndex >= 0 && resumed.RejectedStepIndex < compact.RejectedSteps.Count)
                rejected = compact.RejectedSteps[resumed.RejectedStepIndex];
        }
        else if (compact.ReplacementScope == step.Scope && compact.ReplacementRequirements is { } requirements &&
            requirements.ToHashSet(StringComparer.Ordinal).SetEquals(step.RequirementIds))
            rejected = compact.RejectedSteps.LastOrDefault();
        if (rejected is null || rejected["step"]?["scope"]?.ToString() != step.Scope || rejected["step"]?["requirementIds"] is not JsonArray ids ||
            !ids.Select(item => item!.ToString()).ToHashSet(StringComparer.Ordinal).SetEquals(step.RequirementIds)) return [];
        return (rejected["reviews"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(review => review["defectWitness"] is JsonObject || review["technicalRejections"] is JsonObject || review["resolvedWitnesses"] is JsonArray).ToList();
    }

    internal static JsonArray? DefectProgressContext(PlanningSession state, JsonObject currentContext)
    {
        if (state.Construction!.Candidate is not { } candidate) return null;
        if (currentContext["actions"] is not JsonArray) currentContext = PlanningBusinessProjection.Build(state, global: false);
        var active = ActiveDefects(candidate);
        // The verdict bounds new targets per response, not accumulated concerns
        // across completed reviews. Present every unresolved concern; request
        // admission bounds calls, and only business review can resolve findings.
        var result = new JsonArray();
        foreach (var (witness, entry) in active)
        {
            var priorContext = candidate.Reviews.Select(review => review["context"]).OfType<JsonObject>()
                .FirstOrDefault(context => DefectHash(context) == witness["contextHash"]?.ToString());
            var sameBasis = witness["baseline"]?.ToString() == candidate.BaseHash && candidate.BaseHash == Hash(state.Plan);
            var sameSource = witness["sourceHash"]?.ToString() == DefectSources(state);
            var sameClaims = witness["claimHash"]?.ToString() == DefectClaims(state, candidate);
            var addedEvidence = DefectEvidenceHashes(currentContext).Where(pair => pair.Value?.ToString() != witness["evidenceHashes"]?[pair.Key]?.ToString()).Select(pair => pair.Key).ToArray();
            var relevantEvidence = entry["evidenceAddresses"]!.AsArray().Any(address => addedEvidence.Contains(address!.ToString(), StringComparer.Ordinal));
            var selector = entry["selector"] as JsonArray;
            var found = DefectRead(currentContext, selector, out var current, out var currentPointer);
            var snapshot = found ? DefectSnapshot(DefectProjection(current)) : null;
            var status = !sameBasis || !sameSource ? "basis_changed" : !sameClaims ? "claims_changed" : !found ? "unverified" :
                entry["before"]?["hash"]?.ToString() == snapshot!["hash"]!.ToString() ? "unchanged" : "changed";
            if (status == "unchanged" && DefectDependencyChanged(candidate, witness, selector, currentContext)) status = "dependency_changed";
            var progress = new JsonObject
            {
                ["id"] = entry["id"]!.DeepClone(), ["kind"] = entry["kind"]!.DeepClone(), ["status"] = status,
                ["priorContextHash"] = witness["contextHash"]!.DeepClone(), ["priorCandidateIdentity"] = witness["candidateIdentity"]!.DeepClone(),
                ["priorEditAuthority"] = witness["editAuthority"]?.DeepClone(),
                ["sameBaseline"] = sameBasis, ["sameSource"] = sameSource, ["sameClaims"] = sameClaims,
                ["evidenceChanged"] = addedEvidence.Length > 0, ["evidenceImproved"] = relevantEvidence,
                ["relevantIdentity"] = DefectHash(new JsonObject { ["baseline"] = witness["baseline"]!.DeepClone(), ["source"] = witness["sourceHash"]!.DeepClone(),
                    ["claim"] = witness["claimHash"]!.DeepClone(), ["selector"] = entry["selector"]?.DeepClone(), ["before"] = entry["before"]!["hash"]!.DeepClone() }),
                ["obligationPointerInPriorContext"] = entry["obligationPointer"]!.DeepClone(), ["obligation"] = entry["obligation"]!.DeepClone(),
                ["implementationPointerInPriorContext"] = entry["implementationPointer"]!.DeepClone(),
                ["currentImplementationPointer"] = currentPointer,
                ["before"] = ExactSnapshot(entry["before"]!, priorContext, entry["implementationPointer"]!.ToString()),
                ["current"] = ExactSnapshot(snapshot, currentContext, currentPointer),
                ["obligations"] = new JsonArray((entry["obligations"] as JsonArray ?? new JsonArray(new JsonObject
                    { ["pointer"] = entry["obligationPointer"]!.DeepClone(), ["snapshot"] = entry["obligation"]!.DeepClone() }))
                    .Select(obligation => ExactSnapshot(obligation!["snapshot"], priorContext, obligation["pointer"]!.ToString())?["value"]?.DeepClone()).ToArray()),
                ["targets"] = DefectTarget(selector) is { } target ? new JsonArray(target) : new JsonArray(),
                ["explanation"] = entry["explanation"]?.DeepClone(), ["uncertainty"] = entry["uncertainty"]?.DeepClone(),
                ["assessment"] = "Prior finding is advisory. Changed bytes or added evidence do not establish resolution; unchanged bytes do not establish that the finding was true."
            };
            result.Add((JsonNode)progress);
        }
        return result.Count == 0 ? null : result;
    }

    private static JsonObject? ExactSnapshot(JsonNode? snapshot, JsonObject? context, string? pointer)
    {
        if (snapshot is not JsonObject saved) return null;
        var result = saved.DeepClone().AsObject();
        // The compact witness keeps its original hash. Its exact retained review
        // supplies values omitted from that storage summary; no new authority is inferred.
        if (!result.ContainsKey("value") && context is not null && pointer is not null && TryPointer(context, pointer, out var value))
            result["value"] = DefectProjection(value);
        return result;
    }

    private static string? DefectTarget(JsonArray? selector)
    {
        if (selector?.ElementAtOrDefault(1) is not JsonObject subject || subject["value"]?.ToString() is not { } id) return null;
        return selector[0]?.ToString() switch { "actions" or "scopes" => id, "groups" => "group:" + id,
            "publicOutputs" => "output:" + id, _ => null };
    }

    private static bool DefectDependencyChanged(PlanningConstructionCandidate candidate, JsonObject witness,
        JsonArray? selector, JsonObject current)
    {
        if (selector?.FirstOrDefault()?.ToString() != "actions" || selector.ElementAtOrDefault(1) is not JsonObject subject ||
            subject["key"]?.ToString() != "id" || subject["value"]?.ToString() is not { } consumer) return false;
        // Use the exact retained review, not the accepted baseline: a second
        // ineffective patch cannot reuse progress made before the latest finding.
        var prior = candidate.Reviews.Select(review => review["context"]).OfType<JsonObject>()
            .FirstOrDefault(context => DefectHash(context) == witness["contextHash"]?.ToString());
        if (prior?["actions"] is not JsonArray originals || current["actions"] is not JsonArray actions) return false;
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var changed = actions.OfType<JsonObject>().Where(action => action["id"]?.ToString() is { } id && id != consumer && symbols.Tasks.ContainsKey(id) &&
            originals.OfType<JsonObject>().SingleOrDefault(before => before["id"]?.ToString() == id) is { } before &&
            new[] { "arguments", "outputs", "instruction" }.Any(key => !JsonNode.DeepEquals(before[key], action[key])))
            .Select(action => action["id"]!.ToString()).ToArray();
        // The existing invalidation closure tracks transitive values, captures,
        // exports, controls and called groups. This permits renewed review only;
        // neither changed dependencies nor changed bytes resolve a finding.
        return changed.Length > 0 && DependentTaskRegion(candidate.Plan, changed).Affected.Contains(consumer);
    }

    internal static JsonObject? PriorRepairObservations(PlanningSession state)
    {
        if (state.Construction is not { Candidate: { } candidate } ||
            candidate.BaseHash != Hash(state.Plan)) return null;
        var active = ActiveDefects(candidate).Select(item => item.Entry["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        if (active.Count == 0) return null;
        var resolutions = candidate.Reviews.SelectMany(review => (review["resolvedWitnesses"] as JsonArray ?? []).Select(id => id!.ToString()))
            .Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => new JsonObject { ["id"] = id, ["status"] = "resolved" }, StringComparer.Ordinal);
        var sources = DefectSources(state); var claims = DefectClaims(state, candidate);
        var entries = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var review in candidate.Reviews.AsEnumerable().Reverse())
        {
            if (review["defectWitness"] is not JsonObject witness || witness["version"]?.GetValue<int>() != 1 ||
                witness["baseline"]?.ToString() != candidate.BaseHash || witness["sourceHash"]?.ToString() != sources ||
                witness["claimHash"]?.ToString() != claims || witness["findings"] is not JsonArray findings) continue;
            foreach (var entry in findings.OfType<JsonObject>())
            {
                var id = entry["id"]!.ToString();
                if (active.Contains(id) || !resolutions.TryGetValue(id, out var resolution) || !seen.Add(id)) continue;
                entries.Add((JsonNode)new JsonObject
                {
                    ["id"] = id, ["priorContextHash"] = witness["contextHash"]!.DeepClone(),
                    ["priorCandidateIdentity"] = witness["candidateIdentity"]!.DeepClone(),
                    ["obligation"] = entry["obligation"]!.DeepClone(), ["obligationPointer"] = entry["obligationPointer"]!.DeepClone(),
                    ["implementationPointer"] = entry["implementationPointer"]!.DeepClone(), ["before"] = entry["before"]!.DeepClone(),
                    ["explanation"] = entry["explanation"]?.DeepClone(), ["resolution"] = resolution.DeepClone()
                });
                if (entries.Count == 3) break;
            }
            if (entries.Count == 3) break;
        }
        return entries.Count == 0 ? null : new JsonObject
        {
            ["meaning"] = "Historical advisory observations from this repair chain, tied to their original contexts. Avoid restoring rejected values without resolving their original concern. Prior findings remain contestable; these observations neither reactivate findings nor grant edits or require Judge resolutions.",
            ["entries"] = entries
        };
    }

    internal static void AddAuthorizedRevisionDraft(PlanningSession state, JsonObject context)
    {
        if (state.Construction?.Candidate?.AuthorizedPaths is not { } paths ||
            context["draft"] is not JsonObject draft) return;
        draft["basis"] = "authorized_revision";
        draft["authorizedPaths"] = Set(paths);
        draft["tasks"] = new JsonArray(AuthorizedRevisionTasks(state).Select(item => item.Task).ToArray());
    }

    internal static List<(string Id, JsonNode Task)> AuthorizedRevisionTasks(PlanningSession state)
    {
        if (state.Plan is null || state.Construction?.Candidate is not { AuthorizedPaths: { } paths } candidate ||
            candidate.BaseHash != Hash(state.Plan) || TaskPlanRevisions.Validate(state.Plan, candidate.Plan, paths, state.Catalog, paths).Any()) return [];
        var before = new TaskPlanSymbols(state.Plan); var after = new TaskPlanSymbols(candidate.Plan);
        var result = new List<(string Id, JsonNode Task)>();
        foreach (var id in candidate.TaskIds.Distinct(StringComparer.Ordinal))
        {
            if (!paths.Any(path => path == "/tasks/" + id || path.StartsWith("/tasks/" + id + "/", StringComparison.Ordinal)) ||
                !before.Tasks.TryGetValue(id, out var previous) || !after.Tasks.TryGetValue(id, out var current) ||
                previous.Scope.Path != current.Scope.Path || before.Phase(id) != after.Phase(id)) continue;
            var task = TaskNode(current.Task); task["scope"] = current.Scope.Path; task["phase"] = after.Phase(id);
            result.Add((id, task));
        }
        return result;
    }

    private static List<(JsonObject Witness, JsonObject Entry)> ActiveDefects(PlanningConstructionCandidate candidate)
    {
        var active = new Dictionary<string, (JsonObject Witness, JsonObject Entry)>(StringComparer.Ordinal);
        foreach (var review in candidate.Reviews)
        {
            // Only validated completed reviews are retained in this shape. A
            // still-present finding must be reissued by that same report.
            if (review["resolvedWitnesses"] is JsonArray resolutions)
                foreach (var id in resolutions) active.Remove(id!.ToString());
            if (review["defectWitness"] is not JsonObject witness || witness["version"]?.GetValue<int>() != 1 || witness["findings"] is not JsonArray findings) continue;
            foreach (var entry in findings.OfType<JsonObject>()) active[entry["id"]!.ToString()] = (witness, entry);
        }
        return active.Values.ToList();
    }

    private static JsonObject DefectEvidenceHashes(JsonObject context) => new((context["evidence"] as JsonObject ?? [])
        .Where(pair => pair.Value is not null).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(DefectHash(pair.Value)))));

    private static JsonArray DefectAddresses(JsonObject context, IEnumerable<string> obligations, string affected)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pointer in obligations.Append(affected))
        {
            var tokens = pointer.Split('/');
            if (tokens is ["", "evidence", var encoded]) addresses.Add(encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            for (var count = tokens.Length; count > 1; count--)
                if (TryPointer(context, string.Join('/', tokens.Take(count)), out var node) && node is JsonObject obj)
                {
                    if (obj["id"] is { } id && obj["kind"] is not null) addresses.Add("task:" + id);
                    if (obj["id"] is { } requirement && obj["original"] is not null) addresses.Add("requirement:" + requirement);
                    if (obj["id"] is { } sourceId && obj["text"] is not null) addresses.Add("source:" + sourceId);
                    if (obj["declaration"]?["name"] is { } name) addresses.Add("input:" + name);
                }
        }
        return Set(addresses);
    }

    private static string DefectSources(PlanningSession state) => DefectHash(new JsonObject
    {
        ["requirements"] = Node(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["sources"] = new JsonArray(state.Construction!.Compact!.Sources.Select(source => (JsonNode)new JsonObject
            { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text }).ToArray()),
        ["requirementSources"] = new JsonObject(state.Construction.Compact.RequirementSources.Select(pair =>
            new KeyValuePair<string, JsonNode?>(pair.Key, Set(pair.Value)))),
        ["globalSources"] = Set(state.Construction.Compact.GlobalSourceIds),
        ["contracts"] = new JsonArray((state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
            .Where(capability => state.Construction.Compact.CurrentStep!.OperationIds.Contains(TaskOperations.Describe(capability).Id, StringComparer.Ordinal))
            .Select(capability => new { Id = TaskOperations.Describe(capability).Id, capability.Version,
                Hash = DefectHash(Node(capability, PlanningJsonContext.Default.PlanningCapability)) })
            .Distinct().OrderBy(contract => contract.Id, StringComparer.Ordinal).ThenBy(contract => contract.Version, StringComparer.Ordinal).ThenBy(contract => contract.Hash, StringComparer.Ordinal)
            .Select(contract => (JsonNode)new JsonObject { ["id"] = contract.Id, ["version"] = contract.Version, ["hash"] = contract.Hash }).ToArray())
    });

    private static string DefectClaims(PlanningSession state, PlanningConstructionCandidate candidate) => DefectHash(new JsonObject
    {
        ["scope"] = candidate.Extension.Scope, ["complete"] = candidate.Extension.Complete,
        ["closeScopes"] = Set(candidate.Extension.CloseScopes), ["openScopes"] = Set(candidate.OpenScopes),
        ["requirements"] = Set(state.Construction!.Compact!.CurrentStep!.RequirementIds),
        ["associatedRequirements"] = Set(candidate.Extension.RequirementIds),
        ["closeRequirements"] = Set(state.Construction.Compact.CurrentStep.CloseRequirementIds),
        ["commitments"] = new JsonObject(state.Construction.Compact.PendingCommitments.Select(pair =>
            new KeyValuePair<string, JsonNode?>(pair.Key, Node(pair.Value, PlanningJsonContext.Default.ListPlanningCommitment))))
    });

    private static JsonArray Set(IEnumerable<string> values) => new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject DefectSnapshot(JsonNode? node)
    {
        var bytes = Encoding.UTF8.GetByteCount(node?.ToJsonString() ?? "null");
        var result = new JsonObject { ["hash"] = DefectHash(node), ["bytes"] = bytes, ["omitted"] = bytes > DefectSnapshotBytes };
        if (bytes <= DefectSnapshotBytes) result["value"] = node?.DeepClone();
        return result;
    }

    private static JsonNode? DefectProjection(JsonNode? node)
    {
        var value = node?.DeepClone();
        // An operation's purpose is non-executable metadata. Transform objectives
        // and all literal strings are executable inputs and remain exact.
        if (value is JsonObject task && task["id"] is JsonValue && task["kind"]?.ToString() == "operation") task.Remove("objective");
        return value;
    }

    private static string DefectHash(JsonNode? node) => PlanningGraphCompiler.Fingerprint(Canonical(node)?.ToJsonString() ?? "null");

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone()
    };

    private static JsonArray? DefectSelector(JsonObject context, string pointer)
    {
        JsonNode? node = context;
        var selector = new JsonArray();
        foreach (var encoded in pointer[1..].Split('/'))
        {
            var token = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node is JsonObject obj && obj.TryGetPropertyValue(token, out var child))
            { selector.Add((JsonNode?)JsonValue.Create(token)); node = child; }
            else if (node is JsonArray array && int.TryParse(token, out var index) && index >= 0 && index < array.Count)
            {
                var item = array[index];
                var key = new[] { "id", "name", "consumerLocation" }.FirstOrDefault(key => item is JsonObject selected && selected[key] is JsonValue &&
                    array.Count(other => other is JsonObject candidate && JsonNode.DeepEquals(candidate[key], selected[key])) == 1);
                if (key is not null) selector.Add((JsonNode)new JsonObject { ["key"] = key, ["value"] = item![key]!.DeepClone() });
                // Expression operands are ordered; unidentifiable collection
                // members cannot safely be rebound to another context by index.
                else if (selector.LastOrDefault()?.ToString() == "items") selector.Add((JsonNode)new JsonObject { ["index"] = index });
                else return null;
                node = item;
            }
            else return null;
        }
        return selector;
    }

    private static bool DefectRead(JsonObject context, JsonArray? selector, out JsonNode? node, out string? pointer)
    {
        node = context; pointer = "";
        if (selector is null) { pointer = null; return false; }
        foreach (var part in selector)
        {
            string token;
            if (part is JsonValue value && node is JsonObject obj && obj.TryGetPropertyValue(value.ToString(), out var child))
            { token = value.ToString(); node = child; }
            else if (part is JsonObject selection && node is JsonArray array)
            {
                int index;
                if (selection["key"] is JsonValue key)
                {
                    var matches = array.Select((item, offset) => (item, offset)).Where(item => item.item is JsonObject candidate &&
                        JsonNode.DeepEquals(candidate[key.ToString()], selection["value"])).ToArray();
                    if (matches.Length != 1) { pointer = null; return false; }
                    index = matches[0].offset;
                }
                else if (selection["index"] is JsonValue position && position.TryGetValue<int>(out index) && index >= 0 && index < array.Count) { }
                else { pointer = null; return false; }
                token = index.ToString(System.Globalization.CultureInfo.InvariantCulture); node = array[index];
            }
            else { pointer = null; return false; }
            pointer += "/" + token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        }
        return true;
    }
}
