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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanningConstructionResponse))]
internal partial class ConstructionJsonContext : JsonSerializerContext;

public sealed partial class HybridWorkflowPlanner
{
    private static void ReviseConstruction(PlanningSession state, PlanningCommand command)
    {
        var construction = state.Construction!;
        construction.Candidate = null; construction.PendingReviewContext = null; construction.PendingReviewHash = null;
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
            return;
        }
        if (!construction.OpenScopes.Contains("/root", StringComparer.Ordinal)) construction.OpenScopes.Add("/root");
    }

    private static async Task AdvanceConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!;
        var limits = construction.Limits;
        if (construction.Version != 1 || limits.MaxTasksPerDraft is < 1 or > 32 || limits.MaxCheckpoints is < 1 or > 1024 ||
            limits.MaxCorrectionsPerDraft is < 0 or > 64 || limits.IntermediateReviewInterval is < 1 or > 128 ||
            limits.DraftOutputTokens is < 1 or > 65536 || limits.ReviewOutputTokens is < 1 or > 65536)
            throw new PlanningConflictException("The captured construction contract or limits are unsupported.");
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
        var calls = construction.Candidate?.NeedsIntermediateReview == true || construction.Candidate is null && construction.Checkpoints.Count > 0 ? 3 : 2;
        if (state.PendingCall is null && PlanningModelCalls.RemainingCalls(state) < calls)
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "Construction needs a draft or correction and its mandatory local review within the saved cumulative allowance. No call was dispatched.");
        if (state.PendingCall is null && PlanningBudgetOptions.Parse(state.Request.Options)?.MaxTotalTokens is { } limit &&
            limit - (state.Usage?.TotalTokens ?? 0) < (long)state.Request.Generation.MaxInputTokensPerRequest * calls +
                Math.Min(construction.Limits.DraftOutputTokens, state.Request.Generation.MaxOutputTokens) +
                (long)(calls - 1) * Math.Min(construction.Limits.ReviewOutputTokens, state.Request.Generation.MaxOutputTokens))
            throw new WorkflowRuntimeException("CONSTRUCTION_REVIEW_ALLOWANCE", "The remaining cumulative token allowance cannot admit the draft and mandatory reviews at their captured request ceilings. No call was dispatched.");
        if (state.PendingCall is null && runtime is IPlanningConstructionAdmission admission)
        {
            var outputCeilings = Enumerable.Repeat(Math.Min(construction.Limits.ReviewOutputTokens, state.Request.Generation.MaxOutputTokens), calls).ToArray();
            outputCeilings[0] = Math.Min(construction.Limits.DraftOutputTokens, state.Request.Generation.MaxOutputTokens);
            var probe = PlanningGenerationPolicy.Apply(new GnOuGo.Flow.Core.Runtime.LLMRequest
            {
                Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
                Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "", MaxTokens = outputCeilings[0]
            }, state.Request.Generation);
            await admission.AdmitConstructionAsync(probe, state.Request.Generation.MaxInputTokensPerRequest, outputCeilings, ct);
        }
    }

    private static async Task GenerateConstructionAsync(PlanningSession state, IPlanningRuntime runtime, bool repair, CancellationToken ct)
    {
        var construction = state.Construction!;
        await ReserveConstructionReviewAsync(state, runtime, ct);
        var previous = construction.Candidate;
        if (repair && previous is null && state.PendingCall is null) construction.DraftCorrections++;
        if (state.PendingCall is null)
        {
            foreach (var capability in PlanningDiscoveryContext.Candidates(state).Take(4))
                if (!state.Discovery.Resolved.Any(c => c.Id == capability.Id && c.Version == capability.Version) &&
                    !state.Catalog!.Capabilities.Any(c => c.Id == capability.Id && c.Version == capability.Version))
                    await PlanningDiscoveryContext.ResolveAsync(state, runtime, capability, ct);
        }
        state.Phase = repair ? PlanningPhase.Replanning : PlanningConstruction.DraftPurpose;
        var context = PlanningConstruction.Context(state);
        if (state.PendingCall is null) construction.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        else if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The issued extension no longer matches its retained accepted checkpoint.");
        var operations = context["operations"]!.AsArray().Select(c => c!["operation"]?["id"]?.ToString() ?? c["id"]!.ToString());
        var schema = PlanningConstruction.Schema(state, operations);
        var prompt = PlanningConstruction.Instructions + (repair ? "\nCorrect only the retained tentative extension. Preserve its target scope and accepted requirements; the accepted TaskPlan is immutable.\n" : "\n") + PlanningJsonTransport.Prompt(context);
        var response = await PlanningModelCalls.CallAsync(state, runtime, repair ? "replan" : PlanningConstruction.DraftPurpose,
            prompt, schema, ct, construction.Limits.DraftOutputTokens);
        construction.LastProposalHash = "proposal:" + PlanningGraphCompiler.Fingerprint(response.ToJsonString());
        if (construction.FailedCandidates.Contains(construction.LastProposalHash, StringComparer.Ordinal))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated an invalid construction action. No budget was reset.");
        var proposal = response.Deserialize(ConstructionJsonContext.Default.PlanningConstructionResponse)!;
        if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan) || proposal.BaseHash != construction.IssuedBaseHash)
            throw new PlanningConflictException("The completed extension no longer matches its retained accepted checkpoint.");
        var actions = (proposal.Extension is null ? 0 : 1) + (proposal.DiscoveryRequests is null ? 0 : 1) +
            (proposal.Clarifications is null ? 0 : 1) + (proposal.EvidenceRequests is null ? 0 : 1) + (proposal.RollbackTask is null ? 0 : 1);
        if (actions != 1) PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return exactly one construction action.");
        if (proposal.Clarifications is { } questions)
        {
            if (proposal.Requirements is not null) PlanningConstruction.Fail("CLARIFICATION_INVALID", "/requirements", "Clarify before accepting requirements.");
            PlanningClarifications.Suspend(state, questions); state.PendingQuestions = questions; state.Status = PlanningStatus.Clarification;
            return;
        }
        if (proposal.EvidenceRequests is { } evidence)
        {
            PlanningConstruction.LoadEvidence(state, evidence);
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
        if (state.Request.Mode == PlanningMode.Auto)
        {
            foreach (var choice in next.Plan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            next.Hash = PlanningConstruction.Hash(next.Plan);
        }
        next.Corrections = previous?.Corrections ?? construction.DraftCorrections;
        if (construction.FailedCandidates.Contains(next.Hash, StringComparer.Ordinal))
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
                    var review = new PlanningSession { Plan = candidate, Catalog = state.Catalog, Requirements = state.Requirements,
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
        state.Diagnostics = draft.Diagnostics.ToList();
        state.Phase = draft.Diagnostics.Count == 0 ? PlanningConstruction.LocalPurpose : PlanningPhase.Replanning;
        if (draft.Diagnostics.Count > 0 && !state.Construction.FailedCandidates.Contains(draft.Hash, StringComparer.Ordinal))
            state.Construction.FailedCandidates.Add(draft.Hash);
        return Task.CompletedTask;
    }

    private static async Task CorrectConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!; var draft = construction.Candidate!;
        if (state.PendingCall is null && (draft.Corrections >= construction.Limits.MaxCorrectionsPerDraft || PlanningModelCalls.RemainingRepairs(state) == 0 ||
            state.Request.Options["automatic_correction"]?.ToString() == "false"))
        { state.Diagnostics = draft.Diagnostics; Stop(state); return; }
        await ReserveConstructionReviewAsync(state, runtime, ct);
        if (state.PendingCall is null) draft.Corrections++;
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
        try { request = state.PendingCall?.Request ?? PlanningConstruction.RepairRequest(state, repairState); }
        catch (WorkflowRuntimeException ex) when (state.PendingCall is null && draft.AuthorizedPaths is null && guardPaths.Length == 0 && ex.Code is "REVISION_REQUIRED" or "REPAIR_SCOPE_INVALID")
        {
            // The fresh construction contract already owns this tentative fragment;
            // a bounded replacement does not grant edits to accepted work.
            await GenerateConstructionAsync(state, runtime, repair: true, ct);
            return;
        }
        if (state.PendingCall is null && guardPaths.Length > 0) request.Prompt = PlanningPrompt.GuardCorrectionInstructions + "\n" + request.Prompt;
        PlanningRepairPatch.Verify(repairState, request);
        var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", request.Prompt, request.StructuredOutputSchema!.AsObject(), ct, construction.Limits.DraftOutputTokens);
        var correction = response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        if (correction.EvidenceRequests is { } evidence)
        {
            if (correction.Patch is not null || correction.DiscoveryRequests is not null || correction.Clarifications is not null)
                PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/evidenceRequests", "Return original evidence requests as the only repair action.");
            PlanningConstruction.LoadEvidence(state, evidence); return;
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
        if (hash == draft.Hash || construction.FailedCandidates.Contains(hash, StringComparer.Ordinal))
            throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The correction repeated a rejected candidate or made no semantic change.");
        draft.Plan = revised; draft.Hash = hash; draft.LocalReviewed = false; draft.IntermediateReviewed = false;
        PlanningConstruction.RefreshCandidate(state);
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        await ValidateConstructionCandidateAsync(state, runtime, ct);
    }

    private static async Task AcceptConstructionAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!; var draft = construction.Candidate!;
        if (!draft.LocalReviewed || draft.NeedsIntermediateReview && !draft.IntermediateReviewed || draft.Diagnostics.Count > 0)
            throw new PlanningConflictException("The candidate has not completed its required deterministic and advisory reviews.");
        PlanningConstruction.PreserveAccepted(state, draft.Plan);
        var symbols = new TaskPlanSymbols(draft.Plan);
        var checkpoint = new PlanningConstructionCheckpoint
        {
            Sequence = construction.Checkpoints.Count + 1, BaseHash = draft.BaseHash, PlanHash = draft.Hash,
            TaskIds = draft.TaskIds, ImportedTaskIds = draft.ImportedTaskIds, RequirementIds = draft.Extension.RequirementIds,
            ClosedScopes = draft.Extension.CloseScopes, Reviews = draft.Reviews, NeedsIntermediateReview = draft.NeedsIntermediateReview,
            Fragment = new JsonObject
            {
                ["scope"] = draft.Extension.Scope,
                ["tasks"] = new JsonArray(draft.TaskIds.Where(symbols.Tasks.ContainsKey)
                    .Select(id => PlanningConstruction.Node(symbols.Tasks[id].Task, PlanningJsonContext.Default.PlanTask)).ToArray()),
                ["outputs"] = PlanningConstruction.Node(draft.Extension.Outputs, PlanningJsonContext.Default.ListTaskOutput),
                ["interfaces"] = PlanningConstruction.Context(state, review: true)["interfaces"]?.DeepClone()
            }
        };
        state.Plan = draft.Plan; construction.OpenScopes = draft.OpenScopes; construction.Checkpoints.Add(checkpoint);
        foreach (var requirement in construction.Requirements.Where(r => draft.Extension.RequirementIds.Contains(r.Id, StringComparer.Ordinal)))
        { requirement.Status = "declared"; requirement.Checkpoints.Add(checkpoint.Sequence); }
        construction.Complete = draft.Extension.Complete; construction.Candidate = null;
        construction.DraftCorrections = 0; construction.LastProposalHash = null;
        construction.PendingReviewContext = null; construction.PendingReviewHash = null; construction.Evidence = null; construction.EvidenceRequests.Clear();
        state.Diagnostics.Clear(); state.Graph = null; Invalidate(state);
        state.Phase = construction.Complete ? PlanningPhase.Validation : PlanningConstruction.DraftPurpose;
        await runtime.CheckpointAsync(state, ct);
        if (construction.Complete) await CompileAsync(state, runtime, ct);
        else if (state.Request.Mode == PlanningMode.Interactive && state.Plan.Choices.Any(c => c.Selected is null)) state.Status = PlanningStatus.Clarification;
    }

    private static async Task ConstructionDiscoveryAsync(PlanningSession state, IPlanningRuntime runtime, List<PlanningDiscoveryRequest> requests, CancellationToken ct)
    {
        if (requests.Count is < 1 or > 4 || requests.Distinct().Count() != requests.Count)
            PlanningConstruction.Fail("DISCOVERY_BATCH_INVALID", "/discoveryRequests", "Request one to four distinct metadata reads.");
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
            var parent = request.Cursor is null ? null : state.Discovery.Pages.SingleOrDefault(p => p.SourceId == request.SourceId && p.NextCursor == request.Cursor);
            if (request.Cursor is not null && (parent is null || request.Query is not null || request.ProducedArtifactKind is not null))
                PlanningConstruction.Fail("CURSOR_UNKNOWN", "/discoveryRequests", "Select an issued continuation cursor without changing its query.");
            var query = request.Cursor is null ? request.Query ?? PlanningDiscoveryContext.Query(state) : parent!.Query;
            var kind = request.Cursor is null ? request.ProducedArtifactKind : parent!.ProducedArtifactKind;
            if (state.Discovery.Pages.Any(p => p.SourceId == request.SourceId && p.Cursor == request.Cursor && p.Query == query && p.ProducedArtifactKind == kind))
                PlanningConstruction.Fail("DISCOVERY_NO_PROGRESS", "/discoveryRequests", "This metadata page is retained already.");
            await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, request.SourceId, request.Cursor, ct, query, kind);
        }
        state.Phase = PlanningPhase.Discovery;
    }
}
