using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningConstructionResponse
{
    public string? BaseHash { get; set; }
    public PlanningRequirements? Requirements { get; set; }
    public PlanningConstructionExtension? Extension { get; set; }
    public List<PlanningDiscoveryRequest>? DiscoveryRequests { get; set; }
    public List<PlanningQuestion>? Clarifications { get; set; }
    public List<string>? EvidenceRequests { get; set; }
    public string? RollbackTask { get; set; }
    public string? RollbackReason { get; set; }
    public List<PlanningConstructionCommitment>? Commitments { get; set; }
    public string? ReselectStep { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanningConstructionResponse))]
internal partial class ConstructionJsonContext : JsonSerializerContext;

public sealed partial class HybridWorkflowPlanner
{
    private static void ReviseConstruction(PlanningSession state, PlanningCommand command)
    {
        var construction = state.Construction!;
        if (construction.Version == 2 && command.PreserveRequirements != true) construction.Compact = new()
        {
            PresentationVersion = construction.Compact?.PresentationVersion,
            InterfaceExtractions = PlanningCompactConstruction.IsCompactPresentation(state) ? [] : null
        };
        else if (construction.Version == 2 && construction.Compact is { } compactRevision)
        { compactRevision.CurrentStep = null; compactRevision.PendingCommitments.Clear(); }
        construction.Candidate = null; construction.CandidateFailureIdentityVersion = null;
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        construction.Evidence = null; construction.EvidenceRequests.Clear(); construction.Complete = false;
        state.PendingQuestions = null; state.ApprovedHash = null; state.Graph = null; Invalidate(state);
        state.Diagnostics.Clear(); state.RevisionScope.Clear(); state.EditablePaths = null;
        state.Status = PlanningStatus.Generating; state.Phase = PlanningConstruction.DraftPurpose;
        if (command.PreserveRequirements != true)
        {
            // Explicit intent changes start a new unapproved construction revision.
            // Original fragments, receipts and cumulative counters stay in this journal.
            state.Request.Baseline = state.Plan; state.Plan = null; state.Requirements = null;
            construction.Requirements.Clear(); construction.RebuildTasks.Clear(); construction.RebuildScopes.Clear(); construction.OpenScopes = ["/root"];
            return;
        }
        if (command.EditablePaths is { Count: > 0 } paths && state.Plan is { } accepted)
        {
            construction.Candidate = new()
            {
                BaseHash = PlanningConstruction.Hash(accepted), Hash = PlanningConstruction.Hash(accepted), Plan = PlanningConstruction.Clone(accepted),
                Extension = new() { Scope = "/root", Complete = construction.OpenScopes.Count == 0 },
                OpenScopes = construction.OpenScopes.ToList(), AuthorizedPaths = paths.ToList(),
                TaskIds = paths.Select(p => p.Split('/')).Where(p => p.Length > 2 && p[1] == "tasks").Select(p => p[2]).Distinct(StringComparer.Ordinal).ToList(),
                Diagnostics = paths.Select(p => new PlanningDiagnostic("JUDGE_SCOPED_REVIEW", p, "Apply the explicitly requested implementation revision within this exact path.")).ToList()
            };
            if (construction.Version == 2) PlanningCompactConstruction.RevisionStep(state, command.Text!);
            return;
        }
        if (!construction.OpenScopes.Contains("/root", StringComparer.Ordinal)) construction.OpenScopes.Add("/root");
    }

