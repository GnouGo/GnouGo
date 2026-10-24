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
        if (state.Construction?.Compact?.PresentationVersion != 5 || state.Construction.Candidate is not { } candidate ||
            findings.Count is 0 or > 3) return null;
        var entries = new JsonArray();
        foreach (var finding in findings.OfType<JsonObject>())
        {
            if (finding["evidence"] is not JsonArray { Count: 2 } evidence ||
                !HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, finding, locationEvidence: true)) continue;
            var obligation = evidence[0]!.ToString(); var implementation = evidence[1]!.ToString();
            // A correctable location can be more precise than its supporting
            // task, but only within that exact concrete implementation.
            var location = finding["location"]!.ToString();
            var affected = location.StartsWith(implementation + "/", StringComparison.Ordinal) ? location : implementation;
            if (!TryPointer(context, obligation, out var original) || !TryPointer(context, affected, out var before)) continue;
            var selector = DefectSelector(context, affected);
            var entry = new JsonObject
            {
                ["kind"] = finding["kind"]!.DeepClone(), ["location"] = location,
                ["obligation"] = DefectSnapshot(original), ["obligationPointer"] = obligation,
                ["implementationPointer"] = affected, ["selector"] = selector,
                ["before"] = DefectSnapshot(DefectProjection(before)),
                ["explanation"] = finding["explanation"]?.DeepClone(), ["uncertainty"] = finding["uncertainty"]?.DeepClone(),
                ["fact"] = finding["fact"]?.DeepClone(), ["requiredBinding"] = DefectRequiredBinding(context, finding["requiredBinding"] as JsonObject),
                ["evidenceAddresses"] = DefectAddresses(context, obligation, affected)
            };
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
            ["evidenceHashes"] = DefectEvidenceHashes(context), ["supportHashes"] = DefectSupportHashes(context), ["findings"] = entries
        };
    }

    internal static void CarryDefectWitnesses(PlanningSession state, PlanningConstructionCandidate? previous,
        PlanningConstructionCandidate next)
    {
        if (state.Construction?.Compact?.PresentationVersion != 5) return;
        // Reselection already retains the old reviews. Carry their witnessed
        // history into the replacement; do not reinterpret the old pointers.
        var reviews = previous?.Reviews ?? state.Construction.Compact.RejectedSteps.LastOrDefault()?["reviews"]?
            .AsArray().OfType<JsonObject>().ToList() ?? [];
        foreach (var retained in reviews.Where(review => review["defectWitness"] is JsonObject || review["response"]?["resolutions"] is JsonArray))
            if (!next.Reviews.Any(review => JsonNode.DeepEquals(review, retained))) next.Reviews.Add(retained.DeepClone().AsObject());
    }

    internal static JsonArray? DefectProgressContext(PlanningSession state, JsonObject currentContext)
    {
        if (state.Construction?.Compact?.PresentationVersion != 5 || state.Construction.Candidate is not { } candidate) return null;
        var active = ActiveDefects(candidate);
        if (active.Count > 3) Fail("CONSTRUCTION_REVIEW_INCONCLUSIVE", "/defectProgress", "More unresolved prior findings exist than this bounded review can assess.");
        RestoreAcceptedDefectEvidence(state, currentContext, active);
        var result = new JsonArray();
        foreach (var (witness, entry) in active)
        {
            var sameBasis = witness["baseline"]?.ToString() == candidate.BaseHash && candidate.BaseHash == Hash(state.Plan);
            var sameSource = witness["sourceHash"]?.ToString() == DefectSources(state);
            var sameClaims = witness["claimHash"]?.ToString() == DefectClaims(state, candidate);
            var addedEvidence = DefectEvidenceHashes(currentContext).Where(pair => pair.Value?.ToString() != witness["evidenceHashes"]?[pair.Key]?.ToString()).Select(pair => pair.Key).ToArray();
            var relevantEvidence = entry["evidenceAddresses"]!.AsArray().Any(address => addedEvidence.Contains(address!.ToString(), StringComparer.Ordinal));
            var found = DefectRead(currentContext, entry["selector"] as JsonArray, out var current, out var currentPointer);
            var snapshot = found ? DefectSnapshot(DefectProjection(current)) : null;
            var status = !sameBasis || !sameSource ? "basis_changed" : !sameClaims ? "claims_changed" : !found ? "unverified" :
                entry["before"]?["hash"]?.ToString() == snapshot!["hash"]!.ToString() ? "unchanged" : "changed";
            result.Add((JsonNode)new JsonObject
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
                ["currentImplementationPointer"] = currentPointer, ["before"] = entry["before"]!.DeepClone(), ["current"] = snapshot,
                ["explanation"] = entry["explanation"]?.DeepClone(), ["uncertainty"] = entry["uncertainty"]?.DeepClone(), ["factInPriorContext"] = entry["fact"]?.DeepClone(),
                ["requiredBinding"] = DefectBindingProgress(currentContext, entry["requiredBinding"] as JsonObject),
                ["assessment"] = "Prior finding is advisory. Changed bytes or added evidence do not establish resolution; unchanged bytes do not establish that the finding was true."
            });
        }
        return result.Count == 0 ? null : result;
    }

    private static void RestoreAcceptedDefectEvidence(PlanningSession state, JsonObject context,
        List<(JsonObject Witness, JsonObject Entry)> active)
    {
        if (state.Plan is null || active.Count == 0) return;
        var candidate = state.Construction!.Candidate!;
        var accepted = new TaskPlanSymbols(state.Plan); var current = new TaskPlanSymbols(candidate.Plan);
        var baseline = Hash(state.Plan); var sources = DefectSources(state);
        foreach (var (witness, entry) in active)
        {
            if (candidate.BaseHash != baseline || witness["baseline"]?.ToString() != baseline ||
                witness["sourceHash"]?.ToString() != sources ||
                entry["implementationPointer"]?.ToString().Split('/') is not ["", "evidence", var encoded]) continue;
            var address = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (!address.StartsWith("task:", StringComparison.Ordinal) || context["evidence"]?[address] is not null ||
                !accepted.Tasks.TryGetValue(address[5..], out var before) || !current.Tasks.TryGetValue(address[5..], out var after) ||
                before.Scope.Path != after.Scope.Path || accepted.Phase(address[5..]) != current.Phase(address[5..]) ||
                !JsonNode.DeepEquals(Node(before.Task, PlanningJsonContext.Default.PlanTask), Node(after.Task, PlanningJsonContext.Default.PlanTask))) continue;
            // Re-resolve only the cited accepted original. A prior snapshot is
            // historical evidence, never a replacement for the current TaskPlan.
            var value = TaskNode(after.Task); value["scope"] = after.Scope.Path; value["phase"] = current.Phase(address[5..]);
            if (Encoding.UTF8.GetByteCount(value.ToJsonString()) > DefectSnapshotBytes ||
                witness["evidenceHashes"]?[address]?.ToString() != DefectHash(value)) continue;
            (context["evidence"] ??= new JsonObject())[address] = value;
        }
    }

    internal static bool HasNewDefectEvidence(PlanningSession state, JsonObject context, JsonObject prior, string obligation, string implementation)
    {
        if (state.Construction?.Compact?.PresentationVersion != 5 || state.Construction.Candidate is not { } candidate) return false;
        var witness = candidate.Reviews.Select(review => review["defectWitness"]).OfType<JsonObject>().LastOrDefault(item =>
            item["findings"] is JsonArray findings && findings.Any(finding => finding?["id"]?.ToString() == prior["id"]?.ToString()));
        if (witness?["supportHashes"] is not JsonObject support) return false;
        foreach (var pointer in new[] { obligation, implementation })
        {
            if (!TryPointer(context, pointer, out var node) || node is null || DefectSelector(context, pointer) is not { } selector) continue;
            var key = DefectHash(selector);
            if (support[key] is { } before && before.ToString() != DefectHash(DefectProjection(node)) || support[key] is null && support["complete"]?.GetValue<bool>() == true) return true;
        }
        return false;
    }

    private static List<(JsonObject Witness, JsonObject Entry)> ActiveDefects(PlanningConstructionCandidate candidate)
    {
        var active = new Dictionary<string, (JsonObject Witness, JsonObject Entry)>(StringComparer.Ordinal);
        foreach (var review in candidate.Reviews)
        {
            // Only validated completed reviews are retained in this shape. A
            // still-present finding must be reissued by that same report.
            if (review["response"]?["resolutions"] is JsonArray resolutions)
                foreach (var resolution in resolutions.OfType<JsonObject>())
                    if (resolution["status"]?.ToString() is "resolved" or "still_present" or "invalidated_by_evidence" && resolution["id"] is { } id) active.Remove(id.ToString());
            if (review["defectWitness"] is not JsonObject witness || witness["version"]?.GetValue<int>() != 1 || witness["findings"] is not JsonArray findings) continue;
            foreach (var entry in findings.OfType<JsonObject>()) active[entry["id"]!.ToString()] = (witness, entry);
        }
        return active.Values.ToList();
    }

    private static JsonObject DefectEvidenceHashes(JsonObject context) => new((context["evidence"] as JsonObject ?? [])
        .Where(pair => pair.Value is not null).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(DefectHash(pair.Value)))));

    private static JsonObject DefectSupportHashes(JsonObject context)
    {
        var schema = HybridWorkflowPlanner.ConstructionReviewSchema(context, verified: true);
        var pointers = schema["properties"]!["findings"]!["anyOf"]!.AsArray().Single(item => item?["type"]?.ToString() == "array")!
            ["items"]!["properties"]!["evidence"]!["items"]!["enum"]!.AsArray();
        var hashes = new JsonObject { ["complete"] = pointers.Count <= 128 };
        foreach (var pointer in pointers.Take(128))
            if (TryPointer(context, pointer!.ToString(), out var node) && DefectSelector(context, pointer.ToString()) is { } selector)
                hashes[DefectHash(selector)] = DefectHash(DefectProjection(node));
        return hashes;
    }

    private static JsonArray DefectAddresses(JsonObject context, string obligation, string affected)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pointer in new[] { obligation, affected })
        {
            var tokens = pointer.Split('/');
            if (tokens is ["", "evidence", var encoded]) addresses.Add(encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            for (var count = tokens.Length; count > 1; count--)
                if (TryPointer(context, string.Join('/', tokens.Take(count)), out var node) && node is JsonObject obj)
                {
                    if (obj["id"] is { } id && obj["kind"] is not null) addresses.Add("task:" + id);
                    if (obj["id"] is { } requirement && obj["sources"] is JsonArray sources)
                    {
                        addresses.Add("requirement:" + requirement);
                        foreach (var source in sources.OfType<JsonObject>()) if (source["id"] is { } sourceId) addresses.Add("source:" + sourceId);
                    }
                    if (obj["declaration"]?["name"] is { } name) addresses.Add("input:" + name);
                }
        }
        return Set(addresses);
    }

    private static JsonObject? DefectRequiredBinding(JsonObject context, JsonObject? assertion)
    {
        if (assertion?["source"] is not { } sourcePointer || assertion["consumer"] is not { } consumerPointer ||
            !TryPointer(context, sourcePointer.ToString(), out var source) || !TryPointer(context, consumerPointer.ToString(), out var consumer) ||
            source is not JsonObject original || consumer is not JsonObject { } argument || argument["name"] is null || argument["value"] is null) return null;
        var binding = original["binding"]?.DeepClone() ?? (original["declaration"]?["name"] is { } name
            ? new JsonObject { ["kind"] = "input", ["source"] = name.DeepClone() } : null);
        if (binding is null || DefectSelector(context, consumerPointer.ToString()) is not { } selector || selector.Count < 2) return null;
        var taskSelector = new JsonArray(selector.Take(selector.Count - 2).Select(part => part?.DeepClone()).ToArray());
        if (!DefectRead(context, taskSelector, out var task, out _) || task?["kind"]?.ToString() != "operation") return null;
        return new JsonObject { ["source"] = binding, ["consumerSelector"] = selector, ["taskSelector"] = taskSelector, ["operation"] = task["operation"]?.DeepClone(),
            ["sourcePointerInPriorContext"] = sourcePointer.DeepClone(), ["consumerPointerInPriorContext"] = consumerPointer.DeepClone() };
    }

    private static JsonObject? DefectBindingProgress(JsonObject context, JsonObject? binding)
    {
        if (binding is null) return null;
        var found = DefectRead(context, binding["consumerSelector"] as JsonArray, out var consumer, out var pointer) &&
            DefectRead(context, binding["taskSelector"] as JsonArray, out var task, out _) && task?["kind"]?.ToString() == "operation" &&
            JsonNode.DeepEquals(task["operation"], binding["operation"]);
        var value = consumer?["value"]?.Deserialize(PlanningJsonContext.Default.TaskValue);
        var source = binding["source"]!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
        var operands = value is null ? [] : TaskPlanCompiler.Values(value).ToArray();
        var status = !found || value is null ? "unknown" : operands.Any(operand => JsonNode.DeepEquals(Node(operand, PlanningJsonContext.Default.TaskValue), Node(source, PlanningJsonContext.Default.TaskValue)))
            ? "established" : operands.Any(operand => operand.Kind is "output" or "present" or "choice" ||
                source.Kind == "field" && operand.Kind == "input") ? "unknown" : "disconnected";
        return new JsonObject { ["source"] = DefectSnapshot(binding["source"]), ["consumerPointer"] = pointer, ["dependency"] = status,
            ["meaning"] = "Exact structural source dependency at this argument only; the asserted business need and semantic resolution still require review." };
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
