using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstructionReviewCoverage
{
    internal const string IncrementalInstructions = """
        reviewPropositions are host-issued claims tied to whole original clauses and exact subjects. Acknowledge every issued ID exactly once with subject or broad. Use subject only where offered and only after checking that the whole original clause's present behavioral obligation is confined to that exact subject; a selected association, matching name/type, interface declaration or empty findings is insufficient. Mixed, global, negative, crosscutting or uncertain applicability is broad. Report actual defects through findings, or request evidence/return inconclusive when this review cannot finish. An acknowledgment never establishes execution success or grants permission.
        reusedPropositions retain original reviews, not new clean verdicts. Review all new behavior, interactions, necessary work and current closure claims against the current window. requiredPurposes identifies the reviewed obligation: a conserved local behavior does not satisfy the new fragment's intermediate interaction review. Preserved producer meaning does not validate a new consumer or binding. Retrieve an original whenever a new interaction needs its exact meaning. Completion claims remain fresh even when contributing behavior was previously reviewed.
        """;

    private static JsonObject SelectIncremental(PlanningSession state, JsonObject context,
        Func<JsonObject, int, LLMRequest> prepare, JsonObject? reassessment)
    {
        var candidate = state.Construction!.Candidate!;
        var units = Units(state); var core = Core(context);
        var manifest = IncrementalCurrentManifest(state, candidate);
        if (manifest is null)
        {
            var basis = Basis(state, candidate, units);
            basis["propositions"] = Propositions(state, candidate, units);
            basis["incrementalCompletion"] = ClaimsCompletion(state, candidate);
            basis["incrementalComplete"] = candidate.Extension.Complete;
            basis["incrementalClaimScopes"] = ClaimScopes(state.Construction!.Compact!.CurrentStep!, candidate.Extension.CloseScopes);
            basis["incrementalClaimSources"] = ClaimSources(state, state.Construction.Compact.CurrentStep!);
            basis["incrementalConsumerClaim"] = ContinuationClaimHash(state, state.Construction.Compact.CurrentStep!);
            basis["requiredPurposes"] = Strings(Purposes(candidate.NeedsIntermediateReview));
            var originalSet = reassessment is null ? null : candidate.Reviews.LastOrDefault(row =>
                row["purpose"]?.ToString() == ManifestPurpose && row["setHash"]?.ToString() == reassessment["setHash"]?.ToString() &&
                IncrementalManifestValid(state, row, units) && SameReassessmentBasis(row["basis"]!.AsObject(), basis) &&
                row["reuse"]!.AsArray().OfType<JsonObject>().All(reuse => ReuseValid(state, row, reuse, Purposes(candidate.NeedsIntermediateReview), candidate.Plan)));
            if (reassessment is not null && originalSet is null)
                throw new PlanningConflictException("A proposition reassessment lost its exact original source-window set.");
            var basisHash = Hash(basis);
            manifest = new JsonObject { ["purpose"] = ManifestPurpose, ["version"] = 2, ["outcome"] = "source_windows",
                ["candidateHash"] = candidate.Hash, ["basis"] = basis, ["basisHash"] = basisHash,
                ["sources"] = SourceHashes(units), ["reuse"] = new JsonArray() };
            var reusable = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            // Completion, explicit retrieval and unresolved corrections cannot borrow
            // a narrower contribution or silently discard requested original text.
            if (originalSet is not null)
                foreach (var row in originalSet["reuse"]!.AsArray().OfType<JsonObject>())
                    reusable.Add(row["sourceKey"]!.ToString(), row.DeepClone().AsObject());
            if (originalSet is null && !PlanningConstruction.UnresolvedDefects(candidate))
                foreach (var proposition in basis["propositions"]!.AsArray().OfType<JsonObject>())
                    if (MayReuseForClaims(state, candidate, proposition) &&
                        !OriginalRequested(state, units.Single(unit => unit.Key == proposition["sourceKey"]!.ToString())) &&
                        FindReuse(state, candidate, proposition) is { } reuse)
                        reusable.Add(proposition["sourceKey"]!.ToString(), reuse);
            var groups = PackingGroups(units, core, state);
            foreach (var group in originalSet is null ? groups : [])
                if (group.Any(key => !reusable.ContainsKey(key)))
                    foreach (var key in group) reusable.Remove(key);
            // A novel fragment still needs an original-grounded semantic review.
            if (reusable.Count == units.Count) reusable.Clear();
            foreach (var row in reusable.Values) manifest["reuse"]!.AsArray().Add((JsonNode)row);
            var windows = originalSet is null ? new List<List<string>>() : originalSet["windows"]!.AsArray()
                .Select(window => window!.AsArray().Select(key => key!.ToString()).ToList()).ToList();
            var current = new List<string>();
            foreach (var group in originalSet is null ? groups.Where(group => group.Any(key => !reusable.ContainsKey(key))) : [])
            {
                var proposed = current.Concat(group).ToList();
                var probe = IncrementalWindow(core, units, manifest, proposed, units.Count, units.Count, new string('0', 64));
                if (current.Count > 0 && !Fits(prepare(probe, units.Count), state)) { windows.Add(current); current = group; }
                else current = proposed;
            }
            if (current.Count > 0) windows.Add(current);
            if (windows.Count == 0) windows.Add([]);
            manifest["windows"] = new JsonArray(windows.Select(keys => (JsonNode)Strings(keys)).ToArray());
            manifest["setHash"] = IncrementalSetHash(manifest);
            candidate.Reviews.Add(manifest);
            candidate.LocalReviewed = false; candidate.IntermediateReviewed = false;
        }
        var remaining = IncrementalMissing(state, candidate.Reviews, manifest, Purposes(candidate.NeedsIntermediateReview)).ToArray();
        if (remaining.Length == 0) throw new PlanningConflictException("All required propositions were already reviewed.");
        JsonObject Present(int index) => IncrementalWindow(core, units, manifest,
            manifest["windows"]![index]!.AsArray().Select(key => key!.ToString()).ToList(), manifest["windows"]!.AsArray().Count,
            index, manifest["setHash"]!.ToString());
        if (reassessment is not null)
        {
            var index = reassessment["windowIndex"]!.GetValue<int>();
            if (!remaining.Contains(index) || !JsonNode.DeepEquals(manifest["windows"]![index], reassessment["sourceIds"]))
                throw new PlanningConflictException("A proposition reassessment cannot change its original window.");
            return Present(index);
        }
        foreach (var index in remaining) if (!Fits(prepare(Present(index), remaining.Length), state)) return Present(index);
        return Present(remaining[0]);
    }

    private static bool SameReassessmentBasis(JsonObject original, JsonObject current)
    {
        var supported = original.DeepClone().AsObject();
        supported["evidence"] = current["evidence"]!.DeepClone();
        return JsonNode.DeepEquals(supported, current);
    }

    private static JsonArray Propositions(PlanningSession state, PlanningConstructionCandidate candidate, List<Unit> units) =>
        new(units.Select(unit => (JsonNode)Proposition(state, candidate, unit)).ToArray());

    private static JsonObject Proposition(PlanningSession state, PlanningConstructionCandidate candidate, Unit unit)
    {
        JsonObject? subject = null; JsonObject? proof = null;
        if (unit.Kind == "requirement")
        {
            // Preserve a previously acknowledged exact subject only through its
            // original receipt and a current conservation proof. Associations do
            // not select a subject, and a new caller still needs fresh review.
            foreach (var checkpoint in state.Construction!.Checkpoints.AsEnumerable().Reverse())
                if (IncrementalCheckpointManifest(state, checkpoint) is { } origin)
                    foreach (var prior in origin["basis"]!["propositions"]!.AsArray().OfType<JsonObject>())
                        if (prior["sourceKey"]?.ToString() == unit.Key && prior["sourceHash"]?.ToString() == Hash(unit.Original) &&
                            FindReuse(state, candidate, prior) is not null)
                            return prior.DeepClone().AsObject();
            var sources = SourceIds(unit).ToHashSet(StringComparer.Ordinal);
            var outputs = (state.Construction!.Compact!.InterfaceExtractions ?? [])
                .Where(extraction => extraction.Complete).SelectMany(extraction => extraction.Dispositions ?? [])
                .Where(disposition => sources.Contains(disposition.SourceId) && disposition.Kind == "interfaces" &&
                    state.Construction.Compact.Sources.Any(source => source.Id == disposition.SourceId && source.Version == disposition.Version))
                .SelectMany(disposition => disposition.Outputs).Distinct(StringComparer.Ordinal).ToArray();
            if (outputs.Length == 1 && state.Requirements!.Outputs?.Count(output => output.Name == outputs[0]) == 1)
            {
                subject = new JsonObject { ["kind"] = "output", ["name"] = outputs[0] };
                proof = PlanningConstructionDependencies.Capture(state, candidate.Plan, subject);
                if (proof is null) subject = null;
            }
            if (subject is null && candidate.Extension.Scope != "/root" &&
                candidate.Extension.CloseScopes.Contains(candidate.Extension.Scope, StringComparer.Ordinal))
            {
                // The host offers the actual newly closed selected boundary,
                // not a model's association or a name/type-based semantic guess.
                // The Judge must still establish whole-original locality.
                var group = candidate.Plan.Groups.SingleOrDefault(item => "/groups/" + item.Id + "/body" == candidate.Extension.Scope);
                subject = group is null ? new JsonObject { ["kind"] = "scope", ["path"] = candidate.Extension.Scope }
                    : new JsonObject { ["kind"] = "group", ["id"] = group.Id };
                proof = PlanningConstructionDependencies.Capture(state, candidate.Plan, subject);
                if (proof is null) subject = null;
            }
        }
        var result = new JsonObject { ["sourceKey"] = unit.Key, ["sourceHash"] = Hash(unit.Original),
            ["kind"] = unit.Kind == "global" ? "global" : "local_behavior", ["subject"] = subject,
            ["dependencyProof"] = proof, ["requiredPurposes"] = Strings(subject is not null ? ["local"] : Purposes(candidate.NeedsIntermediateReview)) };
        result["id"] = "proposition:" + Hash(result); return result;
    }

    private static JsonObject IncrementalWindow(JsonObject core, List<Unit> units, JsonObject manifest, List<string> keys, int count, int index, string set)
    {
        var metadata = Metadata(manifest["basisHash"]!.ToString(), set, count, index, keys); metadata["version"] = 2;
        var context = Window(core, units, keys, metadata);
        context["reviewPropositions"] = WindowPropositions(manifest, keys);
        context["reusedPropositions"] = new JsonArray(manifest["reuse"]!.AsArray().OfType<JsonObject>().Select(row => (JsonNode)new JsonObject
        {
            ["sourceKey"] = row["sourceKey"]!.DeepClone(), ["propositionId"] = row["propositionId"]!.DeepClone(),
            ["checkpoint"] = row["checkpoint"]!.DeepClone(), ["sourceRequestId"] = row["sourceRequestId"]!.DeepClone(),
            ["subject"] = row["subject"]!.DeepClone(), ["reviewFingerprint"] = row["reviewFingerprint"]!.DeepClone()
        }).ToArray());
        return context;
    }

    private static JsonArray WindowPropositions(JsonObject manifest, IEnumerable<string> keys)
    {
        var selected = keys.ToHashSet(StringComparer.Ordinal);
        var result = new JsonArray(manifest["basis"]!["propositions"]!.AsArray().OfType<JsonObject>()
            .Where(row => selected.Contains(row["sourceKey"]!.ToString())).Select(row =>
            {
                var displayed = row.DeepClone().AsObject(); displayed.Remove("dependencyProof"); displayed.Remove("sourceHash"); return (JsonNode)displayed;
            }).ToArray());
        Add("interaction");
        if (manifest["basis"]!["incrementalCompletion"]?.GetValue<bool>() == true) Add("completion");
        if (manifest["basis"]!["boundaryProof"] is not null || manifest["basis"]!["continuationProof"] is not null) Add("transition");
        return result;
        void Add(string kind) => result.Add((JsonNode)new JsonObject { ["id"] = "proposition:" +
            Hash(new JsonObject { ["basis"] = manifest["basisHash"]!.DeepClone(), ["kind"] = kind, ["sources"] = Strings(selected.Order(StringComparer.Ordinal)) }),
            ["kind"] = kind, ["subject"] = null, ["requiredPurposes"] = Strings(manifest["basis"]!["requiredPurposes"]!.AsArray().Select(value => value!.ToString())) });
    }

    internal static void ConstrainPropositionAcknowledgments(JsonObject context, JsonObject schema)
    {
        if (context["reviewPropositions"] is not JsonArray propositions) return;
        // Equal applicability domains share one object alternative. Every exact
        // host ID and the original allowed ID/applicability pairs remain present.
        var choices = new JsonArray(propositions.OfType<JsonObject>().GroupBy(row => row["subject"] is JsonObject)
            .Select(group => (JsonNode)PlanningSchemas.Object(
                ("id", PlanningSchemas.Enum(group.Select(row => row["id"]!.ToString()).ToArray())),
                ("applicability", group.Key ? PlanningSchemas.Enum("subject", "broad") : PlanningSchemas.Enum("broad")))).ToArray());
        foreach (var action in schema["properties"]!["action"]!["anyOf"]!.AsArray().OfType<JsonObject>())
            if (action["properties"]!["kind"]!["enum"]![0]!.ToString() == "findings")
            {
                action["properties"]!["acknowledgments"] = PlanningSchemas.Array(new JsonObject { ["anyOf"] = choices }, propositions.Count, propositions.Count);
                action["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("acknowledgments"));
            }
    }

    internal static bool AcknowledgmentsValid(JsonObject context, JsonObject response) => context["reviewPropositions"] is not JsonArray propositions ||
        AcknowledgmentsValid(propositions, response["acknowledgments"]);

    private static bool AcknowledgmentsValid(JsonArray propositions, JsonNode? acknowledgments)
    {
        if (acknowledgments is not JsonArray rows || rows.Count != propositions.Count || rows.Any(row => row is not JsonObject)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.OfType<JsonObject>())
        {
            var id = row["id"]?.ToString();
            if (id is null || !ids.Add(id) || propositions.OfType<JsonObject>().SingleOrDefault(proposition => proposition["id"]?.ToString() == id) is not { } proposition ||
                row.Count != 2 || row["applicability"]?.ToString() is not ("subject" or "broad") ||
                row["applicability"]?.ToString() == "subject" && proposition["subject"] is not JsonObject) return false;
        }
        return true;
    }

    internal static void RetainPropositionReceipt(JsonObject context, JsonObject review, LLMRequest request)
    {
        if (context["reviewPropositions"] is not JsonArray propositions) return;
        var serialized = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        review["sourceRequestId"] = request.ClientRequestId;
        review["sourceRequestHash"] = PlanningGraphCompiler.Fingerprint(serialized);
        var unsigned = JsonSerializer.Deserialize(serialized, PlanningJsonContext.Default.LLMRequest)!;
        unsigned.ClientRequestId = null;
        review["sourceRequestContentHash"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(unsigned, PlanningJsonContext.Default.LLMRequest));
        review["rawResponseHash"] = Hash(review["rawResponse"]!);
        review["reviewPropositions"] = propositions.DeepClone();
    }

    private static bool IncrementalComplete(PlanningSession state, PlanningConstructionCandidate candidate) =>
        !PlanningConstruction.UnresolvedDefects(candidate) && IncrementalCurrentManifest(state, candidate) is { } manifest &&
        IncrementalUnion(state, candidate.Reviews, manifest, Purposes(candidate.NeedsIntermediateReview), candidate.Plan);

    private static int IncrementalRemainingCalls(PlanningSession state) => state.Construction?.Candidate is { } candidate &&
        IncrementalCurrentManifest(state, candidate) is { } manifest
            ? IncrementalMissing(state, candidate.Reviews, manifest, Purposes(candidate.NeedsIntermediateReview)).Count() : 1;

    private static bool IncrementalReviewed(PlanningSession state, PlanningConstructionCheckpoint checkpoint, string purpose) =>
        IncrementalCheckpointManifest(state, checkpoint) is { } manifest && CurrentContractsValid(state, manifest) &&
        IncrementalUnion(state, checkpoint.Reviews, manifest, [purpose == PlanningConstruction.LocalPurpose ? "local" : "intermediate"],
            checkpoint.PlanHash == PlanningConstruction.Hash(state.Plan) ? state.Plan : null);

    internal static JsonObject? ReviewedTransitionBasis(PlanningSession state, PlanningConstructionCheckpoint checkpoint) =>
        PlanningCompactConstruction.UsesIncrementalReview(state) && IncrementalCheckpointManifest(state, checkpoint) is { } manifest && CurrentContractsValid(state, manifest) &&
        IncrementalUnion(state, checkpoint.Reviews, manifest, Purposes(checkpoint.NeedsIntermediateReview),
            checkpoint.PlanHash == PlanningConstruction.Hash(state.Plan) ? state.Plan : null) ? manifest : null;

    private static JsonObject? IncrementalCurrentManifest(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        if (candidate.BaseHash != PlanningConstruction.Hash(state.Plan) || candidate.Hash != PlanningConstruction.Hash(candidate.Plan) ||
            !JsonNode.DeepEquals(PlanningConstruction.Node(candidate.Extension.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding),
                PlanningConstruction.Node(step.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding)) ||
            !JsonNode.DeepEquals(PlanningConstruction.Node(candidate.Extension, PlanningJsonContext.Default.PlanningConstructionExtension)!["boundaryTransitions"],
                PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!["boundaryTransitions"])) return null;
        var units = Units(state); var basis = Basis(state, candidate, units);
        basis["propositions"] = Propositions(state, candidate, units); basis["incrementalCompletion"] = ClaimsCompletion(state, candidate);
        basis["incrementalComplete"] = candidate.Extension.Complete;
        basis["incrementalClaimScopes"] = ClaimScopes(step, candidate.Extension.CloseScopes);
        basis["incrementalClaimSources"] = ClaimSources(state, step);
        basis["incrementalConsumerClaim"] = ContinuationClaimHash(state, step);
        basis["requiredPurposes"] = Strings(Purposes(candidate.NeedsIntermediateReview));
        return candidate.Reviews.LastOrDefault(row => row["version"]?.ToString() == "2" && row["purpose"]?.ToString() == ManifestPurpose &&
            JsonNode.DeepEquals(row["basis"], basis) && ContinuationClaimCurrent(state, step, basis) && IncrementalManifestValid(state, row, units) &&
            (!PlanningConstruction.UnresolvedDefects(candidate) || row["reuse"] is JsonArray { Count: 0 }) &&
            row["reuse"]!.AsArray().OfType<JsonObject>().All(reuse => ReuseValid(state, row, reuse, Purposes(candidate.NeedsIntermediateReview), candidate.Plan)));
    }

    private static JsonObject? IncrementalCheckpointManifest(PlanningSession state, PlanningConstructionCheckpoint checkpoint) =>
        checkpoint.Step is not { } step ? null : checkpoint.Reviews.LastOrDefault(row => row["version"]?.ToString() == "2" &&
            row["purpose"]?.ToString() == ManifestPurpose && row["candidateHash"]?.ToString() == checkpoint.PlanHash &&
            row["basis"]?["baseline"]?.ToString() == checkpoint.BaseHash && row["basis"]?["candidate"]?.ToString() == checkpoint.PlanHash &&
            row["basis"]?["scope"]?.ToString() == step.Scope && row["basis"]?["claims"]?.ToString() ==
                Claims(step, checkpoint.ClosedScopes, checkpoint.Fragment["complete"]?.GetValue<bool>() == true) &&
            row["basis"]?["incrementalComplete"]?.GetValue<bool>() == (checkpoint.Fragment["complete"]?.GetValue<bool>() == true) &&
            JsonNode.DeepEquals(row["basis"]?["incrementalClaimScopes"], ClaimScopes(step, checkpoint.ClosedScopes)) &&
            JsonNode.DeepEquals(row["basis"]?["incrementalClaimSources"], ClaimSources(state, step)) &&
            row["basis"]?["incrementalConsumerClaim"]?.ToString() == ContinuationClaimHash(state, step) &&
            JsonNode.DeepEquals(row["basis"]?["closedRequirementIds"], Strings(step.CloseRequirementIds)) &&
            IncrementalManifestValid(state, row, Units(state), allowAddedSources: true));

    private static bool IncrementalManifestValid(PlanningSession state, JsonObject manifest, List<Unit> units, bool allowAddedSources = false)
    {
        if (manifest["version"]?.ToString() != "2" || manifest["basis"] is not JsonObject basis || manifest["basisHash"]?.ToString() != Hash(basis) ||
            manifest["setHash"]?.ToString() != IncrementalSetHash(manifest) || manifest["sources"] is not JsonObject sources ||
            (!allowAddedSources && !JsonNode.DeepEquals(sources, SourceHashes(units))) ||
            sources.Any(pair => SourceHashes(units)[pair.Key]?.ToString() != pair.Value?.ToString()) || basis["sourceHash"]?.ToString() != Hash(sources) ||
            basis["propositions"] is not JsonArray propositions || propositions.Count != sources.Count ||
            manifest["windows"] is not JsonArray { Count: > 0 } windows || manifest["reuse"] is not JsonArray reuse) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in propositions)
        {
            if (row is not JsonObject proposition || proposition["sourceKey"]?.ToString() is not { } key || !keys.Add(key) ||
                proposition["sourceHash"]?.ToString() != sources[key]?.ToString()) return false;
            var copy = proposition.DeepClone().AsObject(); copy.Remove("id");
            if (proposition["id"]?.ToString() != "proposition:" + Hash(copy)) return false;
        }
        var covered = new List<string>();
        foreach (var window in windows)
        {
            if (window is not JsonArray array || array.Any(key => key is not JsonValue)) return false;
            covered.AddRange(array.Select(key => key!.ToString()));
        }
        foreach (var row in reuse)
        {
            if (row is not JsonObject value || value["sourceKey"] is not JsonValue key) return false;
            covered.Add(key.ToString());
        }
        if (covered.Count != covered.Distinct(StringComparer.Ordinal).Count() || !keys.SetEquals(covered)) return false;
        // Historical acceptance retains its exact contract identities. Current
        // applicability is checked against the subject's complete proof instead
        // of invalidating independent facts for an unrelated catalog revision.
        return basis["policy"]?.ToString() == PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy)) &&
            basis["contracts"] is JsonArray contracts && contracts.All(node => node is JsonObject contract &&
                contract["id"] is JsonValue && contract["version"] is JsonValue && IsHash(contract["hash"]));
    }

    private static bool IncrementalUnion(PlanningSession state, List<JsonObject> reviews, JsonObject manifest, string[] purposes, TaskPlan? current = null) =>
        manifest["reuse"]!.AsArray().OfType<JsonObject>().All(row => ReuseValid(state, manifest, row, purposes, current)) &&
        !IncrementalMissing(state, reviews, manifest, purposes).Any();

    private static IEnumerable<int> IncrementalMissing(PlanningSession state, List<JsonObject> reviews, JsonObject manifest, string[] purposes) =>
        Enumerable.Range(0, manifest["windows"]!.AsArray().Count).Where(index => IncrementalCleanReceipt(state, reviews, manifest, index, purposes) is null);

    private static bool CurrentContractsValid(PlanningSession state, JsonObject manifest)
    {
        foreach (var contract in manifest["basis"]!["contracts"]!.AsArray().OfType<JsonObject>())
        {
            var matches = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
                .Where(capability => TaskOperations.Describe(capability).Id == contract["id"]?.ToString() &&
                    capability.Version == contract["version"]?.ToString()).ToArray();
            if (matches.Select(ContractHash).Distinct(StringComparer.Ordinal).Count() != 1) return false;
            matches = matches.DistinctBy(capability => (capability.Id, capability.Version)).ToArray();
            if (matches.Length != 1 || contract["hash"]?.ToString() != ContractHash(matches[0])) return false;
        }
        return true;
    }

    private static JsonObject? IncrementalCleanReceipt(PlanningSession state, List<JsonObject> reviews, JsonObject manifest, int index, string[] purposes)
    {
        var expected = WindowPropositions(manifest, manifest["windows"]![index]!.AsArray().Select(key => key!.ToString()));
        var rows = reviews.Where(row => row["sourceCoverage"]?["basisHash"]?.ToString() == manifest["basisHash"]?.ToString() &&
            row["sourceCoverage"]?["windowIndex"]?.ToString() == index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Supported reassessment retains its earlier inconclusive/invalid report.
        // It cannot erase an actual adverse finding or create two clean receipts.
        if (rows.Any(row => row["outcome"]?.ToString() == "revision_required")) return null;
        var clean = rows.Where(Valid).ToArray();
        return clean.Length == 1 ? clean[0] : null;

        bool Valid(JsonObject review)
        {
            if (!JsonNode.DeepEquals(review["reviewPropositions"], expected) ||
                !AcknowledgmentsValid(expected, review["response"]?["acknowledgments"]) ||
                !JsonNode.DeepEquals(review["response"]?["acknowledgments"], review["rawResponse"]?["action"]?["acknowledgments"]) ||
                review["sourceRequestId"]?.ToString() is not { Length: > 65 } id || !IsHash(review["sourceRequestHash"]) ||
                !IsHash(review["sourceRequestContentHash"]) || !id.EndsWith(":" + review["sourceRequestContentHash"]!.ToString(), StringComparison.Ordinal) ||
                (state.RequestMeasurements ?? []).Count(measurement => measurement.RequestId == id &&
                    measurement.RequestHash == review["sourceRequestHash"]!.ToString() && measurement.Admission == "admitted" &&
                    measurement.Phase == review["purpose"]?.ToString()) != 1 ||
                review["rawResponse"] is not JsonObject raw || review["rawResponseHash"]?.ToString() != Hash(raw)) return false;
            var copy = review.DeepClone().AsObject(); copy["rawResponse"]!["action"]!.AsObject().Remove("acknowledgments");
            copy["sourceCoverage"]!["version"] = 1;
            return CleanWindow([copy], manifest, index, purposes);
        }
    }

    private static JsonObject? FindReuse(PlanningSession state, PlanningConstructionCandidate candidate, JsonObject proposition)
    {
        if (proposition["dependencyProof"] is not JsonObject proof || proposition["subject"] is not JsonObject subject ||
            !PlanningConstructionDependencies.Preserved(state, candidate.Plan, proof)) return null;
        foreach (var checkpoint in state.Construction!.Checkpoints.AsEnumerable().Reverse())
        {
            if (ProofInvalidated(checkpoint, proof) || IncrementalCheckpointManifest(state, checkpoint) is not { } origin) continue;
            var original = origin["basis"]!["propositions"]!.AsArray().OfType<JsonObject>()
                .SingleOrDefault(row => row["sourceKey"]?.ToString() == proposition["sourceKey"]?.ToString() && row["id"]?.ToString() == proposition["id"]?.ToString());
            if (original is null) continue;
            var windows = origin["windows"]!.AsArray();
            for (var index = 0; index < windows.Count; index++)
                if (windows[index]!.AsArray().Any(key => key!.ToString() == proposition["sourceKey"]!.ToString()) &&
                    IncrementalCleanReceipt(state, checkpoint.Reviews, origin, index, ["local"]) is not null)
                {
                    var receipt = IncrementalCleanReceipt(state, checkpoint.Reviews, origin, index, ["local"])!;
                    if (!SubjectAcknowledged(receipt, original["id"]!.ToString())) continue;
                    return new JsonObject { ["sourceKey"] = proposition["sourceKey"]!.DeepClone(), ["propositionId"] = original["id"]!.DeepClone(),
                        ["checkpoint"] = checkpoint.Sequence, ["originBasisHash"] = origin["basisHash"]!.DeepClone(), ["windowIndex"] = index,
                        ["reviewFingerprint"] = receipt["fingerprint"]!.DeepClone(), ["sourceRequestId"] = receipt["sourceRequestId"]!.DeepClone(),
                        ["sourceRequestHash"] = receipt["sourceRequestHash"]!.DeepClone(), ["rawResponseHash"] = receipt["rawResponseHash"]!.DeepClone(),
                        ["subject"] = subject.DeepClone() };
                }
        }
        return null;
    }

    private static bool ReuseValid(PlanningSession state, JsonObject manifest, JsonObject reuse, string[] purposes, TaskPlan? current = null)
    {
        if (reuse["checkpoint"] is not JsonValue number || !number.TryGetValue<int>(out var sequence) || sequence < 1 || sequence > state.Construction!.Checkpoints.Count) return false;
        var checkpoint = state.Construction.Checkpoints[sequence - 1];
        if (checkpoint.Sequence != sequence ||
            IncrementalCheckpointManifest(state, checkpoint) is not { } origin || origin["basisHash"]?.ToString() != reuse["originBasisHash"]?.ToString() ||
            reuse["windowIndex"] is not JsonValue window || !window.TryGetValue<int>(out var index) || index < 0 || index >= origin["windows"]!.AsArray().Count ||
            IncrementalCleanReceipt(state, checkpoint.Reviews, origin, index, ["local"]) is null) return false;
        var target = manifest["basis"]!["propositions"]!.AsArray().OfType<JsonObject>().SingleOrDefault(row => row["sourceKey"]?.ToString() == reuse["sourceKey"]?.ToString());
        var original = origin["basis"]!["propositions"]!.AsArray().OfType<JsonObject>().SingleOrDefault(row => row["sourceKey"]?.ToString() == reuse["sourceKey"]?.ToString());
        if (target is null || original is null || target["dependencyProof"] is not JsonObject proof || ProofInvalidated(checkpoint, proof) ||
            !JsonNode.DeepEquals(target, original) || !JsonNode.DeepEquals(target["requiredPurposes"], Strings(["local"])) || target["id"]?.ToString() != reuse["propositionId"]?.ToString() ||
            !ClaimsAllowReuse(manifest["basis"]!["incrementalComplete"]?.GetValue<bool>() != false,
                manifest["basis"]!["incrementalClaimScopes"] as JsonArray, manifest["basis"]!["incrementalClaimSources"] as JsonArray, target) ||
            !JsonNode.DeepEquals(target["subject"], reuse["subject"]) ||
            current is not null && !PlanningConstructionDependencies.Preserved(state, current, proof)) return false;
        var receipt = IncrementalCleanReceipt(state, checkpoint.Reviews, origin, index, ["local"])!;
        return new[] { "fingerprint", "sourceRequestId", "sourceRequestHash", "rawResponseHash" }.All(key =>
            receipt[key]?.ToString() == reuse[key == "fingerprint" ? "reviewFingerprint" : key]?.ToString()) && SubjectAcknowledged(receipt, target["id"]!.ToString());
    }

    private static bool SubjectAcknowledged(JsonObject receipt, string id) => receipt["response"]?["acknowledgments"] is JsonArray rows &&
        rows.Count(row => row?["id"]?.ToString() == id && row["applicability"]?.ToString() == "subject") == 1;
    private static bool ProofInvalidated(PlanningConstructionCheckpoint checkpoint, JsonObject proof) =>
        proof["projection"]?["tasks"] is not JsonArray tasks || tasks.Any(task =>
            checkpoint.InvalidatedTaskIds.Contains(task?["id"]?.ToString() ?? "", StringComparer.Ordinal));
    private static bool MayReuseForClaims(PlanningSession state, PlanningConstructionCandidate candidate, JsonObject proposition)
    {
        return ClaimsAllowReuse(candidate.Extension.Complete, ClaimScopes(state.Construction!.Compact!.CurrentStep!, candidate.Extension.CloseScopes),
            ClaimSources(state, state.Construction.Compact.CurrentStep!), proposition) &&
            PlanningConstructionDependencies.Preserved(state, candidate.Plan, proposition["dependencyProof"]!.AsObject());
    }
    private static JsonArray ClaimSources(PlanningSession state, PlanningConstructionStep step) => Strings(step.CloseRequirementIds
        .Concat((step.BoundaryTransitions ?? []).SelectMany(claim => claim.RequirementIds))
        .Concat(step.ContinuationBinding is { } association ? state.Construction!.Compact!.SuspendedSteps?
            .SingleOrDefault(item => item.Id == association.ContinuationId)?.Consumer.RequirementIds ?? [] : [])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    private static bool ContinuationClaimCurrent(PlanningSession state, PlanningConstructionStep step, JsonObject basis) =>
        step.ContinuationBinding is not { } association || state.Construction!.Compact!.SuspendedSteps?
            .SingleOrDefault(item => item.Id == association.ContinuationId) is { } item &&
            basis["continuationProof"]?["consumerHash"]?.ToString() == PlanningCompactConstruction.ContinuationConsumerHash(item);
    private static string? ContinuationClaimHash(PlanningSession state, PlanningConstructionStep step)
    {
        if (step.ContinuationBinding is not { } association) return null;
        if (state.Construction!.Compact!.SuspendedSteps?.SingleOrDefault(item => item.Id == association.ContinuationId) is not { } item)
            return "missing";
        var claim = PlanningConstruction.Node(item, PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!.AsObject();
        // Resume legitimately appends its reviewed producer to dependency bookkeeping.
        // Retain all immutable original consumer, prerequisite and authority fields;
        // current-candidate admission still checks the full original consumer hash.
        claim.Remove("status"); claim.Remove("lastPrerequisiteCheckpoint"); claim.Remove("dependencyHashes");
        return Hash(claim);
    }
    private static JsonArray ClaimScopes(PlanningConstructionStep step, IEnumerable<string> closed) => Strings(closed
        .Concat((step.BoundaryTransitions ?? []).Where(claim => claim.CloseScope).Select(claim => claim.Scope)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    private static bool ClaimsAllowReuse(bool complete, JsonArray? closedScopes, JsonArray? closedRequirements, JsonObject proposition)
    {
        if (complete || closedScopes is null || closedRequirements is null || closedScopes.Any(scope => scope?.ToString() == "/root") ||
            closedRequirements.Any(id => proposition["sourceKey"]?.ToString() == "requirement:" + id?.ToString()) ||
            proposition["dependencyProof"] is not JsonObject proof || proof["class"]?.ToString() != "deterministic_behavior_not_completion" ||
            proof["projection"]?["scopes"] is not JsonArray scopes) return false;
        // TaskPlan scope paths are flattened. The proof includes every actual
        // ancestor, preceding execution and cleanup scope, so exact membership
        // (not a guessed string-prefix ancestry) detects affected boundaries.
        return !scopes.Any(scope => closedScopes.Any(closed => closed?.ToString() == scope?["path"]?.ToString()));
    }
    private static bool OriginalRequested(PlanningSession state, Unit unit) => (state.Construction!.Evidence ?? []).Any(pair =>
        pair.Key == unit.Key || pair.Key.StartsWith("source:", StringComparison.Ordinal) && SourceIds(unit).Contains(pair.Key[7..], StringComparer.Ordinal) ||
        pair.Key.StartsWith("page:requirements:", StringComparison.Ordinal) && pair.Value?["items"] is JsonArray rows &&
            rows.Any(row => "requirement:" + row?["id"]?.ToString() == unit.Key));
    private static bool ClaimsCompletion(PlanningSession state, PlanningConstructionCandidate candidate) => candidate.Extension.Complete ||
        candidate.Extension.CloseScopes.Count > 0 || state.Construction!.Compact!.CurrentStep!.CloseRequirementIds.Count > 0;
    private static string[] Purposes(bool intermediate) => intermediate ? ["local", "intermediate"] : ["local"];
    private static bool IsHash(JsonNode? node) => node?.ToString() is { Length: 64 } value && value.All(char.IsAsciiHexDigit);
    private static string IncrementalSetHash(JsonObject manifest) => Hash(new JsonObject { ["basis"] = manifest["basisHash"]?.DeepClone(),
        ["windows"] = manifest["windows"]?.DeepClone(), ["reuse"] = manifest["reuse"]?.DeepClone() });
}
