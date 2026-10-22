using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static async Task AdvanceCompactConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!;
        if (construction.Compact is null) throw new PlanningConflictException("The captured compact construction state is missing.");
        if (!PlanningCompactConstruction.CapturedPresentationValid(state))
            throw new PlanningConflictException("The captured compact presentation protocol is unsupported or inconsistent.");
        state.Status = PlanningStatus.Generating;
        if (construction.Compact.CurrentStep?.PendingDiscovery is not null)
        {
            await ContinueConstructionDiscoveryAsync(state, runtime, ct);
            return;
        }
        if (construction.Complete) { await CompileAsync(state, runtime, ct); return; }
        PlanningCompactConstruction.InitializeSources(state);
        var pendingExtraction = PlanningCompactConstruction.IsCompactPresentation(state) &&
            state.PendingCall?.Purpose == PlanningCompactConstruction.IntentPurpose &&
            state.PendingCall.Request.StructuredOutputSchema?["properties"]?["clauses"] is null;
        if (!pendingExtraction && (state.PendingCall is null || state.PendingCall.Purpose == PlanningCompactConstruction.IntentPurpose))
            if (construction.Compact.Sources.FirstOrDefault(s => s.Kind is "page" or "answer_page" or "revision_page" && !s.Indexed) is { } sourcePage)
            { await IndexConstructionIntentAsync(state, runtime, sourcePage, ct); return; }
        if (PlanningCompactConstruction.IsCompactPresentation(state) &&
            construction.Compact.InterfaceExtractions?.FirstOrDefault(e => !e.Complete) is { } extraction)
        { await ExtractConstructionInterfacesAsync(state, runtime, extraction, ct); return; }
        if (!PlanningCompactConstruction.IntentDeclarationsComplete(construction.Compact))
            throw new PlanningConflictException("Complete the captured original interface declarations before generation or review.");
        if (construction.Candidate is { } candidate)
        {
            if (candidate.BaseHash != PlanningConstruction.Hash(state.Plan)) throw new PlanningConflictException("The candidate baseline changed.");
            if (candidate.Diagnostics.Count > 0) { await CorrectConstructionAsync(state, runtime, ct); return; }
            if (!candidate.LocalReviewed || candidate.NeedsIntermediateReview && !candidate.IntermediateReviewed)
            { await ReviewConstructionAsync(state, runtime, candidate.LocalReviewed, ct); return; }
            await AcceptConstructionAsync(state, runtime, ct); return;
        }
        if (construction.Checkpoints.Count >= construction.Limits.MaxCheckpoints)
        {
            state.Diagnostics = [new("CONSTRUCTION_CHECKPOINT_LIMIT", "/construction", "The captured checkpoint allowance is exhausted.")];
            Stop(state); return;
        }
        var compact = construction.Compact;
        if (state.Requirements is null || state.Requirements.Outcomes.Count == 0)
            throw new WorkflowRuntimeException("CONSTRUCTION_INTENT_EMPTY", "The retained source pages contain no explicit business requirement.");
        if (state.Catalog is null)
        {
            state.Catalog = await runtime.DiscoverAsync(state.Request, ct);
            state.Discovery.Sources = (await runtime.Capabilities.ListSourcesAsync(ct)).ToList();
        }
        if (compact.CurrentStep is null) { await SelectConstructionStepAsync(state, runtime, ct); return; }
        if (compact.CurrentStep.BaseHash != PlanningConstruction.Hash(state.Plan)) throw new PlanningConflictException("The selected step has a stale baseline.");
        if (!compact.CurrentStep.DiscoveryComplete) { await DiscoverConstructionStepAsync(state, runtime, ct); return; }
        var repair = state.PendingCall is { } pending ? pending.Purpose == "replan" : state.Diagnostics.Any(d => d.Required);
        if (repair && state.PendingCall is null && (construction.DraftCorrections >= construction.Limits.MaxCorrectionsPerDraft || PlanningModelCalls.RemainingRepairs(state) == 0))
        { Stop(state); return; }
        await GenerateConstructionAsync(state, runtime, repair, ct);
    }

    private static async Task ReserveCompactContinuationAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.PendingCall is not null) return;
        // The current navigation call cannot consume the draft and its mandatory reviews.
        var construction = state.Construction!;
        var reviews = construction.Checkpoints.Count > 0 ? 2 : 1;
        if (PlanningModelCalls.RemainingCalls(state) < reviews + 2)
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "The remaining allowance cannot admit this navigation call, a draft and mandatory reviews.");
        var input = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningConstruction.DraftPurpose);
        var outputs = new[] { PlanningModelCalls.ConstructionOutputLimit(state, PlanningCompactConstruction.SelectPurpose),
            PlanningModelCalls.ConstructionOutputLimit(state, PlanningConstruction.DraftPurpose) }
            .Concat(Enumerable.Repeat(PlanningModelCalls.ConstructionOutputLimit(state, PlanningConstruction.LocalPurpose), reviews)).ToArray();
        if (PlanningBudgetOptions.Parse(state.Request.Options)?.MaxTotalTokens is { } tokens &&
            tokens - (state.Usage?.TotalTokens ?? 0) < (long)input * outputs.Length + outputs.Sum())
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "The remaining cumulative tokens cannot admit navigation and its mandatory continuation.");
        if (runtime is IPlanningConstructionAdmission admission)
        {
            var probe = PlanningConstructionPhasePolicies.Apply(new LLMRequest { MaxTokens = outputs[0],
                Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
                Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "" }, state.Request, PlanningCompactConstruction.SelectPurpose);
            await admission.AdmitConstructionAsync(probe, input, outputs, ct);
        }
    }

    private static async Task<JsonNode> CompactCallAsync(PlanningSession state, IPlanningRuntime runtime, string purpose,
        Func<LLMRequest> create, CancellationToken ct, PlanningCorrectionAttempt? correction = null)
    {
        using var ownedCorrection = correction is null ? new PlanningCorrectionAttempt(state, state.Diagnostics.Any(d => d.Required)) : null;
        correction ??= ownedCorrection!;
        if (state.PendingCall is null && state.Diagnostics.Any(d => d.Required))
        {
            if (PlanningModelCalls.RemainingRepairs(state) == 0 || state.Request.Options["automatic_correction"]?.ToString() == "false")
                throw new WorkflowRuntimeException("CONSTRUCTION_CORRECTION_ALLOWANCE", "The captured correction allowance cannot retry this rejected navigation action.");
            if (!correction.Enabled) state.ReplanAttempts++;
        }
        await ReserveCompactContinuationAsync(state, runtime, ct);
        state.Phase = purpose;
        var request = state.PendingCall?.Request ?? create();
        var response = await PlanningModelCalls.CallAsync(state, runtime, purpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), ct, state.Construction!.Limits.DraftOutputTokens, correction);
        var descriptionReadKey = purpose == PlanningCompactConstruction.DiscoveryPurpose
            ? PlanningCompactConstruction.DescriptionReadKey(state, request.StructuredOutputSchema!, response) : null;
        if (purpose == PlanningCompactConstruction.DiscoveryPurpose)
            PlanningCompactConstruction.RecordPresentedOperations(state, request.Prompt);
        var compact = PlanningCompactConstruction.State(state);
        var legacyHash = purpose + ":" + compact.CurrentStep?.Id + ":" + PlanningConstruction.Hash(state.Plan) + ":" + PlanningGraphCompiler.Fingerprint(response.ToJsonString());
        var ordinaryHash = CompactActionFingerprint(state, legacyHash);
        var hash = descriptionReadKey is null ? ordinaryHash : ordinaryHash + ":" + descriptionReadKey;
        if (compact.FailedActions.Contains(hash, StringComparer.Ordinal) || compact.FailedActions.Contains(ordinaryHash, StringComparer.Ordinal) ||
            compact.FailedActions.Contains(legacyHash, StringComparer.Ordinal))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated a rejected compact construction action.");
        // Retain the attempt before validation. Successful state transitions change phase/context.
        compact.FailedActions.Add(hash);
        if (purpose == PlanningCompactConstruction.DiscoveryPurpose)
            PlanningCompactConstruction.ValidateOperationPageAuthority(request.StructuredOutputSchema!, response);
        return response;
    }

    internal static string CompactActionFingerprint(PlanningSession state, string action)
    {
        // Reviewed requirement closure can advance without changing the TaskPlan.
        // Only accepted progress changes this identity, never another attempt,
        // a presentation cursor, diagnostics or the remaining allowance.
        var progress = new JsonObject
        {
            ["checkpoint"] = state.Construction!.Checkpoints.LastOrDefault()?.Sequence ?? 0,
            ["sources"] = new JsonArray(PlanningCompactConstruction.State(state).Sources
                .Where(s => s.Indexed && s.Kind is "page" or "answer_page" or "revision_page")
                .Select(s => (JsonNode?)JsonValue.Create(s.Id + "@" + s.Version)).ToArray())
        };
        return action + ":progress:" + PlanningGraphCompiler.Fingerprint(progress.ToJsonString());
    }

    private static async Task IndexConstructionIntentAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionSource source, CancellationToken ct)
    {
        if (PlanningCompactConstruction.IsCompactPresentation(state))
        { await ClassifyConstructionIntentAsync(state, runtime, source, ct); return; }
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.IntentPurpose, () =>
            PlanningCompactConstruction.Request(PlanningCompactConstruction.IndexInstructions, new JsonObject
            { ["sources"] = new JsonArray(new JsonObject { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text }),
                ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics) },
                PlanningCompactConstruction.IntentSchema(source), PlanningCompactConstruction.IntentPurpose), ct);
        var clauses = response["clauses"]!.AsArray();
        if (response["sourceId"]!.ToString() != source.Id || string.Concat(clauses.Select(c => c!["text"]!.ToString())) != source.Text)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_COVERAGE", "/clauses", "Verbatim clauses must cover the entire exact original source page, in order, without omissions or duplication.");
        var compact = PlanningCompactConstruction.State(state);
        var requirements = ReadConstructionInterfaces(state, response, source.Kind);
        if (clauses.Any(c => c!["kind"]!.ToString() == "requirement" && string.IsNullOrWhiteSpace(c["text"]!.ToString())))
            PlanningConstruction.Fail("REQUIREMENTS_INVALID", "/clauses", "Whitespace cannot be a business requirement; preserve it in an adjacent clause or global context.");
        state.Requirements = requirements;
        for (var i = 0; i < clauses.Count; i++)
        {
            var text = clauses[i]!["text"]!.ToString(); var kind = clauses[i]!["kind"]!.ToString();
            var id = source.Id + ":clause:" + i;
            compact.Sources.Add(new() { Id = id, Text = text, Kind = kind, Indexed = true, Version = PlanningGraphCompiler.Fingerprint(text) });
            if (kind == "global") compact.GlobalSourceIds.Add(id);
            else
            {
                var requirementId = "req_" + compact.RequirementSources.Count;
                state.Requirements.Outcomes.Add(new(requirementId, text));
                compact.RequirementSources.Add(requirementId, [id]);
                state.Construction!.Requirements.Add(new() { Id = requirementId });
            }
        }
        source.Indexed = true;
        state.Diagnostics.Clear();
        if (state.Construction!.Candidate is { } retainedCandidate)
        {
            retainedCandidate.LocalReviewed = false; retainedCandidate.IntermediateReviewed = false;
            await ValidateConstructionCandidateAsync(state, runtime, ct);
        }

    }

    private static PlanningRequirements ReadConstructionInterfaces(PlanningSession state, JsonNode response, string sourceKind)
    {
        var requirements = CloneConstructionRequirements(state);
        var inputs = response["inputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)!;
        var outputs = response["outputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)!;
        if (outputs.Any(o => o.Default is not null))
            PlanningConstruction.Fail("REQUIREMENTS_INVALID", "/outputs", "Public output declarations cannot provide defaults; the workflow must produce the requested result.");
        AddInterface(requirements.Inputs!, inputs, "/inputs");
        AddInterface(requirements.Outputs!, outputs, "/outputs");
        ValidateInterfaces(requirements.Inputs!, "/inputs");
        // Optional public outputs need no fallback; shared semantic validation still checks their exact types.
        ValidateInterfaces(requirements.Outputs!.Select(o => new TaskInput { Name = o.Name, Type = o.Type, Required = true, Default = o.Default }).ToList(), "/outputs");
        return requirements;

        void AddInterface(List<TaskInput> accepted, List<TaskInput> additions, string path)
        {
            foreach (var input in additions)
            {
                if (string.IsNullOrWhiteSpace(input.Name)) PlanningConstruction.Fail("REQUIREMENTS_INVALID", path, "Business interfaces require names.");
                var previous = accepted.SingleOrDefault(i => i.Name == input.Name);
                if (previous is not null && !PlanningClarifications.SameInputs([previous], [input]))
                    PlanningConstruction.Fail("REQUIREMENTS_CHANGED", path, "A later source page cannot rewrite an already accepted caller or public interface.");
                if (previous is null)
                {
                    var initialPublicInterface = path == "/outputs" && sourceKind == "page" && state.Construction!.Checkpoints.Count == 0;
                    if (state.Plan is not null && !initialPublicInterface)
                        PlanningConstruction.Fail("REQUIREMENTS_CHANGED", path, "Changing an accepted interface requires explicit revision.");
                    accepted.Add(input);
                }
            }
        }
        void ValidateInterfaces(List<TaskInput> interfaces, string path)
        {
            var findings = TaskPlanCompiler.ValidateConstruction(new() { Inputs = interfaces }, state.Catalog ?? new(), state.Request,
                new HashSet<string>(StringComparer.Ordinal) { "/root" }).Diagnostics;
            if (findings.Count > 0) throw new PlanningResponseException(findings.Select(d => d with
                { Location = d.Location.StartsWith("/inputs", StringComparison.Ordinal) ? path + d.Location[7..] : d.Location }).ToList());
        }
    }

    private static async Task SelectConstructionStepAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        using var correction = new PlanningCorrectionAttempt(state, state.Diagnostics.Any(d => d.Required));
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose, () =>
            PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state), PlanningCompactConstruction.SelectionContext(state),
                PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose), ct, correction);
        if (response["baseHash"]!.ToString() != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The step selection identifies a stale accepted checkpoint.");
        if (await CompactOtherActionAsync(state, runtime, response, "step", ct, correction: correction)) return;
        var compact = PlanningCompactConstruction.State(state);
        var step = response["step"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep)!;
        PlanningCompactConstruction.ValidateSelection(state, step);
        step.Id = "step_" + (++compact.NextStep);
        step.DiscoveryComplete = step.Kind is not null && step.Kind != "operation";
        compact.CurrentStep = step;
        if (step.Kind is null) await ReserveCompactContinuationAsync(state, runtime, ct);
        compact.PendingCommitments.Clear();
        PlanningCompactConstruction.RetainPrerequisiteEvidence(state);
        state.Diagnostics.Clear();
    }

    private static async Task DiscoverConstructionStepAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var compact = PlanningCompactConstruction.State(state); var step = compact.CurrentStep!;
        var issued = state.PendingCall?.Request;
        var exactRequest = issued;
        var correcting = state.Diagnostics.Any(d => d.Required);
        var primary = PlanningCompactConstruction.UsesDeferredBinding(state) ? "binding" : "operationIds";
        using var correction = new PlanningCorrectionAttempt(state, correcting);
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose, () =>
            exactRequest = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), PlanningCompactConstruction.DiscoveryContext(state),
                PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose), ct, correction);
        if (response["reselectStep"] is { } reason)
        {
            // Interruption diagnostics can be added after dispatch. Only the exact
            // issued context establishes whether this navigation call was charged
            // as a correction before its durable pending checkpoint.
            if (issued is not null)
            {
                var context = JsonNode.Parse(issued.Prompt[(issued.Prompt.LastIndexOf('\n') + 1)..]);
                correcting = context?["diagnostics"]?.AsArray().Any(d => d?["required"]?.GetValue<bool>() == true) == true;
            }
            if (new[] { primary, "evidenceRequests", "clarifications", "discoveryRequests" }.Any(name => response[name] is not null) ||
                !PlanningCompactConstruction.CanReselectDiscovery(state) || string.IsNullOrWhiteSpace(reason.ToString()) || reason.ToString().Length > 512)
                PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep", "Reselect only an inspected tentative operation step, as one bounded action within its retained correction authority.");
            if (!correcting && PlanningModelCalls.RemainingRepairs(state) == 0)
                PlanningConstruction.Fail("CONSTRUCTION_CORRECTION_ALLOWANCE", "/reselectStep", "The retained correction allowance cannot admit tentative-step reselection.");
            if (correction.Enabled) correction.Reselect();
            else
            {
                if (!correcting) state.ReplanAttempts++;
                state.Construction!.DraftCorrections++;
            }
            state.Diagnostics.Add(new("CONSTRUCTION_TENTATIVE_SELECTION", "/construction/compact/currentStep", reason.ToString()));
            PlanningCompactConstruction.Reselect(state, reason.ToString());
            // The rejected decision and its diagnostics remain evidence. This action consumed
            // one correction; the replacement selection must not charge the same action again.
            state.Diagnostics.Clear();
            return;
        }
        var descriptionReadKey = PlanningCompactConstruction.DescriptionReadKey(state, exactRequest!.StructuredOutputSchema!, response);
        if (await CompactOtherActionAsync(state, runtime, response, primary, ct, descriptionReadKey, correction, exactRequest.StructuredOutputSchema)) return;
        var binding = primary == "binding" ? response[primary]!.AsObject() : null;
        var operations = (binding?["operationIds"] ?? response["operationIds"])!.AsArray().Select(o => o!.ToString()).ToArray();
        var kind = binding?["kind"]?.ToString() ?? "operation";
        var groupId = binding?["groupId"]?.ToString();
        if (kind == "operation" ? operations.Length is < 1 or > 2 || groupId is not null : operations.Length != 0)
            PlanningConstruction.Fail("DISCOVERY_SELECTION_INVALID", "/binding", "Only an operation binding selects exact operations; local work must not bind operation identities.");
        foreach (var operation in operations)
        {
            var retained = state.Catalog!.Capabilities.Concat(state.Discovery.Resolved)
                .Where(c => TaskOperations.Describe(c).Id == operation).DistinctBy(c => (c.Id, c.Version)).ToArray();
            if (retained.Length == 1)
            {
                if (!state.Catalog.AllowedStepTypes.Contains(retained[0].StepType) || state.Catalog.Policy.DeniedCapabilityIds.Contains(retained[0].Id))
                    PlanningConstruction.Fail("DISCOVERY_SELECTION_INVALID", "/operationIds", "The selected operation is not policy-allowed.");
                continue;
            }
            var discovered = state.Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.Operation?.Id == operation)
                .DistinctBy(c => (c.Id, c.Version)).ToArray();
            if (discovered.Length != 1) PlanningConstruction.Fail("DISCOVERY_SELECTION_INVALID", "/operationIds", "Select an unambiguous discovered operation.");
            await PlanningDiscoveryContext.ResolveAsync(state, runtime, discovered[0], ct);
        }
        if (binding is not null)
        {
            var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            var selected = probe.Construction!.Compact!.CurrentStep!;
            selected.Kind = kind; selected.GroupId = groupId;
            selected.OperationIds = operations.Distinct(StringComparer.Ordinal).ToList(); selected.DiscoveryComplete = true;
            PlanningCompactConstruction.ValidateSelection(probe, selected);
            PlanningCompactConstruction.RetainPrerequisiteEvidence(probe);
            var draft = PlanningConstruction.DraftRequest(probe, repair: false);
            try
            {
                PlanningConstruction.AdmitEvidenceFollowup(state, PlanningModelCalls.PrepareRequest(probe, PlanningConstruction.DraftPurpose,
                    draft.Prompt, draft.StructuredOutputSchema!.AsObject(), probe.Construction.Limits.DraftOutputTokens), PlanningConstruction.DraftPurpose);
            }
            catch (PlanningResponseException ex) when (ex.Diagnostics.Any(d => d.Code == "CONSTRUCTION_EVIDENCE_LIMIT"))
            {
                throw new WorkflowRuntimeException("CONSTRUCTION_CONTRACT_LIMIT",
                    "The selected operations' mandatory original constraint closure and complete generation grammar exceed the captured input ceiling. No implementation binding was accepted.");
            }
            step.Kind = selected.Kind; step.GroupId = selected.GroupId;
        }
        step.OperationIds = operations.Distinct(StringComparer.Ordinal).ToList();
        // The independent selection decision precedes the draft being assessed.
        // These bindings stay tentative until that draft and its review are accepted.
        compact.PendingCommitments = step.RequirementIds.ToDictionary(id => id,
            _ => step.OperationIds.Select(operation => new PlanningCommitment(operation)).ToList(), StringComparer.Ordinal);
        step.DiscoveryComplete = true;
        PlanningCompactConstruction.RetainPrerequisiteEvidence(state);
        state.Diagnostics.Clear();
    }

    private static async Task<bool> CompactOtherActionAsync(PlanningSession state, IPlanningRuntime runtime,
        JsonNode response, string primary, CancellationToken ct, string? descriptionReadKey = null, PlanningCorrectionAttempt? correction = null,
        JsonNode? issuedDiscoverySchema = null)
    {
        var names = new[] { primary, "evidenceRequests", "clarifications", "discoveryRequests" };
        if (names.Count(name => response[name] is not null) != 1)
            PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return exactly one compact construction action.");
        if (response["evidenceRequests"] is JsonArray evidence)
        {
            var purpose = primary == "step" ? PlanningCompactConstruction.SelectPurpose : PlanningCompactConstruction.DiscoveryPurpose;
            PlanningConstruction.LoadEvidence(state, evidence.Select(e => e!.ToString()).ToList(), descriptionReadKey,
                followup: next =>
                {
                    var request = primary == "step"
                        ? PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(next), PlanningCompactConstruction.SelectionContext(next),
                            PlanningCompactConstruction.SelectionSchema(next), purpose)
                        : PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(next), PlanningCompactConstruction.DiscoveryContext(next),
                            PlanningCompactConstruction.DiscoverySchema(next), purpose);
                    return PlanningModelCalls.PrepareRequest(next, purpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), next.Construction!.Limits.DraftOutputTokens);
                }, purpose: purpose, issuedDiscoverySchema: issuedDiscoverySchema);
            if (PlanningCompactConstruction.IsCompactPresentation(state))
                state.Diagnostics.RemoveAll(d => d.Location == "/evidenceRequests" &&
                    d.Code is "CONSTRUCTION_EVIDENCE_UNKNOWN" or "CONSTRUCTION_EVIDENCE_INVALID" or "CONSTRUCTION_EVIDENCE_LIMIT");
            correction?.EvidenceAccepted();
            return true;
        }
        if (response["clarifications"] is JsonArray questions)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            return true;
        }
        if (response["discoveryRequests"] is JsonArray requests)
        {
            await ConstructionDiscoveryAsync(state, runtime, requests.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest)!, ct, correction);
            if (PlanningCompactConstruction.UsesDeferredBinding(state)) return true;
            if (PlanningConstructionPhasePolicies.IsFresh(state.Request))
            {
                // Provider receipts are retained even if the enlarged follow-up
                // cannot be admitted. Never reread a provider to retry admission.
                var purpose = PlanningCompactConstruction.DiscoveryPurpose;
                var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
                    PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state), purpose);
                PlanningConstruction.AdmitEvidenceFollowup(state,
                    PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt, request.StructuredOutputSchema!.AsObject(),
                        state.Construction!.Limits.DraftOutputTokens), purpose);
            }
            else state.Construction!.Evidence = null;
            if (!PlanningCompactConstruction.IsCompactPresentation(state)) state.Construction.EvidenceRequests.Clear();
            return true;
        }
        return false;
    }
}