    private static async Task AdvanceConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!;
        var limits = construction.Limits;
        if (construction.Version is not (1 or 2) || limits.MaxTasksPerDraft is < 1 or > 32 || limits.MaxCheckpoints is < 1 or > 1024 ||
            limits.MaxCorrectionsPerDraft is < 0 or > 64 || limits.IntermediateReviewInterval is < 1 or > 128 ||
            limits.DraftOutputTokens is < 1 or > 65536 || limits.ReviewOutputTokens is < 1 or > 65536)
            throw new PlanningConflictException("The captured construction contract or limits are unsupported.");
        if (construction.CandidateFailureIdentityVersion is not (null or 1) ||
            construction.CandidateFailureIdentityVersion is not null && construction is not { Version: 2, Compact.PresentationVersion: 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 })
            throw new PlanningConflictException("The captured candidate failure identity is unsupported.");
        if (construction.Version == 2) { await AdvanceCompactConstructionAsync(state, runtime, ct); return; }
        state.Status = PlanningStatus.Generating;
        if (construction.Complete)
        {
            await CompileAsync(state, runtime, ct);
            return;
        }
        if (state.Catalog is null)
        {
            state.Catalog = await runtime.DiscoverAsync(state.Request, ct);
            state.Discovery.Sources = (await runtime.Capabilities.ListSourcesAsync(ct)).ToList();
            if (state.Discovery.Sources.Count == 1)
                await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, state.Discovery.Sources[0].Id, null, ct, PlanningDiscoveryContext.Query(state));
        }
        if (construction.Candidate is { } candidate)
        {
            if (candidate.BaseHash != PlanningConstruction.Hash(state.Plan))
                throw new PlanningConflictException("The tentative extension no longer matches its accepted checkpoint.");
            if (candidate.Diagnostics.Count > 0)
            {
                await CorrectConstructionAsync(state, runtime, ct);
                return;
            }
            if (!candidate.LocalReviewed || candidate.NeedsIntermediateReview && !candidate.IntermediateReviewed)
            {
                await ReviewConstructionAsync(state, runtime, candidate.LocalReviewed, ct);
                return;
            }
            await AcceptConstructionAsync(state, runtime, ct);
            return;
        }
        if (construction.Checkpoints.Count >= limits.MaxCheckpoints)
        {
            state.Diagnostics = [new("CONSTRUCTION_CHECKPOINT_LIMIT", "/construction", "The captured checkpoint allowance is exhausted. Accepted fragments and accounting are retained.")];
            Stop(state); return;
        }
        var repair = state.PendingCall is { } retained ? retained.Purpose == "replan" : state.Diagnostics.Any(d => d.Required);
        if (repair && state.PendingCall is null && (construction.DraftCorrections >= construction.Limits.MaxCorrectionsPerDraft || PlanningModelCalls.RemainingRepairs(state) == 0))
        { Stop(state); return; }
        await GenerateConstructionAsync(state, runtime, repair, ct);
    }

    private static async Task ReserveConstructionReviewAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!;
        var calls = PlanningCompactConstruction.UsesConvergentConstruction(state) ? 1 + Math.Max(1, PlanningCompactConstruction.RequiredReviewCalls(state)) :
            construction.Candidate?.NeedsIntermediateReview == true || construction.Candidate is null && construction.Checkpoints.Count > 0 ? 3 : 2;
        var inputLimit = construction.Version == 2 ? PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningConstruction.DraftPurpose)
            : state.Request.Generation.MaxInputTokensPerRequest;
        var draftOutput = PlanningModelCalls.ConstructionOutputLimit(state, PlanningConstruction.DraftPurpose);
        var reviewOutput = PlanningModelCalls.ConstructionOutputLimit(state, PlanningConstruction.LocalPurpose);
        if (state.PendingCall is null && PlanningModelCalls.RemainingCalls(state) < calls)
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "Construction needs a draft or correction and its mandatory local review within the saved cumulative allowance. No call was dispatched.");
        if (state.PendingCall is null && PlanningBudgetOptions.Parse(state.Request.Options)?.MaxTotalTokens is { } limit &&
            limit - (state.Usage?.TotalTokens ?? 0) < (long)inputLimit * calls +
                draftOutput + (long)(calls - 1) * reviewOutput)
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "The remaining cumulative token allowance cannot admit the draft and mandatory reviews at their captured request ceilings. No call was dispatched.");
        if (state.PendingCall is null && runtime is IPlanningConstructionAdmission admission)
        {
            var outputCeilings = Enumerable.Repeat(reviewOutput, calls).ToArray();
            outputCeilings[0] = draftOutput;
            var probe = PlanningConstructionPhasePolicies.Apply(new GnOuGo.Flow.Core.Runtime.LLMRequest
            {
                Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
                Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "", MaxTokens = outputCeilings[0]
            }, state.Request, PlanningConstruction.DraftPurpose);
            await admission.AdmitConstructionAsync(probe, inputLimit, outputCeilings, ct);
        }
    }

    private static async Task GenerateConstructionAsync(PlanningSession state, IPlanningRuntime runtime, bool repair, CancellationToken ct)
    {
        var construction = state.Construction!;
        await ReserveConstructionReviewAsync(state, runtime, ct);
        var previous = construction.Candidate;
        using var correction = new PlanningCorrectionAttempt(state, repair, draft: true);
        if (!correction.Enabled && repair && previous is null && state.PendingCall is null) construction.DraftCorrections++;
        if (state.PendingCall is null && construction.Version == 1)
        {
            foreach (var capability in PlanningDiscoveryContext.Candidates(state).Take(4))
                if (!state.Discovery.Resolved.Any(c => c.Id == capability.Id && c.Version == capability.Version) &&
                    !state.Catalog!.Capabilities.Any(c => c.Id == capability.Id && c.Version == capability.Version))
                    await PlanningDiscoveryContext.ResolveAsync(state, runtime, capability, ct);
        }
        state.Phase = repair ? PlanningPhase.Replanning : PlanningConstruction.DraftPurpose;
        if (state.PendingCall is null) construction.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        else if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The issued extension no longer matches its retained accepted checkpoint.");
        // CallAsync checkpoints this transient recovery contract with the issued
        // request before dispatch. Already issued requests/candidates keep their
        // captured (including absent historical) comparison semantics.
        if (state.PendingCall is null && previous is null && construction is { Version: 2, Compact.PresentationVersion: 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 })
            construction.CandidateFailureIdentityVersion = 1;
        var request = state.PendingCall?.Request ?? PlanningConstruction.DraftRequest(state, repair);
        var response = await PlanningModelCalls.CallAsync(state, runtime, repair ? "replan" : PlanningConstruction.DraftPurpose,
            request.Prompt, request.StructuredOutputSchema!.AsObject(), ct, construction.Limits.DraftOutputTokens, correction);
        construction.LastProposalHash = "proposal:" + PlanningGraphCompiler.Fingerprint(response.ToJsonString());
        if (construction.FailedCandidates.Contains(construction.LastProposalHash, StringComparer.Ordinal))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated an invalid construction action. No budget was reset.");
        var reselection = response["reselectStep"] is { } selected
            ? PlanningCompactConstruction.ReadReselection(state, selected, request.StructuredOutputSchema!) : null;
        var normalized = response.DeepClone();
        if (reselection is not null) normalized["reselectStep"] = reselection.Reason;
        var proposal = normalized.Deserialize(ConstructionJsonContext.Default.PlanningConstructionResponse)!;
        if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan) || proposal.BaseHash != construction.IssuedBaseHash)
            throw new PlanningConflictException("The completed extension no longer matches its retained accepted checkpoint.");
        var actions = (proposal.Extension is null ? 0 : 1) + (proposal.DiscoveryRequests is null ? 0 : 1) +
            (proposal.Clarifications is null ? 0 : 1) + (proposal.EvidenceRequests is null ? 0 : 1) + (proposal.RollbackTask is null ? 0 : 1) +
            (proposal.ReselectStep is null ? 0 : 1);
        if (actions != 1) PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return exactly one construction action.");
        if (proposal.ReselectStep is { } reason)
        {
            if (!repair)
            {
                // CallAsync validated against the exact issued schema, including
                // recovered requests whose reselectStep may still be null-only.
                if (!PlanningCompactConstruction.CanReselectInitialDraft(state) ||
                    proposal.Requirements is not null || proposal.Commitments is not null || proposal.RollbackReason is not null ||
                    string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
                    PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep",
                        "Reselect only the unaccepted tentative step within its captured correction allowance.");
                if (correction.Enabled) correction.Reselect();
                else { state.ReplanAttempts++; construction.DraftCorrections++; }
                state.Diagnostics.Add(new("CONSTRUCTION_TENTATIVE_SELECTION", "/construction/compact/currentStep", reason));
                PlanningCompactConstruction.Reselect(state, reselection!);
                construction.FailedCandidates.Add(construction.LastProposalHash);
                // The archived diagnosis consumed this correction. Replacement
                // selection must not charge it again after a checkpoint or restart.
                state.Diagnostics.Clear();
            }
            else
            {
                PlanningCompactConstruction.Reselect(state, reselection!);
                if (correction.Enabled) state.Diagnostics.Clear();
            }
            return;
        }
        if (proposal.Clarifications is { } questions)
        {
            if (proposal.Requirements is not null) PlanningConstruction.Fail("CLARIFICATION_INVALID", "/requirements", "Clarify before accepting requirements.");
            PlanningClarifications.Suspend(state, questions); state.PendingQuestions = questions; state.Status = PlanningStatus.Clarification;
            return;
        }
        if (proposal.EvidenceRequests is { } evidence)
        {
            if (correction.Enabled && (proposal.Requirements is not null || proposal.Commitments is not null || proposal.RollbackReason is not null))
                PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/evidenceRequests", "Evidence retrieval cannot also stage requirements, commitments or rollback decisions.");
            try
            {
                PlanningConstruction.LoadEvidence(state, evidence, followup: next =>
                {
                    var draft = PlanningConstruction.DraftRequest(next, repair);
                    return PlanningModelCalls.PrepareRequest(next, repair ? "replan" : PlanningConstruction.DraftPurpose,
                        draft.Prompt, draft.StructuredOutputSchema!.AsObject(), next.Construction!.Limits.DraftOutputTokens);
                }, purpose: repair ? "replan" : PlanningConstruction.DraftPurpose);
            }
            catch (PlanningResponseException error) when (PlanningCompactConstruction.UsesVerifiedReview(state) &&
                PlanningConstructionPhasePolicies.IsFresh(state.Request) && error.Diagnostics.All(PlanningCompactConstruction.IsEvidenceAdmissionLimit))
            {
                // Admission is atomic. A rejected batch is navigation feedback,
                // but it must not replace unresolved semantic repair findings.
                if (evidence.Count == 1)
                    throw new WorkflowRuntimeException("CONSTRUCTION_EVIDENCE_LIMIT", error.Diagnostics[0].Message);
                var unresolved = state.Diagnostics.Where(diagnostic => diagnostic.Required &&
                    !PlanningCompactConstruction.IsEvidenceAdmissionLimit(diagnostic)).ToArray();
                if (unresolved.Length > 0)
                    throw new PlanningResponseException(unresolved.Concat(error.Diagnostics).Distinct().ToList());
                throw;
            }
            correction.EvidenceAccepted();
            return;
        }
        if (proposal.DiscoveryRequests is { } discovery)
        {
            await ConstructionDiscoveryAsync(state, runtime, discovery, ct);
            return;
        }
        if (proposal.RollbackTask is { } task)
        {
            if (repair) PlanningConstruction.Fail("CONSTRUCTION_ROLLBACK_SCOPE", "/rollbackTask", "Finish or explicitly abandon the current candidate before rolling back accepted work.");
            PlanningConstruction.Rollback(state, task, proposal.RollbackReason);
            return;
        }
        ValidateRequirements(state, new PlanningProposal { Requirements = proposal.Requirements });
        var requirements = state.Requirements ?? proposal.Requirements;
        if (requirements?.Inputs is null || requirements.Outputs is null)
            PlanningConstruction.Fail("REQUIREMENTS_INVALID", "/requirements", "The first extension requires explicit caller inputs and business outputs.");
        if (PlanningCommitments.Validate(state, requirements!, final: true) is { Count: > 0 } invalid)
            throw new PlanningResponseException(invalid);
        state.Requirements ??= requirements;
        if (construction.Requirements.Count == 0)
            construction.Requirements = state.Requirements!.Outcomes.Select(r => new PlanningConstructionRequirement { Id = r.Id }).ToList();
        if (repair && previous is not null && proposal.Extension!.Scope != previous.Extension.Scope)
            PlanningConstruction.Fail("REVISION_SCOPE_CHANGED", "/extension/scope", "Correction cannot move the tentative extension to another scope.");
        var next = PlanningConstruction.Apply(state, proposal.Extension!);
        if (construction.Version == 2) PlanningCompactConstruction.StageCommitments(state, proposal.Commitments);
        if (construction.Version == 2) PlanningCompactConstruction.ValidateExtension(state, next);
        if (state.Request.Mode == PlanningMode.Auto)
        {
            foreach (var choice in next.Plan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            next.Hash = PlanningConstruction.Hash(next.Plan);
        }
        next.Corrections = previous?.Corrections ?? construction.DraftCorrections;
        PlanningConstruction.CarryDefectWitnesses(state, previous, next);
        if (PlanningConstruction.PreviouslyRejected(state, next))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "Construction repeated a failed semantic candidate. No budget was reset.");
        construction.Candidate = next; construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        construction.Evidence = null; construction.EvidenceRequests.Clear();
        await ValidateConstructionCandidateAsync(state, runtime, ct);
        if (next.Diagnostics.Count == 0 && state.Request.Mode == PlanningMode.Interactive && next.Plan.Choices.Any(c => c.Selected is null))
            state.Status = PlanningStatus.Clarification;
    }

    private static Task ValidateConstructionCandidateAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var draft = state.Construction!.Candidate!;
        var pendingRevision = PlanningCompactConstruction.UsesDecisionView(state) &&
            draft.AuthorizedPaths is { Count: > 0 } authorized && draft.Hash == draft.BaseHash &&
            draft.Hash == PlanningConstruction.Hash(draft.Plan) && draft.BaseHash == PlanningConstruction.Hash(state.Plan)
            ? draft.Diagnostics.Where(d => d.Required && d.Code == "JUDGE_SCOPED_REVIEW" &&
                authorized.Contains(d.Location, StringComparer.Ordinal)).ToArray() : [];
        if (state.Construction.Version == 2) PlanningCompactConstruction.ValidateExtension(state, draft);
        foreach (var operation in TaskPlanRevisions.Tasks(draft.Plan).Select(t => t.Operation).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (state.Catalog!.Capabilities.Any(c => TaskOperations.Describe(c).Id == operation)) continue;
            var matches = state.Discovery.Resolved.Where(c => TaskOperations.Describe(c).Id == operation).DistinctBy(c => (c.Id, c.Version)).ToArray();
            if (matches.Length == 1) state.Catalog.Capabilities.Add(matches[0]);
        }
        var candidate = PlanningConstruction.Clone(draft.Plan);
        foreach (var choice in candidate.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        draft.Diagnostics = TaskPlanCompiler.ValidateConstruction(candidate, state.Catalog!, state.Request,
            draft.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics.ToList();
        draft.Diagnostics.AddRange(PlanningConstruction.RebuildFindings(state, candidate, draft.Extension.Complete));
        if (draft.Extension.Complete)
        {
            if (draft.Diagnostics.Count == 0)
            {
                var compilation = new TaskPlanCompiler().Compile(candidate, state.Catalog!, state.Request);
                draft.Diagnostics.AddRange(compilation.Diagnostics);
                if (compilation.Graph is { } graph)
                {
                    var review = new PlanningSession { Plan = candidate, Catalog = state.Catalog, Requirements = state.Construction.Version == 2 ? PlanningCompactConstruction.CandidateRequirements(state) : state.Requirements,
                        IntentVersion = state.IntentVersion, Request = state.Request, Discovery = state.Discovery };
                    draft.Diagnostics.AddRange(PlanningClarifications.OutputFindings(review, graph));
                    draft.Diagnostics.AddRange(PlanningCommitments.Assess(review).SelectMany(r => r.Diagnostics).Where(d => d.Required));
                }
            }
            var covered = state.Construction.Requirements.Where(r => r.Status != "remaining").Select(r => r.Id)
                .Concat(draft.Extension.RequirementIds).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in state.Requirements!.Outcomes.Where(r => !covered.Contains(r.Id)))
                draft.Diagnostics.Add(new("CONSTRUCTION_REQUIREMENT_REMAINING", "/requirements/" + missing.Id, "Associate or explicitly resolve this accepted requirement before declaring completion."));
        }
        if (state.Construction.Version == 2)
            draft.Diagnostics.AddRange(PlanningCompactConstruction.CandidateFindings(state));
        PlanningConstruction.RetainTechnicalRejections(state);
        if (PlanningCompactConstruction.UsesConvergentConstruction(state) && draft.Diagnostics.Count == 0 &&
            draft.Reviews.Any(review => review["defectWitness"] is JsonObject))
        {
            // Candidate novelty and the resolution of its cited defect are different.
            // An unchanged affected value without new support needs another local
            // strategy, not another business Judge judging the same implementation.
            var context = PlanningCompactConstruction.Context(state, review: true, intermediate: draft.NeedsIntermediateReview);
            var unchanged = (context["defectProgress"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(progress => progress["status"]?.ToString() == "unchanged" && progress["evidenceImproved"]?.GetValue<bool>() != true).ToArray();
            foreach (var progress in unchanged)
            {
                var identity = "defect:" + progress["id"] + ":" + progress["current"]?["hash"];
                if (state.Construction.FailedCandidates.Contains(identity, StringComparer.Ordinal))
                    throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The same affected implementation was repeated without relevant evidence or a different applicable repair strategy.");
                state.Construction.FailedCandidates.Add(identity);
                draft.Diagnostics.Add(new("CONSTRUCTION_DEFECT_UNCHANGED", "/construction/candidate",
                    "The prior finding's cited implementation is unchanged. Its truth remains advisory: make a targeted repair, retrieve evidence contesting it, suspend for an evidenced prerequisite, or withdraw a premature completion claim within existing authority. Prior witness: " + progress["id"]));
            }
        }
        // Reindexing intent cannot discharge an unchanged, explicitly requested repair.
        // Keep host intent out of deterministic history; a changed typed patch clears it.
        draft.Diagnostics.AddRange(pendingRevision);
        state.Diagnostics = draft.Diagnostics.ToList();
        state.Phase = draft.Diagnostics.Count == 0 ? PlanningConstruction.LocalPurpose : PlanningPhase.Replanning;
        if (draft.Diagnostics.Count > 0) PlanningConstruction.RecordRejection(state, draft);
        return Task.CompletedTask;
    }

    private static async Task CorrectConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!; var draft = construction.Candidate!;
        if (state.PendingCall is null && (draft.Corrections >= construction.Limits.MaxCorrectionsPerDraft || PlanningModelCalls.RemainingRepairs(state) == 0 ||
            state.Request.Options["automatic_correction"]?.ToString() == "false"))
        { state.Diagnostics = draft.Diagnostics; Stop(state); return; }
        await ReserveConstructionReviewAsync(state, runtime, ct);
        if (state.PendingCall is null && !PlanningConstructionPhasePolicies.IsFresh(state.Request)) draft.Corrections++;
        // An output-only patch cannot introduce a missing committed producer.
        // Ordinary construction owns this tentative fragment, so replace it within
        // the same bounded envelope. Preserve issued requests and explicit edits.
        if (state.PendingCall is null && draft.AuthorizedPaths is null &&
            draft.Diagnostics.Any(d => d.Required && d.Code == PlanningCommitments.MissingOperation))
        {
            await GenerateConstructionAsync(state, runtime, repair: true, ct);
            return;
        }
        // Reuse typed repair whenever diagnosed slots stay inside the tentative fragment.
        var repairState = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        repairState.Plan = draft.Plan;
        repairState.EditablePaths = draft.AuthorizedPaths;
        repairState.RevisionScope = draft.AuthorizedPaths ?? TaskPlanRevisions.Scope(draft.Plan, draft.Diagnostics).Where(p => PlanningConstruction.Owns(draft, p)).ToList();
        repairState.Diagnostics = draft.Diagnostics;
        var guardPaths = draft.AuthorizedPaths is null ? draft.Diagnostics.Where(d => d.Code == "TASK_CONDITION_TYPE" && d.Location.EndsWith("/requires", StringComparison.Ordinal) &&
            PlanningConstruction.Owns(draft, d.Location)).Select(d => d.Location).Distinct(StringComparer.Ordinal).ToArray() : [];
        if (guardPaths.Length > 0)
        {
            var sources = GuardSources(draft.Plan, state.Catalog!);
            var original = new TaskPlanSymbols(draft.Plan);
            var unsupported = guardPaths.Where(p => !original.Values.TryGetValue(p, out var value) || !CorrectableGuard(value.Value, sources.GetValueOrDefault(p) ?? [])).ToArray();
            if (unsupported.Length > 0) { state.Diagnostics = unsupported.Select(UnsafeGuard).ToList(); Stop(state); return; }
            repairState.RevisionScope = repairState.RevisionScope.Concat(guardPaths).Distinct(StringComparer.Ordinal).ToList();
            repairState.EditablePaths = repairState.RevisionScope.ToList();
            repairState.Request.RevisionContext += " " + PlanningPrompt.GuardCorrectionInstructions;
        }
        if (repairState.RevisionScope.Count == 0 || state.PendingCall is { } pending && !PlanningRepairPatch.Issued(pending.Request.StructuredOutputSchema?.AsObject()))
        {
            await GenerateConstructionAsync(state, runtime, repair: true, ct);
            return;
        }
        GnOuGo.Flow.Core.Runtime.LLMRequest request;
        try { request = state.PendingCall?.Request ?? PlanningConstruction.RepairRequest(state, repairState, guardPaths.Length > 0 ? PlanningPrompt.GuardCorrectionInstructions : null); }
        catch (WorkflowRuntimeException ex) when (state.PendingCall is null && draft.AuthorizedPaths is null && guardPaths.Length == 0 && ex.Code is "REVISION_REQUIRED" or "REPAIR_SCOPE_INVALID")
        {
            // The fresh construction contract already owns this tentative fragment;
            // a bounded replacement does not grant edits to accepted work.
            await GenerateConstructionAsync(state, runtime, repair: true, ct);
            return;
        }
        PlanningRepairPatch.Verify(repairState, request);
        using var accounting = new PlanningCorrectionAttempt(state, correcting: true, draft: true);
        var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", request.Prompt, request.StructuredOutputSchema!.AsObject(), ct, construction.Limits.DraftOutputTokens, accounting);
        var selectedReselection = response["reselectStep"] is { } selected
            ? PlanningCompactConstruction.ReadReselection(state, selected, request.StructuredOutputSchema!) : null;
        var normalized = response.DeepClone();
        if (selectedReselection is not null) normalized["reselectStep"] = selectedReselection.Reason;
        var correction = normalized.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        if (correction.ReselectStep is { } reselection)
        {
            if (correction.Patch is not null || correction.DiscoveryRequests is not null || correction.Clarifications is not null || correction.EvidenceRequests is not null)
                PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/reselectStep", "Reselection must be the only correction action.");
            PlanningCompactConstruction.Reselect(state, selectedReselection!);
            if (accounting.Enabled) state.Diagnostics.Clear();
            return;
        }
        if (correction.EvidenceRequests is { } evidence)
        {
            if (correction.Patch is not null || correction.DiscoveryRequests is not null || correction.Clarifications is not null)
                PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/evidenceRequests", "Return original evidence requests as the only repair action.");
            PlanningConstruction.LoadEvidence(state, evidence, followup: next =>
            {
                var followup = PlanningConstruction.RepairRequest(next, repairState, guardPaths.Length > 0 ? PlanningPrompt.GuardCorrectionInstructions : null);
                return PlanningModelCalls.PrepareRequest(next, "replan", followup.Prompt, followup.StructuredOutputSchema!.AsObject(), next.Construction!.Limits.DraftOutputTokens);
            }, purpose: "replan");
            accounting.EvidenceAccepted();
            return;
        }
        if (correction.DiscoveryRequests is { } discovery)
        { await ConstructionDiscoveryAsync(state, runtime, discovery, ct); return; }
        if (correction.Clarifications is { } questions)
        { PlanningClarifications.Suspend(state, questions); state.PendingQuestions = questions; state.Status = PlanningStatus.Clarification; return; }
        if (correction.Patch is null) PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/patch", "Return the issued semantic repair.");
        var revised = PlanningRepairPatch.Apply(repairState, correction.Patch!, request, draft.OpenScopes.ToHashSet(StringComparer.Ordinal));
        if (guardPaths.Length > 0)
        {
            var before = new TaskPlanSymbols(draft.Plan); var after = new TaskPlanSymbols(revised);
            var sources = GuardSources(draft.Plan, state.Catalog!, revised);
            var unsafePaths = guardPaths.Where(p => !after.Values.TryGetValue(p, out var next) ||
                !PreservesNullableGuard(before.Values[p].Value, next.Value, sources.GetValueOrDefault(p) ?? [])).ToArray();
            if (unsafePaths.Length > 0) { state.Diagnostics = unsafePaths.Select(UnsafeGuard).ToList(); Stop(state); return; }
        }
        PlanningConstruction.PreserveAccepted(state, revised);
        var hash = PlanningConstruction.Hash(revised);
        if (hash == draft.Hash || PlanningConstruction.PreviouslyRejected(state, draft, hash))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The correction repeated a rejected candidate or made no semantic change.");
        draft.Plan = revised; draft.Hash = hash; draft.LocalReviewed = false; draft.IntermediateReviewed = false;
        // Task/scope evidence describes the previous candidate. Its consumed
        // fingerprints remain auditable and already include that candidate hash.
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request)) construction.Evidence = null;
        PlanningConstruction.RefreshCandidate(state);
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        await ValidateConstructionCandidateAsync(state, runtime, ct);
    }

    private static async Task AcceptConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!; var draft = construction.Candidate!;
        if (!draft.LocalReviewed || draft.NeedsIntermediateReview && !draft.IntermediateReviewed || draft.Diagnostics.Count > 0 ||
            !PlanningConstructionReviewCoverage.Complete(state, draft))
            throw new PlanningConflictException("The candidate has not completed its required deterministic and advisory reviews.");
        PlanningConstruction.PreserveAccepted(state, draft.Plan);
        var symbols = new TaskPlanSymbols(draft.Plan);
        var retainedTasks = draft.TaskIds.AsEnumerable();
        if (PlanningCompactConstruction.UsesAcceptedProgress(state))
            retainedTasks = retainedTasks.Concat(draft.Extension.CloseScopes.SelectMany(path => symbols.Scopes
                .Where(scope => scope.Path == path && scope.Owner is not null).Select(scope => scope.Owner!.Id))).Distinct(StringComparer.Ordinal);
        var checkpoint = new PlanningConstructionCheckpoint
        {
            Sequence = construction.Checkpoints.Count + 1, BaseHash = draft.BaseHash, PlanHash = draft.Hash,
            TaskIds = draft.TaskIds, ImportedTaskIds = draft.ImportedTaskIds, RequirementIds = draft.Extension.RequirementIds,
            ClosedRequirementIds = construction.Version == 2 ? construction.Compact!.CurrentStep!.CloseRequirementIds.ToList() : null,
            Step = construction.Version == 2 ? PlanningConstruction.Node(construction.Compact!.CurrentStep,
                PlanningJsonContext.Default.PlanningConstructionStep)!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep) : null,
            ClosedScopes = draft.Extension.CloseScopes, Reviews = draft.Reviews, NeedsIntermediateReview = draft.NeedsIntermediateReview,
            Fragment = new JsonObject
            {
                ["scope"] = draft.Extension.Scope,
                ["tasks"] = new JsonArray(retainedTasks.Where(symbols.Tasks.ContainsKey)
                    .Select(id => PlanningConstruction.Node(symbols.Tasks[id].Task, PlanningJsonContext.Default.PlanTask)).ToArray()),
                ["outputs"] = PlanningConstruction.Node(draft.Extension.Outputs, PlanningJsonContext.Default.ListTaskOutput),
                ["interfaces"] = PlanningConstruction.Context(state, review: true)["interfaces"]?.DeepClone()
            }
        };
        if (construction.Version == 2)
        {
            if (PlanningCompactConstruction.UsesDecisionView(state)) checkpoint.Fragment["complete"] = draft.Extension.Complete;
            if (draft.Extension.Groups.Count > 0)
                checkpoint.Fragment["groups"] = new JsonArray(draft.Extension.Groups.Select(g => (JsonNode?)JsonValue.Create(g.Id)).ToArray());
            if (draft.Extension.Choices.Count > 0)
                checkpoint.Fragment["choices"] = new JsonArray(draft.Extension.Choices.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray());
        }
        state.Plan = draft.Plan; construction.OpenScopes = draft.OpenScopes; construction.Checkpoints.Add(checkpoint);
        foreach (var requirement in construction.Requirements.Where(r => draft.Extension.RequirementIds.Contains(r.Id, StringComparer.Ordinal)))
        { if (construction.Version == 1) requirement.Status = "declared"; requirement.Checkpoints.Add(checkpoint.Sequence); }
        if (construction.Version == 2) PlanningCompactConstruction.Accept(state);
        construction.Complete = draft.Extension.Complete; construction.Candidate = null; construction.CandidateFailureIdentityVersion = null;
        construction.DraftCorrections = 0; construction.LastProposalHash = null;
        construction.PendingReviewContext = null; construction.PendingReviewHash = null; construction.Evidence = null; construction.EvidenceRequests.Clear();
        state.Diagnostics.Clear(); state.Graph = null; Invalidate(state);
        state.Phase = construction.Complete ? PlanningPhase.Validation : PlanningConstruction.DraftPurpose;
        await runtime.CheckpointAsync(state, ct);
        if (construction.Complete) await CompileAsync(state, runtime, ct);
        else if (state.Request.Mode == PlanningMode.Interactive && state.Plan.Choices.Any(c => c.Selected is null)) state.Status = PlanningStatus.Clarification;
    }

    private static async Task ConstructionDiscoveryAsync(PlanningSession state, IPlanningRuntime runtime, List<PlanningDiscoveryRequest> requests, CancellationToken ct, PlanningCorrectionAttempt? correction = null)
    {
        if (requests.Count is < 1 or > 4 || requests.Distinct().Count() != requests.Count)
            PlanningConstruction.Fail("DISCOVERY_BATCH_INVALID", "/discoveryRequests", "Request one to four distinct metadata reads.");
        if (PlanningCompactConstruction.UsesDeferredBinding(state))
        {
            BeginConstructionDiscovery(state, requests);
            correction?.EvidenceAccepted();
            correction?.Dispose(); // Classify the received action once, before any durable metadata progress.
            await runtime.CheckpointAsync(state, ct);
            await ContinueConstructionDiscoveryAsync(state, runtime, ct);
            return;
        }
        foreach (var request in requests)
        {
            if (!state.Discovery.Sources.Any(s => s.Id == request.SourceId)) PlanningConstruction.Fail("SOURCE_UNKNOWN", "/discoveryRequests", "Select an issued capability source.");
            if (request.OperationIds is not null)
            {
                var selected = PlanningDiscoveryContext.Inspection(state, request, "/discoveryRequests");
                state.Discovery.Inspections ??= []; state.Discovery.Inspections.RemoveAll(i => i.SourceId == request.SourceId); state.Discovery.Inspections.Add(request);
                foreach (var item in selected) await PlanningDiscoveryContext.ResolveAsync(state, runtime, item, ct);
                continue;
            }
            var parent = request.Cursor is null ? null : state.Discovery.Pages.SingleOrDefault(p => p.SourceId == request.SourceId && p.NextCursor == request.Cursor &&
                (state.Construction?.Version != 2 || p.Query == PlanningCompactConstruction.State(state).CurrentStep!.Objective && p.ProducedArtifactKind is null));
            if (request.Cursor is not null && (parent is null || request.Query is not null || request.ProducedArtifactKind is not null))
                PlanningConstruction.Fail("CURSOR_UNKNOWN", "/discoveryRequests", "Select an issued continuation cursor without changing its query.");
            var query = request.Cursor is null ? request.Query ?? (state.Construction?.Version == 2
                ? PlanningCompactConstruction.State(state).CurrentStep!.Objective : PlanningDiscoveryContext.Query(state)) : parent!.Query;
            if (state.Construction?.Version == 2 && query != PlanningCompactConstruction.State(state).CurrentStep!.Objective)
                PlanningConstruction.Fail("DISCOVERY_SCOPE_INVALID", "/discoveryRequests", "Discovery and its continuation must use the selected business substep's exact objective.");
            var kind = request.Cursor is null ? request.ProducedArtifactKind : parent!.ProducedArtifactKind;
            if (state.Discovery.Pages.Any(p => p.SourceId == request.SourceId && p.Cursor == request.Cursor && p.Query == query && p.ProducedArtifactKind == kind))
                PlanningConstruction.Fail("DISCOVERY_NO_PROGRESS", "/discoveryRequests", "This metadata page is retained already.");
            await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, request.SourceId, request.Cursor, ct, query, kind);
        }
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request))
            PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
        state.Phase = PlanningPhase.Discovery;
    }

    private static string DiscoveryActionHash(PlanningConstructionStep step, IEnumerable<PlanningDiscoveryRequest> requests) =>
        PlanningGraphCompiler.Fingerprint(step.Id + "\n" + step.BaseHash + "\n" + step.Objective + "\n" +
            JsonSerializer.Serialize(requests.ToList(), PlanningJsonContext.Default.ListPlanningDiscoveryRequest));

    private static void BeginConstructionDiscovery(PlanningSession state, List<PlanningDiscoveryRequest> requests)
    {
        var step = PlanningCompactConstruction.State(state).CurrentStep!;
        if (step.DiscoveryComplete || step.PendingDiscovery is not null || state.PendingCall is not null)
            throw new PlanningConflictException("Complete the exact retained discovery action before selecting another.");
        foreach (var request in requests)
        {
            if (request.OperationIds is not null || request.Query is not null || request.ProducedArtifactKind is not null ||
                !state.Discovery.Sources.Any(source => source.Id == request.SourceId))
                PlanningConstruction.Fail("DISCOVERY_SCOPE_INVALID", "/discoveryRequests", "Select an issued source/cursor pair for this exact business action.");
            if (request.Cursor is not null && !state.Discovery.Pages.Any(page => page.SourceId == request.SourceId &&
                    page.NextCursor == request.Cursor && page.Query == step.Objective && page.ProducedArtifactKind is null))
                PlanningConstruction.Fail("CURSOR_UNKNOWN", "/discoveryRequests", "A continuation must come from a retained page for this same provider and business action.");
        }
        step.PendingDiscovery = new()
        {
            ActionHash = DiscoveryActionHash(step, requests), Requests = requests.ToList(),
            Pending = requests.Select(request => request with { Query = step.Objective }).ToList()
        };
        state.Diagnostics.RemoveAll(d => d.Location.StartsWith("/discoveryRequests", StringComparison.Ordinal) &&
            d.Code is "DISCOVERY_SCOPE_INVALID" or "SOURCE_UNKNOWN" or "CURSOR_UNKNOWN" or "DISCOVERY_BATCH_INVALID" or "DISCOVERY_NO_PROGRESS");
    }

    private static async Task ContinueConstructionDiscoveryAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var step = PlanningCompactConstruction.State(state).CurrentStep!;
        var action = step.PendingDiscovery ?? throw new PlanningConflictException("No received discovery action is pending.");
        if (!PlanningCompactConstruction.UsesDeferredBinding(state) || step.DiscoveryComplete || state.PendingCall is not null ||
            step.BaseHash != PlanningConstruction.Hash(state.Plan) || action.ActionHash != DiscoveryActionHash(step, action.Requests) ||
            action.Requests.Count is < 1 or > 4 || action.RemainingReads is < 0 or > 4)
            throw new PlanningConflictException("The retained discovery action no longer matches its exact business step.");
        while (action.RemainingReads > 0 && action.Pending.Count > 0)
        {
            var request = action.Pending[0];
            var identity = JsonSerializer.Serialize(new List<PlanningDiscoveryRequest> { request }, PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            if (request.Query != step.Objective || request.OperationIds is not null || request.ProducedArtifactKind is not null ||
                !action.Requests.Any(original => original.SourceId == request.SourceId) ||
                !state.Discovery.Sources.Any(source => source.Id == request.SourceId) || action.Visited.Contains(identity, StringComparer.Ordinal))
                throw new WorkflowRuntimeException("DISCOVERY_NO_PROGRESS", "The retained provider continuation is invalid or cyclic.");
            if (request.Cursor is not null && !state.Discovery.Pages.Any(page => page.SourceId == request.SourceId && page.Query == step.Objective &&
                    page.ProducedArtifactKind is null && page.NextCursor == request.Cursor))
                throw new PlanningConflictException("The retained provider cursor lost its exact original page.");
            var page = state.Discovery.Pages.SingleOrDefault(page => page.SourceId == request.SourceId && page.Cursor == request.Cursor &&
                page.Query == step.Objective && page.ProducedArtifactKind is null);
            if (page is null)
            {
                await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, request.SourceId, request.Cursor, ct, step.Objective);
                action.RemainingReads--;
                page = state.Discovery.Pages[^1];
            }
            if (page.UnavailableReason is not null)
            {
                await runtime.CheckpointAsync(state, ct);
                throw new WorkflowRuntimeException("DISCOVERY_INCOMPLETE", "The selected provider metadata is unavailable; its retained outcome cannot establish a capability absence.");
            }
            action.Visited.Add(identity); action.Pending.RemoveAt(0);
            if (page.NextCursor is { } cursor)
            {
                if (string.IsNullOrWhiteSpace(cursor) || cursor == page.Cursor)
                    throw new WorkflowRuntimeException("DISCOVERY_NO_PROGRESS", "The selected provider returned an invalid or cyclic continuation.");
                action.Pending.Add(new(request.SourceId, cursor, step.Objective));
            }
            await runtime.CheckpointAsync(state, ct);
        }
        PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
        var purpose = PlanningCompactConstruction.DiscoveryPurpose;
        var followup = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.DiscoveryGuidance(state), PlanningCompactConstruction.DiscoveryContext(state),
            PlanningCompactConstruction.DiscoverySchema(state), purpose);
        try
        {
            PlanningConstruction.AdmitEvidenceFollowup(state, PlanningModelCalls.PrepareRequest(state, purpose, followup.Prompt,
                followup.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens), purpose);
        }
        catch (PlanningResponseException ex) when (ex.Diagnostics.Any(d => d.Code is "CONSTRUCTION_EVIDENCE_LIMIT" or "CONSTRUCTION_EVIDENCE_INVALID"))
        {
            throw new WorkflowRuntimeException("CONSTRUCTION_DISCOVERY_LIMIT",
                "The retained provider metadata cannot form a valid discovery request within the captured input ceiling. Its exact received action and completed pages remain retained; no follow-up was dispatched.");
        }
        step.PendingDiscovery = null;
        state.Phase = PlanningPhase.Discovery;
    }
}
