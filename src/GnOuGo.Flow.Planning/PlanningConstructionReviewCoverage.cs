using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

// Source windows are evidence for one candidate, never executable work or
// requirement completion. Their manifest lives in the existing review journal.
internal static class PlanningConstructionReviewCoverage
{
    private const string ManifestPurpose = "construction_source_coverage";
    private sealed record Unit(string Key, string Kind, JsonObject Value, JsonObject Original);

    internal static JsonObject Select(PlanningSession state, JsonObject context,
        Func<JsonObject, int, LLMRequest> prepare, JsonObject? reassessmentCoverage = null)
    {
        var candidate = state.Construction!.Candidate!;
        var units = Units(state);
        var core = Core(context);
        var basis = Basis(state, candidate, units);
        var basisHash = Hash(basis);
        var manifest = CurrentManifest(state, candidate);
        if (manifest is null)
        {
            var originalSet = reassessmentCoverage is null ? null : candidate.Reviews.LastOrDefault(row =>
                row["purpose"]?.ToString() == ManifestPurpose && row["setHash"]?.ToString() == reassessmentCoverage["setHash"]?.ToString() &&
                ManifestValid(row, units));
            if (reassessmentCoverage is not null && originalSet is null)
                throw new PlanningConflictException("Reassessment lost its exact original source-window set.");
            var windows = originalSet is null ? new List<List<string>>() : originalSet["windows"]!.AsArray()
                .Select(window => window!.AsArray().Select(key => key!.ToString()).ToList()).ToList();
            var current = new List<string>();
            foreach (var group in originalSet is null ? PackingGroups(units, core, state) : [])
            {
                var proposed = current.Concat(group).ToList();
                // Use the maximum possible window index/count during packing;
                // final envelope counters can only shrink. Linked defect clauses
                // remain together just like source continuations.
                var probe = Window(core, units, proposed, Metadata(basisHash, new string('0', 64), units.Count, units.Count, proposed));
                if (current.Count > 0 && !Fits(prepare(probe, units.Count), state))
                { windows.Add(current); current = group; }
                else current = proposed;
            }
            if (current.Count > 0) windows.Add(current);
            if (windows.Count == 0) windows.Add([]);
            var windowNodes = new JsonArray(windows.Select(keys => (JsonNode)Strings(keys)).ToArray());
            manifest = new JsonObject
            {
                ["purpose"] = ManifestPurpose, ["outcome"] = "source_windows", ["version"] = 1,
                ["candidateHash"] = candidate.Hash, ["basis"] = basis, ["basisHash"] = basisHash,
                ["sources"] = SourceHashes(units), ["windows"] = windowNodes,
                ["setHash"] = SetHash(basisHash, windowNodes)
            };
            candidate.Reviews.Add(manifest);
            candidate.LocalReviewed = false; candidate.IntermediateReviewed = false;
        }
        basisHash = manifest["basisHash"]!.ToString();
        var groups = manifest["windows"]!.AsArray();
        var remaining = Enumerable.Range(0, groups.Count).Where(index => !CleanWindow(candidate.Reviews, manifest, index,
            candidate.NeedsIntermediateReview ? ["local", "intermediate"] : ["local"])).ToArray();
        if (remaining.Length == 0) throw new PlanningConflictException("All source windows were already reviewed for this candidate.");
        JsonObject Present(int index) => Window(core, units, groups[index]!.AsArray().Select(key => key!.ToString()).ToList(),
            Metadata(basisHash, manifest["setHash"]!.ToString(), groups.Count, index,
                groups[index]!.AsArray().Select(key => key!.ToString())));
        if (reassessmentCoverage is not null)
        {
            var index = reassessmentCoverage["windowIndex"]!.GetValue<int>();
            if (!remaining.Contains(index) || !JsonNode.DeepEquals(groups[index], reassessmentCoverage["sourceIds"]))
                throw new PlanningConflictException("Reassessment cannot change its reviewed source window.");
            return Present(index);
        }
        // Fail before spending on other windows when any indivisible required
        // context cannot fit. CallAsync records this exact rejected request.
        foreach (var index in remaining)
        {
            var result = Present(index);
            if (!Fits(prepare(result, remaining.Length), state)) return result;
        }
        return Present(remaining[0]);
    }

