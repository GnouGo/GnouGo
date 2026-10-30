using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private const string BusinessReviewInstructions = """
        Check business behavior from actual actions, arguments, values, controls, effects and results. The host/compiler checked technical types, contracts, references, scopes and bindings. Labels and non-executable objectives prove no behavior; transform instructions are executable. group declares a reusable body; call invokes it; an inline loop body is not a reusable group. Root outputs return to the caller; nested exports and written files are different.
        For current_fragment, assess the coherent behavior of fragment IDs, including pending accepted work, and interactions with supporting producers and ancestor controls. Legitimate future work in open scopes is not an omission; current arguments/effects must still respect originals. Finite iteration ceilings are not required business cardinalities. For completed_workflow, check every original obligation, composition, actual results and cleanup on success and failure.
        Return ok for sound business behavior and completion claims; needs_change only for a demonstrated current violation, citing original obligation IDs and concrete action/scope/output IDs; needs_context with exact offered addresses for missing necessary evidence. Missing context is not a proved defect. Assess value meaning and provenance, not just types. Reassess retained prior concerns against actual changes or contrary evidence: changed wording alone is no repair. No verdict establishes runtime success or authorizes effects.
        """;

    internal static JsonObject ConstructionReviewSchema(JsonObject context)
    {
        static JsonObject Reason() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 768 };
        var decisions = new JsonArray(PlanningSchemas.Object(("verdict", PlanningSchemas.Enum("ok")), ("reason", Reason())));
        var (obligations, targets) = BusinessFindingDomains(context);
        if (obligations.Length > 0 && targets.Length > 0)
            decisions.Add((JsonNode)PlanningSchemas.Object(("verdict", PlanningSchemas.Enum("needs_change")), ("reason", Reason()),
                ("obligations", PlanningSchemas.Array(PlanningSchemas.Enum(obligations), 1, 3)),
                ("targets", PlanningSchemas.Array(PlanningSchemas.Enum(targets), 1, 3))));
        var addresses = BusinessEvidenceAddresses(context);
        decisions.Add((JsonNode)PlanningSchemas.Object(("verdict", PlanningSchemas.Enum("needs_context")),
            ("reason", Reason()), ("contextRequests", addresses.Length == 0
                ? PlanningSchemas.Array(PlanningSchemas.String(), 0, 0) : PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, 8))));
        return PlanningSchemas.Object(("decision", new JsonObject { ["anyOf"] = decisions }));
    }

    internal static bool ConstructionReviewIssued(JsonNode? schema) => schema?["properties"]?["decision"]?["anyOf"] is JsonArray branches &&
        branches.Any(branch => branch?["properties"]?["verdict"]?["enum"]?[0]?.ToString() == "ok");

    internal static (JsonObject Context, LLMRequest Request) CreateConstructionReviewRequest(PlanningSession state, bool global)
    {
        var candidate = state.Construction!.Candidate!;
        var reconsideration = PendingBusinessReconsideration(state);
        var context = reconsideration?["context"]?.DeepClone().AsObject() ?? PlanningBusinessProjection.Build(state, global);
        if (reconsideration is not null)
            context["interpretationReconsideration"] = new JsonObject { ["priorReport"] = reconsideration["priorReport"]?.DeepClone(),
                ["reason"] = reconsideration["reason"]?.DeepClone(),
                ["instruction"] = "Reconsider this report against unchanged original facts; do not require an artificial plan edit." };
        if (PlanningConstruction.DefectProgressContext(state, context) is { } concerns)
            context["priorConcerns"] = new JsonArray(concerns.OfType<JsonObject>().Select(concern => (JsonNode)new JsonObject
            {
                ["reason"] = concern["explanation"]?.DeepClone(), ["before"] = concern["before"]?["value"]?.DeepClone(),
                ["current"] = concern["current"]?["value"]?.DeepClone()
            }).ToArray());
        var purpose = global ? PlanningConstruction.GlobalPurpose : PlanningConstruction.LocalPurpose;
        return (context, PlanningCompactConstruction.Request(BusinessReviewInstructions, context, ConstructionReviewSchema(context), purpose));
    }

    private static async Task ReviewConstructionAsync(PlanningSession state, IPlanningRuntime runtime, bool global, CancellationToken ct)
    {
        var construction = state.Construction!; var candidate = construction.Candidate!;
        var purpose = global ? PlanningConstruction.GlobalPurpose : PlanningConstruction.LocalPurpose;
        state.Phase = purpose;
        LLMRequest request;
        if (state.PendingCall is null)
        {
            (construction.PendingReviewContext, request) = CreateConstructionReviewRequest(state, global);
            construction.PendingReviewHash = BusinessReviewFingerprint(state, construction.PendingReviewContext, global);
        }
        else request = state.PendingCall.Request;
        var context = construction.PendingReviewContext ?? throw new PlanningConflictException("The issued business review lost its frozen context.");
        var fingerprint = BusinessReviewFingerprint(state, context, global);
        if (construction.PendingReviewHash != fingerprint || candidate.BaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The issued business review no longer matches its candidate, originals, contracts or claims.");
        JsonNode report;
        PlanningModelCall? receivedCall = null;
        JsonNode? rawReport = null;
        try
        {
            report = await PlanningModelCalls.CallAsync(state, runtime, purpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), ct,
                construction.Limits.ReviewOutputTokens, issued: () =>
                {
                    request = state.PendingCall!.Request;
                    if (PendingBusinessReconsideration(state) is { } entry)
                    { entry["outcome"] = "reconsideration_issued"; entry["requestId"] = state.PendingCall.Id; }
                }, received: (call, raw) => { receivedCall = call; rawReport = raw; });
        }
        catch (PlanningResponseException error)
        {
            if (error.ReceivedReport is { } received) candidate.Reviews.Add(ReviewRecord(state, context, request, received, global, "invalid_report", receivedCall, rawReport));
            Incomplete("The received business report did not satisfy its issued verdict schema."); return;
        }
        catch (JsonException) { Incomplete("The received business report was not valid JSON."); return; }
        catch (WorkflowRuntimeException error) when (state.PendingCall is null && error.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT" or "LLM_BUDGET_EXCEEDED")
        { Incomplete(error.Code + ": " + error.Message); return; }
        var decision = report["decision"]!.AsObject();
        var verdict = decision["verdict"]!.ToString();
        var row = ReviewRecord(state, context, request, report, global, verdict, receivedCall, rawReport);
        candidate.Reviews.Add(row);
        if (verdict == "needs_context")
        {
            if (decision["contextRequests"] is JsonArray { Count: 0 })
            { Incomplete(decision["reason"]!.ToString()); return; }
            try
            {
                PlanningConstruction.LoadEvidence(state, decision["contextRequests"]!.AsArray().Select(address => address!.ToString()).ToList(),
                    followup: probe =>
                    {
                        var next = CreateConstructionReviewRequest(probe, global).Request;
                        return PlanningModelCalls.PrepareRequest(probe, purpose, next.Prompt, next.StructuredOutputSchema!.AsObject(),
                            probe.Construction!.Limits.ReviewOutputTokens);
                    }, purpose: purpose);
            }
            catch (PlanningResponseException error)
            { Incomplete(string.Join("; ", error.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))); return; }
            construction.PendingReviewContext = null; construction.PendingReviewHash = null;
            return; // Context retrieval consumes calls, never correction or reinterpretation authority.
        }
        if (verdict == "needs_change")
        {
            var findings = BusinessFindings(context, decision);
            if (findings is null)
            { row["outcome"] = "unsupported_report"; Incomplete("A defect must cite original obligations and concrete implementation IDs."); return; }
            if (PlanningConstruction.DefectWitness(state, context, findings) is { } witness) row["defectWitness"] = witness;
            var targets = decision["targets"]!.AsArray().Select(target => target!.ToString()).ToArray();
            AuthorizePendingBusinessRepair(state, row, targets);
            var permitted = targets.All(target => candidate.TaskIds.Contains(target, StringComparer.Ordinal) ||
                target == candidate.Extension.Scope || target.StartsWith("output:", StringComparison.Ordinal) && candidate.Extension.Outputs.Any(output => "output:" + output.Name == target) ||
                candidate.AuthorizedPaths?.Any(path => path == "/tasks/" + target || path.StartsWith("/tasks/" + target + "/", StringComparison.Ordinal)) == true);
            candidate.Diagnostics = targets.Select(target => new PlanningDiagnostic(permitted ? "CONSTRUCTION_BUSINESS_REVIEW" : "BUSINESS_REVIEW_REQUIRED",
                candidate.TaskIds.Contains(target, StringComparer.Ordinal) ? "/tasks/" + target : "/construction/candidate",
                decision["reason"]!.ToString(), ValidationStage: purpose, Rule: "business_fulfillment")).ToList();
            state.Diagnostics = candidate.Diagnostics.ToList();
            PlanningConstruction.RecordRejection(state, candidate);
            construction.PendingReviewContext = null; construction.PendingReviewHash = null;
            if (!permitted && candidate.Reviews.Any(review => review["outcome"]?.ToString() == "reconsideration_issued" &&
                review["candidateIdentity"]?.ToString() == PlanningConstruction.FailureIdentity(state, candidate))) Stop(state);
            return;
        }
        if (verdict != "ok") { Incomplete("The business report supplied no supported verdict."); return; }
        row["resolvedWitnesses"] = new JsonArray(candidate.Reviews.Where(review => review["defectWitness"] is JsonObject)
            .SelectMany(review => review["defectWitness"]!["findings"]!.AsArray()).Select(finding => finding!["id"]!.DeepClone()).ToArray());
        candidate.BusinessReviewed = true;
        var covered = (context["fragment"] as JsonArray ?? []).Select(id => id!.ToString()).ToHashSet(StringComparer.Ordinal);
        var completedRequirements = PlanningCompactConstruction.CandidateCompletionClaims(state).ToHashSet(StringComparer.Ordinal);
        foreach (var checkpoint in construction.Checkpoints.Where(checkpoint => checkpoint.Fragment["businessReviewPending"]?.GetValue<bool>() == true))
        {
            var scope = checkpoint.Step?.Scope;
            var boundaryCovered = scope is not null && candidate.Extension.CloseScopes.Any(boundary =>
                Within(candidate.Plan, scope, boundary));
            if (global || checkpoint.TaskIds.Count > 0 && checkpoint.TaskIds.All(covered.Contains) &&
                (boundaryCovered || checkpoint.RequirementIds.Any(completedRequirements.Contains) ||
                    candidate.Extension.ContinueInScope is null && checkpoint.Step?.Id == construction.Compact!.CurrentStep!.Id))
                checkpoint.Fragment["businessReviewPending"] = false;
        }
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        await AcceptConstructionAsync(state, runtime, ct);

        void Incomplete(string reason)
        {
            candidate.BusinessReviewed = false;
            candidate.Diagnostics = [new("CONSTRUCTION_REVIEW_INCONCLUSIVE", "/construction/candidate", reason, ValidationStage: purpose)];
            state.Diagnostics = candidate.Diagnostics.ToList();
            construction.PendingReviewContext = null; construction.PendingReviewHash = null;
            Stop(state);
        }
    }

    internal static bool TryPrepareBusinessReviewReconsideration(PlanningSession state, IReadOnlyList<string> addresses)
    {
        var candidate = state.Construction?.Candidate;
        if (candidate is null || state.PendingCall is not null || addresses.Count is < 1 or > 8) return false;
        var prior = ReconsiderationSource(state);
        if (prior?["context"] is not JsonObject context) return false;
        var supported = BusinessReviewReconsiderationEvidence(state).ToHashSet(StringComparer.Ordinal);
        if (addresses.Any(address => !supported.Contains(address)) || addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Count) return false;
        var identity = PlanningConstruction.FailureIdentity(state, candidate);
        candidate.Reviews.Add(new JsonObject { ["outcome"] = "reconsideration_ready", ["candidateIdentity"] = identity,
            ["context"] = context.DeepClone(), ["reason"] = "Reconsider the retained finding using the supplied exact original evidence.",
            ["priorReport"] = prior["response"]?.DeepClone() });
        candidate.Diagnostics.Clear(); state.Diagnostics.Clear(); candidate.BusinessReviewed = false;
        state.Construction!.PendingReviewContext = null; state.Construction.PendingReviewHash = null;
        state.Status = PlanningStatus.Generating;
        return true;
    }

    internal static string[] BusinessReviewReconsiderationEvidence(PlanningSession state)
    {
        var prior = ReconsiderationSource(state);
        if (prior?["context"] is not JsonObject context) return [];
        var decision = prior["response"]?["decision"];
        var relevant = (decision?["obligations"] as JsonArray ?? []).SelectMany(item => new[] { "requirement:" + item, "source:" + item })
            .Concat((decision?["targets"] as JsonArray ?? []).SelectMany(item => new[] { "task:" + item, "scope:" + item, item!.ToString() }))
            .ToHashSet(StringComparer.Ordinal);
        return BusinessEvidenceAddresses(context).Concat((context["evidence"] as JsonObject ?? []).Select(pair => pair.Key))
            .Where(relevant.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToArray();
    }

    private static JsonObject? ReconsiderationSource(PlanningSession state)
    {
        if (state.Construction?.Candidate is not { } candidate || state.PendingCall is not null || candidate.Hash != PlanningConstruction.Hash(candidate.Plan)) return null;
        var identity = PlanningConstruction.FailureIdentity(state, candidate);
        if (candidate.Reviews.Any(row => row["candidateIdentity"]?.ToString() == identity &&
            row["outcome"]?.ToString() is "reconsideration_ready" or "reconsideration_issued")) return null;
        var prior = candidate.Reviews.LastOrDefault(row => row["candidateIdentity"]?.ToString() == identity &&
            row["outcome"]?.ToString() is "needs_change" or "unsupported_report");
        return prior?["claimsHash"]?.ToString() == ConstructionClaimsHash(state) && prior["candidateHash"]?.ToString() == candidate.Hash &&
            BusinessSourcesAndContractsValid(state, prior) ? prior : null;
    }

    internal static bool HasPendingBusinessReconsideration(PlanningSession state) => PendingBusinessReconsideration(state) is not null;
    private static JsonObject? PendingBusinessReconsideration(PlanningSession state) => state.Construction?.Candidate is { } candidate
        ? candidate.Reviews.LastOrDefault(row => row["outcome"]?.ToString() == "reconsideration_ready" &&
            row["candidateIdentity"]?.ToString() == PlanningConstruction.FailureIdentity(state, candidate)) : null;

    private static JsonObject ReviewRecord(PlanningSession state, JsonObject context, LLMRequest request, JsonNode report, bool global, string outcome,
        PlanningModelCall? call, JsonNode? rawReport) => new()
    {
        ["purpose"] = global ? PlanningConstruction.GlobalPurpose : PlanningConstruction.LocalPurpose,
        ["candidateHash"] = state.Construction!.Candidate!.Hash, ["baselineHash"] = state.Construction.Candidate.BaseHash,
        ["candidateIdentity"] = PlanningConstruction.FailureIdentity(state, state.Construction.Candidate),
        ["fingerprint"] = BusinessReviewFingerprint(state, context, global), ["claimsHash"] = ConstructionClaimsHash(state),
        ["claims"] = BusinessClaims(state.Construction.Compact!.CurrentStep!, state.Construction.Candidate.Extension),
        ["sources"] = BusinessSources(state), ["contracts"] = BusinessContracts(state), ["consumers"] = BusinessConsumers(state),
        ["policyHash"] = BusinessPolicyHash(state), ["intentHash"] = BusinessIntentHash(state),
        ["reviewedTaskIds"] = new JsonArray((context["actions"] as JsonArray ?? []).Select(action => action!["id"]!.DeepClone()).ToArray()),
        ["openScopes"] = Strings(state.Construction.Candidate.OpenScopes),
        ["requestId"] = request.ClientRequestId, ["requestHash"] = Hash(request, PlanningJsonContext.Default.LLMRequest),
        ["responseHash"] = PlanningGraphCompiler.Fingerprint((rawReport ?? report).ToJsonString()), ["outcome"] = outcome,
        ["response"] = report.DeepClone(), ["rawResponse"] = (rawReport ?? report).DeepClone(), ["context"] = context.DeepClone(),
        ["evidenceAddresses"] = call?.EvidenceAddresses is { } addresses
            ? JsonSerializer.SerializeToNode(addresses, PlanningJsonContext.Default.PlanningEvidenceAddresses) : null
    };

    internal static string ConstructionClaimsHash(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        BusinessClaims(state.Construction!.Compact!.CurrentStep!, state.Construction.Candidate!.Extension).ToJsonString());
    private static JsonObject BusinessClaims(PlanningConstructionStep step, PlanningConstructionExtension extension)
    {
        var claims = new JsonObject
        {
            ["scope"] = extension.Scope, ["requirements"] = Strings(extension.RequirementIds),
            ["closedRequirements"] = Strings(extension.ContinueInScope is null ? step.CloseRequirementIds : []),
            ["closedScopes"] = Strings(extension.CloseScopes), ["complete"] = extension.Complete,
            ["step"] = BusinessStep(step),
            ["continuationBinding"] = PlanningConstruction.Node(extension.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding),
            ["boundaryTransitions"] = PlanningConstruction.Node(extension.BoundaryTransitions, PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition)
        };
        if (extension.ContinueInScope is not null) claims["continueInScope"] = extension.ContinueInScope;
        return claims;
    }
    private static JsonObject BusinessStep(PlanningConstructionStep step)
    {
        var snapshot = PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!.AsObject();
        // The extension is the sole claim authority during review. Acceptance
        // copies these fields into Step for checkpoint navigation, not a new claim.
        snapshot.Remove("continuationBinding"); snapshot.Remove("boundaryTransitions");
        return snapshot;
    }

    internal static bool ConstructionBusinessReviewValid(PlanningSession state, PlanningConstructionCandidate candidate, bool global) =>
        candidate.BusinessReviewed && candidate.Hash == PlanningConstruction.Hash(candidate.Plan) && candidate.BaseHash == PlanningConstruction.Hash(state.Plan) &&
        candidate.Reviews.Any(row => JsonNode.DeepEquals(row["openScopes"], Strings(candidate.OpenScopes)) &&
            ReviewValid(state, row, candidate.Hash, candidate.BaseHash, ConstructionClaimsHash(state), global));
    internal static JsonObject? ReviewedCheckpoint(PlanningSession state, PlanningConstructionCheckpoint checkpoint)
    {
        if (checkpoint.Step is not { } step || checkpoint.InvalidatedTaskIds.Count != 0) return null;
        var extension = new PlanningConstructionExtension { Scope = step.Scope, RequirementIds = checkpoint.RequirementIds,
            CloseScopes = checkpoint.ClosedScopes, Complete = checkpoint.Fragment["complete"]?.GetValue<bool>() == true,
            ContinuationBinding = step.ContinuationBinding, BoundaryTransitions = step.BoundaryTransitions };
        var claims = PlanningGraphCompiler.Fingerprint(BusinessClaims(step, extension).ToJsonString());
        return checkpoint.Reviews.LastOrDefault(row => ReviewValid(state, row, checkpoint.PlanHash, checkpoint.BaseHash, claims, global: false));
    }
    internal static bool ConstructionArtifactReviewValid(PlanningSession state) => state.Construction?.Checkpoints.LastOrDefault() is { } checkpoint &&
        checkpoint.PlanHash == PlanningConstruction.Hash(state.Plan) && ReviewedCheckpoint(state, checkpoint) is { } row &&
        row["purpose"]?.ToString() == PlanningConstruction.GlobalPurpose;
    private static bool ReviewValid(PlanningSession state, JsonObject row, string candidate, string baseline, string claims, bool global) =>
        row["candidateHash"]?.ToString() == candidate && row["baselineHash"]?.ToString() == baseline && row["claimsHash"]?.ToString() == claims && row["outcome"]?.ToString() == "ok" &&
        row["purpose"]?.ToString() is { } purpose && (purpose == PlanningConstruction.GlobalPurpose || !global && purpose == PlanningConstruction.LocalPurpose) &&
        row["rawResponse"]?["decision"]?["verdict"]?.ToString() == "ok" && PlanningModelCalls.ReviewEvidenceBindingValid(row) &&
        row["responseHash"]?.ToString() == PlanningGraphCompiler.Fingerprint(row["rawResponse"]!.ToJsonString()) &&
        row["claimsHash"]?.ToString() == PlanningGraphCompiler.Fingerprint(row["claims"]!.ToJsonString()) &&
        row["context"] is JsonObject context && row["fingerprint"]?.ToString() == RowFingerprint(row, context) &&
        new[] { "fingerprint", "requestHash", "responseHash" }.All(key => IsHash(row[key]?.ToString())) &&
        !string.IsNullOrEmpty(row["requestId"]?.ToString()) && state.RequestMeasurements?.Any(measurement => measurement.Admission == "admitted" &&
            measurement.RequestHash == row["requestHash"]?.ToString() && measurement.RequestId == row["requestId"]?.ToString() && measurement.Phase == purpose) == true &&
        BusinessSourcesAndContractsValid(state, row);
    private static bool BusinessSourcesAndContractsValid(PlanningSession state, JsonObject row)
    {
        if (row["policyHash"]?.ToString() != BusinessPolicyHash(state) || row["intentHash"]?.ToString() != BusinessIntentHash(state) ||
            !JsonNode.DeepEquals(row["sources"], BusinessSources(state)) || row["contracts"] is not JsonArray contracts ||
            row["consumers"] is not JsonArray consumers || consumers.OfType<JsonObject>().Count() != consumers.Count) return false;
        foreach (var consumer in consumers.OfType<JsonObject>())
        {
            var item = state.Construction!.Compact!.SuspendedSteps?.SingleOrDefault(item => item.Id == consumer["id"]?.ToString());
            if (item is null) return false;
            var current = PlanningCompactConstruction.ContinuationConsumerHash(item);
            if (consumer["hash"]?.ToString() == current) continue;
            var plan = state.Construction.Candidate?.Plan ?? state.Plan;
            if (consumer["resumedHash"]?.ToString() != current || consumer["producer"]?.ToString() is not { } producer ||
                plan is null ||
                !new TaskPlanSymbols(plan).Tasks.TryGetValue(producer, out var task) ||
                consumer["producerHash"]?.ToString() != Hash(task.Task, PlanningJsonContext.Default.PlanTask)) return false;
        }
        var available = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).ToArray();
        return contracts.OfType<JsonObject>().Count() == contracts.Count && contracts.OfType<JsonObject>().All(contract =>
        {
            var exact = available.Where(capability => capability.Id == contract["id"]?.ToString() && capability.Version == contract["version"]?.ToString())
                .Select(capability => Hash(capability, PlanningJsonContext.Default.PlanningCapability)).Distinct(StringComparer.Ordinal).ToArray();
            return exact.Length == 1 && exact[0] == contract["hash"]?.ToString();
        });
    }
    private static JsonArray BusinessSources(PlanningSession state) => new(state.Construction!.Compact!.Sources.OrderBy(source => source.Id, StringComparer.Ordinal)
        .Select(source => (JsonNode)new JsonObject { ["id"] = source.Id, ["version"] = source.Version, ["kind"] = source.Kind,
            ["continuationOf"] = source.ContinuationOf, ["inputs"] = new JsonArray((source.Inputs ?? []).Select(input => (JsonNode?)JsonValue.Create(input.Name)).ToArray()),
            ["outputs"] = new JsonArray((source.Outputs ?? []).Select(output => (JsonNode?)JsonValue.Create(output.Name)).ToArray()), ["hash"] = PlanningGraphCompiler.Fingerprint(source.Text) }).ToArray());
    private static string BusinessIntentHash(PlanningSession state) => PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["request"] = state.Request.Prompt,
        ["inputs"] = JsonSerializer.SerializeToNode(state.Requirements?.Inputs, PlanningJsonContext.Default.ListTaskInput),
        ["outputs"] = JsonSerializer.SerializeToNode(state.Requirements?.Outputs, PlanningJsonContext.Default.ListTaskInput),
        ["requirements"] = new JsonArray((state.Requirements?.Outcomes ?? []).Select(requirement => (JsonNode)new JsonObject
            { ["id"] = requirement.Id, ["original"] = requirement.Description }).ToArray()),
        ["sources"] = new JsonObject(state.Construction!.Compact!.RequirementSources.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Strings(pair.Value)))),
        ["globals"] = Strings(state.Construction.Compact.GlobalSourceIds)
    }.ToJsonString());
    private static string BusinessPolicyHash(PlanningSession state) => PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["request"] = JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog?.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["allowedStepTypes"] = Strings(state.Catalog?.AllowedStepTypes ?? [])
    }.ToJsonString());
    private static JsonArray BusinessConsumers(PlanningSession state)
    {
        var compact = state.Construction!.Compact!;
        var association = state.Construction.Candidate!.Extension.ContinuationBinding;
        var ids = (association?.DischargedContinuationIds ?? []).Append(association?.ContinuationId).Append(compact.ResumingStepId)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new JsonArray((compact.SuspendedSteps ?? []).Where(item => ids.Contains(item.Id)).Select(item =>
        {
            var row = new JsonObject { ["id"] = item.Id, ["hash"] = PlanningCompactConstruction.ContinuationConsumerHash(item) };
            if (association?.ContinuationId != item.Id) return (JsonNode)row;
            var leaf = association.Binding;
            while (leaf.Kind == "field" && leaf.Items.Count == 1) leaf = leaf.Items[0];
            if (leaf.Source is not { } producer || !new TaskPlanSymbols(state.Construction.Candidate.Plan).Tasks.TryGetValue(producer, out var task)) return row;
            var after = PlanningConstruction.Node(item, PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!
                .Deserialize(PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!;
            var producerHash = Hash(task.Task, PlanningJsonContext.Default.PlanTask);
            after.DependencyHashes[producer] = producerHash;
            row["producer"] = producer; row["producerHash"] = producerHash;
            row["resumedHash"] = PlanningCompactConstruction.ContinuationConsumerHash(after);
            return row;
        }).ToArray());
    }
    private static JsonArray BusinessContracts(PlanningSession state)
    {
        var operations = TaskPlanRevisions.Tasks(state.Construction!.Candidate!.Plan).Select(task => task.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new((state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).Where(capability => operations.Contains(TaskOperations.Describe(capability).Id))
            .DistinctBy(capability => (capability.Id, capability.Version)).OrderBy(capability => capability.Id, StringComparer.Ordinal).ThenBy(capability => capability.Version, StringComparer.Ordinal)
            .Select(capability => (JsonNode)new JsonObject { ["id"] = capability.Id, ["version"] = capability.Version,
                ["hash"] = Hash(capability, PlanningJsonContext.Default.PlanningCapability) }).ToArray());
    }
    private static string BusinessReviewFingerprint(PlanningSession state, JsonObject context, bool global) => PlanningGraphCompiler.Fingerprint(
        state.Construction!.Candidate!.BaseHash + "\n" + state.Construction.Candidate.Hash + "\n" + ConstructionClaimsHash(state) + "\n" +
        (global ? "global" : "local") + "\n" + BusinessSources(state).ToJsonString() + "\n" + BusinessContracts(state).ToJsonString() + "\n" +
        BusinessConsumers(state).ToJsonString() + "\n" + BusinessPolicyHash(state) + "\n" + BusinessIntentHash(state) + "\n" + context.ToJsonString());
    private static string RowFingerprint(JsonObject row, JsonObject context) => PlanningGraphCompiler.Fingerprint(
        row["baselineHash"] + "\n" + row["candidateHash"] + "\n" + row["claimsHash"] + "\n" +
        (row["purpose"]?.ToString() == PlanningConstruction.GlobalPurpose ? "global" : "local") + "\n" +
        row["sources"]!.ToJsonString() + "\n" + row["contracts"]!.ToJsonString() + "\n" + row["consumers"]!.ToJsonString() + "\n" + row["policyHash"] + "\n" + row["intentHash"] + "\n" + context.ToJsonString());
    private static string[] BusinessEvidenceAddresses(JsonObject context) => PlanningCompactConstruction.NavigationEvidence(
        new[] { "requirements", "globalConstraints", "actions", "scopes", "groups", "inputs", "publicOutputs", "continuation" }
            .SelectMany(key => context[key] is JsonArray array ? array.Cast<JsonNode?>() : context[key] is JsonObject value ? new JsonNode?[] { value } : [])
            .Concat((context["actions"] as JsonArray ?? []).Select(action => action?["contract"]))
            .Concat((context["continuation"]?["consumer"]?["prerequisite"]?["evidence"] as JsonArray ?? []).Cast<JsonNode?>())
            .Concat((context["continuation"]?["descendants"] as JsonArray ?? []).SelectMany(descendant =>
                (descendant?["consumer"]?["prerequisite"]?["evidence"] as JsonArray ?? []).Cast<JsonNode?>()))
            .Concat((context["evidence"] as JsonObject ?? []).Select(pair => pair.Value)))
        .Except((context["evidence"] as JsonObject ?? []).Select(pair => pair.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static (string[] Obligations, string[] Targets) BusinessFindingDomains(JsonObject context)
    {
        var obligations = new[] { "requirements", "globalConstraints" }
            .SelectMany(key => (context[key] as JsonArray ?? []).Select(item => item?["id"]?.ToString()))
            .OfType<string>().Where(id => OriginalPointer(context, id) is not null).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var targets = new[] { "actions", "scopes" }
            .SelectMany(key => (context[key] as JsonArray ?? []).Select(item => item?["id"]?.ToString()))
            .Concat((context["groups"] as JsonArray ?? []).Select(item => item?["id"] is { } id ? "group:" + id : null))
            .Concat((context["publicOutputs"] as JsonArray ?? []).Select(item => item?["name"] is { } name ? "output:" + name : null))
            .OfType<string>().Where(id => ImplementationPointer(context, id) is not null).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return (obligations, targets);
    }

    private static JsonArray? BusinessFindings(JsonObject context, JsonObject decision)
    {
        var originals = decision["obligations"]!.AsArray().Select(item => OriginalPointer(context, item!.ToString())).ToArray();
        var targets = decision["targets"]!.AsArray().Select(item => ImplementationPointer(context, item!.ToString())).ToArray();
        if (originals.Any(pointer => pointer is null) || targets.Any(pointer => pointer is null)) return null;
        return new JsonArray(targets.Select(pointer => (JsonNode)new JsonObject { ["kind"] = "business_fulfillment", ["location"] = pointer,
            ["evidence"] = new JsonArray(originals[0], pointer), ["explanation"] = decision["reason"]!.DeepClone(), ["uncertainty"] = "" }).ToArray());
    }
    private static string? OriginalPointer(JsonObject context, string id) => Find(context, "requirements", id) ?? Find(context, "globalConstraints", id);
    private static string? ImplementationPointer(JsonObject context, string id) => Find(context, "actions", id) ?? Find(context, "scopes", id) ??
        (id.StartsWith("group:", StringComparison.Ordinal) ? Find(context, "groups", id[6..]) : null) ??
        (id.StartsWith("output:", StringComparison.Ordinal) ? Find(context, "publicOutputs", id[7..], "name") : null);
    private static string? Find(JsonObject context, string collection, string id, string field = "id")
    {
        var items = (context[collection] as JsonArray ?? []).Select((item, index) => (item, index)).Where(pair => pair.item?[field]?.ToString() == id).ToArray();
        return items.Length == 1 ? "/" + collection + "/" + items[0].index : null;
    }
    internal static bool ConstructionBusinessFindingSupported(JsonObject context, JsonObject finding) =>
        finding["evidence"] is JsonArray { Count: 2 } evidence && evidence[0]?.ToString() is { } original &&
        (original.StartsWith("/requirements/", StringComparison.Ordinal) || original.StartsWith("/globalConstraints/", StringComparison.Ordinal)) &&
        evidence[1]?.ToString() is { } implementation && new[] { "/actions/", "/scopes/", "/groups/", "/publicOutputs/" }.Any(prefix => implementation.StartsWith(prefix, StringComparison.Ordinal)) &&
        PlanningConstruction.TryPointer(context, original, out _) && PlanningConstruction.TryPointer(context, implementation, out _);
    private static string Hash<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(value, type));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Within(TaskPlan plan, string scope, string boundary)
    {
        for (var current = new TaskPlanSymbols(plan).Scopes.SingleOrDefault(candidate => candidate.Path == scope); current is not null; current = current.Parent)
            if (current.Path == boundary) return true;
        return false;
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