    internal static bool Complete(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (!PlanningCompactConstruction.UsesDecisionView(state)) return true;
        var manifest = CurrentManifest(state, candidate);
        return manifest is not null && !PlanningConstruction.UnresolvedDefects(candidate) && Enumerable.Range(0, manifest["windows"]!.AsArray().Count).All(index =>
            CleanWindow(candidate.Reviews, manifest, index, candidate.NeedsIntermediateReview ? ["local", "intermediate"] : ["local"]));
    }

    internal static int RemainingCalls(PlanningSession state)
    {
        if (state.Construction?.Candidate is not { } candidate) return 1;
        var manifest = CurrentManifest(state, candidate);
        return manifest is null ? 1 : Enumerable.Range(0, manifest["windows"]!.AsArray().Count).Count(index =>
            !CleanWindow(candidate.Reviews, manifest, index, candidate.NeedsIntermediateReview ? ["local", "intermediate"] : ["local"]));
    }

    internal static void CopyManifest(PlanningConstructionCandidate from, PlanningConstructionCandidate to)
    {
        foreach (var manifest in from.Reviews.Where(row => row["purpose"]?.ToString() == ManifestPurpose))
            if (!to.Reviews.Any(row => JsonNode.DeepEquals(row, manifest))) to.Reviews.Add(manifest.DeepClone().AsObject());
        to.LocalReviewed = false; to.IntermediateReviewed = false;
    }

    internal static bool Reviewed(PlanningSession state, PlanningConstructionCheckpoint checkpoint, string purpose)
    {
        if (!PlanningCompactConstruction.UsesDecisionView(state)) return true;
        var units = Units(state);
        var manifest = checkpoint.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == ManifestPurpose &&
            row["candidateHash"]?.ToString() == checkpoint.PlanHash && row["basis"] is JsonObject basis &&
            basis["baseline"]?.ToString() == checkpoint.BaseHash && basis["candidate"]?.ToString() == checkpoint.PlanHash &&
            checkpoint.Step is { } step && basis["claims"]?.ToString() == Claims(step, checkpoint.ClosedScopes,
                checkpoint.Fragment["complete"]?.GetValue<bool>() == true) &&
            basis["scope"]?.ToString() == step.Scope && BasisOriginalsValid(state, row, units, allowAddedSources: true));
        return manifest is not null && Enumerable.Range(0, manifest["windows"]!.AsArray().Count).All(index =>
            CleanWindow(checkpoint.Reviews, manifest, index, [purpose == PlanningConstruction.LocalPurpose ? "local" : "intermediate"]));
    }

    private static JsonObject? CurrentManifest(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (PlanningCompactConstruction.UsesAcceptedProgress(state) && !JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(candidate.Extension.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding),
            JsonSerializer.SerializeToNode(state.Construction!.Compact!.CurrentStep!.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding))) return null;
        var units = Units(state);
        return candidate.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == ManifestPurpose &&
            row["candidateHash"]?.ToString() == candidate.Hash && row["basis"] is JsonObject basis &&
            basis["baseline"]?.ToString() == candidate.BaseHash && candidate.BaseHash == PlanningConstruction.Hash(state.Plan) &&
            basis["candidate"]?.ToString() == candidate.Hash && candidate.Hash == PlanningConstruction.Hash(candidate.Plan) &&
            basis["scope"]?.ToString() == candidate.Extension.Scope &&
            basis["claims"]?.ToString() == Claims(state.Construction!.Compact!.CurrentStep!, candidate.Extension.CloseScopes, candidate.Extension.Complete) &&
            basis["evidence"]?.ToString() == EvidenceHash(state) && BasisOriginalsValid(state, row, units, allowAddedSources: false));
    }

    private static bool BasisOriginalsValid(PlanningSession state, JsonObject row, List<Unit> units, bool allowAddedSources)
    {
        if (row["basis"] is not JsonObject basis || row["basisHash"]?.ToString() != Hash(basis) ||
            !ManifestValid(row, units, allowAddedSources) || basis["policy"]?.ToString() !=
                PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy)) ||
            basis["contracts"] is not JsonArray contracts) return false;
        foreach (var contract in contracts.OfType<JsonObject>())
        {
            var matches = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
                .Where(capability => TaskOperations.Describe(capability).Id == contract["id"]?.ToString() &&
                    capability.Version == contract["version"]?.ToString()).DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
            if (matches.Length != 1 || contract["hash"]?.ToString() != ContractHash(matches[0])) return false;
        }
        return contracts.All(node => node is JsonObject);
    }

    private static JsonObject Basis(PlanningSession state, PlanningConstructionCandidate candidate, List<Unit> units)
    {
        var operationIds = TaskPlanRevisions.Tasks(candidate.Plan).Where(task => task.Kind == "operation")
            .Select(task => task.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var contracts = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
            .Where(capability => operationIds.Contains(TaskOperations.Describe(capability).Id))
            .DistinctBy(capability => (capability.Id, capability.Version)).OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ThenBy(capability => capability.Version, StringComparer.Ordinal);
        return new JsonObject
        {
            ["baseline"] = candidate.BaseHash, ["candidate"] = candidate.Hash, ["scope"] = candidate.Extension.Scope,
            ["claims"] = Claims(state.Construction!.Compact!.CurrentStep!, candidate.Extension.CloseScopes, candidate.Extension.Complete),
            ["closedRequirementIds"] = Strings(state.Construction.Compact.CurrentStep!.CloseRequirementIds),
            ["sourceHash"] = Hash(SourceHashes(units)), ["evidence"] = EvidenceHash(state),
            ["policy"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy)),
            ["contracts"] = new JsonArray(contracts.Select(capability => (JsonNode)new JsonObject
                { ["id"] = TaskOperations.Describe(capability).Id, ["version"] = capability.Version, ["hash"] = ContractHash(capability) }).ToArray())
        };
    }

    private static bool ManifestValid(JsonObject row, List<Unit> units, bool allowAddedSources = false)
    {
        if (row["version"]?.ToString() != "1" || row["sources"] is not JsonObject sources ||
            row["windows"] is not JsonArray { Count: > 0 } windows || row["basisHash"] is null ||
            row["setHash"]?.ToString() != SetHash(row["basisHash"]!.ToString(), windows) ||
            row["basis"]?["sourceHash"]?.ToString() != Hash(sources)) return false;
        var current = SourceHashes(units);
        if (!allowAddedSources && sources.Count != current.Count || sources.Any(pair => current[pair.Key]?.ToString() != pair.Value?.ToString())) return false;
        var keys = new List<string>();
        foreach (var window in windows)
        {
            if (window is not JsonArray array || array.Any(key => key is not JsonValue)) return false;
            keys.AddRange(array.Select(key => key!.ToString()));
        }
        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Count && keys.ToHashSet(StringComparer.Ordinal).SetEquals(sources.Select(pair => pair.Key));
    }

    private static bool CleanWindow(List<JsonObject> reviews, JsonObject manifest, int index, string[] purposes)
    {
        var keys = manifest["windows"]![index]!.AsArray();
        var matches = reviews.Where(review => review["sourceCoverage"] is JsonObject coverage &&
            coverage["basisHash"]?.ToString() == manifest["basisHash"]?.ToString() &&
            coverage["setHash"]?.ToString() == manifest["setHash"]?.ToString() && coverage["windowIndex"]?.ToString() == index.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            review["candidateHash"]?.ToString() == manifest["candidateHash"]?.ToString() && review["outcome"]?.ToString() == "unverified" &&
            review["response"]?["findings"] is JsonArray { Count: 0 } && review["response"]?["inconclusive"] is null &&
            review["response"]?["evidenceRequests"] is null && review["fingerprint"]?.ToString() is { Length: 64 } fingerprint && fingerprint.All(char.IsAsciiHexDigit) &&
            JsonNode.DeepEquals(review["closedRequirementIds"], manifest["basis"]!["closedRequirementIds"]) &&
            review["rawResponse"] is JsonObject { Count: 2 } raw && raw["action"] is JsonObject action &&
            action["kind"]?.ToString() == "findings" && action["findings"] is JsonArray { Count: 0 } &&
            action.All(pair => pair.Key is "kind" or "findings" or "resolutions") &&
            JsonNode.DeepEquals(action["resolutions"], review["response"]!["resolutions"]) &&
            JsonNode.DeepEquals(raw["uncertainty"], review["response"]!["uncertainty"]) &&
            review["response"]!["uncertainty"] is JsonValue uncertainty && uncertainty.TryGetValue<string>(out var message) && message.Length <= 512 &&
            coverage["version"]?.ToString() == "1" && coverage["windowCount"]?.ToString() == manifest["windows"]!.AsArray().Count.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            coverage["windowHash"]?.ToString() == Hash(keys) && JsonNode.DeepEquals(coverage["sourceIds"], keys) &&
            review["reviewCoverage"] is JsonArray scopes && purposes.All(purpose => scopes.Any(scope => scope?.ToString() == purpose))).ToArray();
        return matches.Length == 1;
    }

    private static List<Unit> Units(PlanningSession state)
    {
        var result = new List<Unit>();
        foreach (var row in PlanningCompactConstruction.OriginalRequirementRows(state).OfType<JsonObject>())
        {
            var original = new JsonObject { ["id"] = row["id"]!.DeepClone(), ["original"] = row["original"]!.DeepClone(),
                ["sources"] = row["sources"]!.DeepClone() };
            foreach (var source in original["sources"]!.AsArray().OfType<JsonObject>())
                if (state.Construction!.Compact!.Sources.SingleOrDefault(item => item.Id == source["id"]?.ToString()) is { } exact)
                { source["text"] = exact.Text; source["continuationOf"] = exact.ContinuationOf; }
            result.Add(new("requirement:" + row["id"]!.ToString(), "requirement", row, original));
        }
        // Global continuation chains also form indivisible source units.
        foreach (var row in PlanningCompactConstruction.OriginalGlobalRows(state).OfType<JsonObject>())
        {
            var source = state.Construction!.Compact!.Sources.Single(item => item.Id == row["id"]!.ToString());
            if (source.ContinuationOf is not null) row["continuationOf"] = source.ContinuationOf;
            var previous = source.ContinuationOf is null ? null : result.LastOrDefault(unit => unit.Kind == "global" &&
                unit.Original["sources"]!.AsArray().Any(node => node?["id"]?.ToString() == source.ContinuationOf));
            if (previous is not null)
                previous.Original["sources"]!.AsArray().Add(row.DeepClone());
            else result.Add(new("global:" + source.Id, "global", row.DeepClone().AsObject(), new JsonObject { ["sources"] = new JsonArray(row.DeepClone()) }));
        }
        return result;
    }

    private static JsonObject Core(JsonObject context)
    {
        var core = context.DeepClone().AsObject();
        core.Remove("requirements"); core.Remove("globalConstraints"); core.Remove("sourceCoverage");
        if (core["evidence"] is JsonObject evidence)
            foreach (var key in evidence.Select(item => item.Key).Where(IsOriginal).ToArray())
            {
                if (key.StartsWith("page:requirements:", StringComparison.Ordinal) && evidence[key]?["next"] is JsonValue next &&
                    !evidence.ContainsKey(next.ToString())) core["nextRequirements"] = next.DeepClone();
                evidence.Remove(key);
            }
        return core;
    }

    private static JsonObject Window(JsonObject core, List<Unit> units, IEnumerable<string> keys, JsonObject metadata)
    {
        var selected = keys.ToHashSet(StringComparer.Ordinal); var context = core.DeepClone().AsObject();
        context["requirements"] = new JsonArray(units.Where(unit => selected.Contains(unit.Key) && unit.Kind == "requirement").Select(unit => unit.Value.DeepClone()).ToArray());
        context["globalConstraints"] = new JsonArray(units.Where(unit => selected.Contains(unit.Key) && unit.Kind == "global")
            .SelectMany(unit => unit.Original["sources"]!.AsArray()).Select(row => row!.DeepClone()).ToArray());
        if (context["defectProgress"] is JsonArray defects)
        {
            var originals = units.Where(unit => selected.Contains(unit.Key)).SelectMany(SourceIds).ToHashSet(StringComparer.Ordinal);
            foreach (var defect in defects.ToArray())
                if (defect?["obligationSourceIds"] is JsonArray ids && !ids.All(id => originals.Contains(id!.ToString()))) defects.Remove(defect);
            if (defects.Count == 0) context.Remove("defectProgress");
        }
        context["sourceCoverage"] = metadata; return context;
    }

    private static List<List<string>> PackingGroups(List<Unit> units, JsonObject core, PlanningSession state)
    {
        var groups = units.Select(unit => new List<string> { unit.Key }).ToList();
        foreach (var defect in (core["defectProgress"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (defect["obligationSourceIds"] is not JsonArray { Count: > 0 } ids)
                throw new PlanningConflictException("A retained defect lost its exact original source identity.");
            var sources = ids.Select(id => id!.ToString()).ToHashSet(StringComparer.Ordinal);
            var linked = units.Where(unit => SourceIds(unit).Any(sources.Contains)).Select(unit => unit.Key).ToHashSet(StringComparer.Ordinal);
            if (!sources.IsSubsetOf(units.Where(unit => linked.Contains(unit.Key)).SelectMany(SourceIds)))
                throw new PlanningConflictException("A retained defect's original evidence is no longer available.");
            Join(linked);
        }
        // Targeted original retrieval must actually reach the next review. Link
        // those whole clauses with its issued window instead of silently dropping
        // them or repeating unrelated originals in every request.
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (address, value) in state.Construction!.Evidence ?? [])
        {
            if (address.StartsWith("requirement:", StringComparison.Ordinal)) requested.Add(address);
            else if (address.StartsWith("source:", StringComparison.Ordinal))
                requested.UnionWith(units.Where(unit => SourceIds(unit).Contains(address[7..], StringComparer.Ordinal)).Select(unit => unit.Key));
            else if (address.StartsWith("page:requirements:", StringComparison.Ordinal) && value?["items"] is JsonArray requirements)
                requested.UnionWith(requirements.OfType<JsonObject>().Select(row => "requirement:" + row["id"]!.ToString()));
        }
        requested.IntersectWith(units.Select(unit => unit.Key));
        if (requested.Count > 0)
        {
            if (state.Construction.PendingReviewContext?["sourceCoverage"]?["sourceIds"] is JsonArray previous)
                requested.UnionWith(previous.Select(key => key!.ToString()).Where(key => units.Any(unit => unit.Key == key)));
            Join(requested);
        }
        return groups;

        void Join(HashSet<string> linked)
        {
            var overlapping = groups.Where(group => group.Any(linked.Contains)).ToArray();
            if (overlapping.Length == 0) return;
            var first = groups.IndexOf(overlapping[0]);
            var merged = overlapping.SelectMany(group => group).ToList();
            foreach (var group in overlapping) groups.Remove(group);
            groups.Insert(first, merged);
        }
    }

    private static IEnumerable<string> SourceIds(Unit unit) => unit.Original["sources"]!.AsArray()
        .OfType<JsonObject>().Select(source => source["id"]!.ToString());

    private static JsonObject Metadata(string basis, string set, int count, int index, IEnumerable<string> keys)
    {
        var sourceIds = Strings(keys);
        return new() { ["version"] = 1, ["basisHash"] = basis, ["setHash"] = set, ["windowCount"] = count,
            ["windowIndex"] = index, ["windowHash"] = Hash(sourceIds), ["sourceIds"] = sourceIds };
    }
    private static string Claims(PlanningConstructionStep step, List<string> closed, bool complete) => Hash(new JsonObject
        { ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep), ["closedScopes"] = Strings(closed), ["complete"] = complete });
    private static string EvidenceHash(PlanningSession state) => Hash(new JsonObject((state.Construction!.Evidence ?? [])
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))));
    private static bool IsOriginal(string address) => address.StartsWith("requirement:", StringComparison.Ordinal) ||
        address.StartsWith("source:", StringComparison.Ordinal) || address.StartsWith("page:requirements:", StringComparison.Ordinal);
    private static JsonObject SourceHashes(List<Unit> units) => new(units.Select(unit => new KeyValuePair<string, JsonNode?>(unit.Key, JsonValue.Create(Hash(unit.Original)))));
    private static string ContractHash(PlanningCapability capability) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string Hash(JsonNode node) => PlanningGraphCompiler.Fingerprint(node.ToJsonString());
    private static string SetHash(string basis, JsonArray windows) => PlanningGraphCompiler.Fingerprint(basis + "\n" + windows.ToJsonString());
    private static bool Fits(LLMRequest request, PlanningSession state) => ModelRequestAdmission.EstimateTokens(request) <=
        PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningConstruction.LocalPurpose);
}
