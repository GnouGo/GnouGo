using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>One bounded planning loop: discover, propose tasks, compile, validate, revise, review.</summary>
public sealed class HybridWorkflowPlanner(TimeProvider? timeProvider = null) : IWorkflowPlanner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.SchemaVersion != 10) throw new PlanningConflictException("This planning format is no longer executable. Regenerate and approve the workflow.");
        if (session.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed. Reload its revision.");
        if (string.IsNullOrWhiteSpace(session.Request.TenantId) || string.IsNullOrWhiteSpace(session.Request.SessionId) || string.IsNullOrWhiteSpace(session.Request.Prompt))
            throw new ArgumentException("Tenant, session identity and request are required.");
        if (session.Request.MaxModelCalls is < 1 or > 1000 || session.Request.MaxReplanAttempts is < 0 or > 10)
            throw new ArgumentException("Invalid planning limits.");
        PlanningMode.Validate(session.Request.Mode);
        PlanningGenerationPolicy.Validate(session.Request.Generation);
        if (command.Kind == "advance" && (PlanningStatus.IsWaiting(session.Status) || PlanningStatus.IsTerminal(session.Status))) return session;
        if (session.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled)
            throw new PlanningConflictException("This planning session is closed.");
        if (command.Kind is "configure_generation" or "revise" && PlanningModelCalls.RemainingCalls(session) == 0)
            throw new PlanningConflictException("The session model-call budget is exhausted. Token settings and revisions cannot extend it. Start a new planning session; retained requests and accounting remain unchanged.");
        var state = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (PlanningBudgetOptions.Parse(state.Request.Options)?.MaxElapsed is { } maximum)
        {
            var remaining = maximum.TotalMilliseconds - state.ActiveMilliseconds;
            if (remaining <= 0) deadline.Cancel(); else deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        }
        var elapsed = Stopwatch.StartNew();
        if (state.WaitingSinceUtc is { } waiting)
        { state.HumanWaitMilliseconds += Math.Max(0, (_time.GetUtcNow() - waiting).TotalMilliseconds); state.WaitingSinceUtc = null; }
        try
        {
            // Recovery must not operate on identities which no longer satisfy the
            // semantic contract, even if an older request/approval was persisted.
            if (command.Kind != "cancel" && state.Plan is { } recovered && TaskPlanCompiler.IdentityDiagnostics(recovered, includeReferences: false) is { Count: > 0 } invalidIdentities)
            { state.Diagnostics = invalidIdentities.ToList(); Invalidate(state); Stop(state); }
            else switch (command.Kind)
            {
                case "advance": await AdvancePlanAsync(state, runtime, deadline.Token); break;
                case "approve":
                    if (state.Status != PlanningStatus.FinalReview || command.ArtifactHash is null || command.ArtifactHash != state.ComputeArtifactHash())
                        throw new PlanningConflictException("Approval must identify the exact current artifact.");
                    PlanningArtifactApproval.Verify(state);
                    state.Diagnostics = (await runtime.ValidateCatalogAsync(state.Catalog!, deadline.Token)).ToList();
                    if (state.Diagnostics.Any(d => d.Required)) Stop(state);
                    else { state.ApprovedHash = command.ArtifactHash; state.Status = PlanningStatus.Approved; }
                    break;
                case "choose":
                    if (state.Status != PlanningStatus.Clarification || command.Selections is null || state.Plan is null)
                        throw new PlanningConflictException("No business choice is awaiting selection.");
                    var pending = state.Plan.Choices.Where(c => c.Selected is null).ToArray();
                    if (!command.Selections.Select(p => p.Key).Order().SequenceEqual(pending.Select(c => c.Id).Order()))
                        throw new ArgumentException("Select exactly the pending choices.");
                    foreach (var choice in pending)
                    {
                        var selected = command.Selections[choice.Id]?.GetValue<string>();
                        if (!choice.Alternatives.Any(a => a.Id == selected)) throw new ArgumentException("Select a declared business alternative.");
                        choice.Selected = selected;
                    }
                    Invalidate(state); await CompileAsync(state, runtime, deadline.Token); break;
                case "configure_mode":
                    PlanningMode.Validate(command.Mode ?? ""); state.Request.Mode = command.Mode!;
                    if (state.Status == PlanningStatus.Clarification && state.Request.Mode == PlanningMode.Auto)
                        await CompileAsync(state, runtime, deadline.Token);
                    break;
                case "configure_generation":
                    if (state.PendingCall is not null || command.Generation is null) throw new PlanningConflictException("Generation settings cannot replace a pending call.");
                    PlanningGenerationPolicy.Validate(command.Generation); state.Request.Generation = command.Generation;
                    state.Diagnostics.RemoveAll(d => d.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT");
                    state.Status = PlanningStatus.Generating; break;
                case "revise":
                    if (state.PendingCall is not null) throw new PlanningConflictException("Reconcile the pending model request before revising.");
                    ArgumentException.ThrowIfNullOrWhiteSpace(command.Text);
                    state.Request.Baseline = state.Plan;
                    state.Request.Prompt += "\nRequested revision: " + command.Text;
                    state.Requirements = null; state.Plan = null; state.Graph = null; state.Diagnostics.Clear(); state.ValidationResults.Clear();
                    state.RevisionScope.Clear(); Invalidate(state); state.Status = PlanningStatus.Generating; break;
                case "cancel": state.Status = PlanningStatus.Cancelled; state.ApprovedHash = null; break;
                default: throw new ArgumentException("Unsupported planning command.");
            }
        }
        catch (PlanningConflictException) { throw; }
        catch (ArgumentException) when (command.Kind != "advance") { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { state.Diagnostics.Add(new(ErrorCodes.LlmBudgetExceeded, "/", "Active planning time was exhausted.")); Stop(state); }
        catch (PlanningResponseException ex)
        {
            state.Diagnostics = state.Plan is not null && state.RevisionScope.Count > 0
                ? state.Diagnostics.Concat(ex.Diagnostics).Distinct().ToList() : ex.Diagnostics;
            Invalidate(state); state.Status = PlanningStatus.Generating;
        }
        catch (WorkflowRuntimeException ex)
        {
            var location = ex.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT" or "DISCOVERY_INCOMPLETE" or "DISCOVERY_NOT_ALLOWED"
                ? ex.Details?["location"]?.GetValue<string>() ?? "/" : "/";
            if (!state.Diagnostics.Any(d => d.Code == ex.Code && d.Location == location))
                state.Diagnostics.Add(new(ex.Code, location, ex.Message));
            Stop(state);
        }
        catch (LLMClientException ex)
        {
            // Never surface exception messages or provider bodies. The integration supplies
            // a redacted classification; the pending reservation remains durable evidence.
            var detail = $"{ex.Kind}; retryable: {ex.Retryable.ToString().ToLowerInvariant()}";
            if (ex.StatusCode is { } status) detail += $"; HTTP {status}";
            if (ex.SafeProviderCode is { } code) detail += $"; provider code: {code}";
            state.Diagnostics.Add(new(ex.IsRequestRejected ? ErrorCodes.ModelRequestRejected : "MODEL_DISPATCH_UNVERIFIABLE", "/",
                ex.IsRequestRejected
                    ? $"The model provider rejected the request ({detail}). Correct the request or provider configuration, then start a new planning session."
                    : $"The model request has no retained completion ({detail}). Usage remains unconfirmed."));
            Stop(state);
        }
        catch (JsonException)
        { state.Diagnostics = [new("PLANNING_RESPONSE_INVALID", "/", "The response does not satisfy the semantic TaskPlan proposal contract.")]; Invalidate(state); state.Status = PlanningStatus.Generating; }
        catch (Exception)
        { state.Diagnostics.Add(new(state.PendingCall is null ? "PLANNING_HOST_FAILURE" : "MODEL_DISPATCH_UNVERIFIABLE", "/", "Planning could not safely complete this operation.")); Stop(state); }
        state.ActiveMilliseconds += elapsed.Elapsed.TotalMilliseconds;
        state.Revision++; state.UpdatedAtUtc = _time.GetUtcNow();
        if (PlanningStatus.IsWaiting(state.Status)) state.WaitingSinceUtc = state.UpdatedAtUtc;
        await runtime.CheckpointAsync(state, ct);
        return state;
    }

    private static async Task AdvancePlanAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        state.Status = PlanningStatus.Generating;
        if (state.PendingCall is null) PlanningModelCalls.EnsureCallAvailable(state);
        if (state.Catalog is null)
        {
            state.Catalog = await runtime.DiscoverAsync(state.Request, ct);
            state.Discovery.Sources = (await runtime.Capabilities.ListSourcesAsync(ct)).ToList();
            if (state.Discovery.Sources.Count == 1)
                await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, state.Discovery.Sources[0].Id, null, ct, state.PendingCall is null ? PlanningDiscoveryContext.Query(state) : null);
        }
        if (state.PendingCall is null && state.Plan is { } retained && state.Diagnostics.Any(d => d.Code == "TASK_ARTIFACT_BINDING"))
        {
            // Reconcile pending receipts first. Fresh requests derive structural authority
            // from the retained baseline and exact available producer contracts.
            var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(retained, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
            foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            var missing = new TaskPlanCompiler().Compile(probe, state.Catalog).Diagnostics.Where(d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING").ToArray();
            if (missing.Length > 0)
            {
                SemanticFailure(state, state.Diagnostics.Where(d => !missing.Any(m => m.Location == d.Location && d.Code is "TASK_ARTIFACT_BINDING" or "TASK_ARTIFACT_PREREQUISITE_MISSING"))
                    .Concat(missing).Distinct().ToArray());
                if (state.Status == PlanningStatus.Stopped) return;
            }
        }
        var repair = PlanningModelCalls.IsRepair(state);
        if (repair && state.PendingCall is null && state.Plan is { } baseline && state.RevisionScope.Count > 0 &&
            state.Diagnostics.Any(d => d.Code == "TASK_INPUT_TYPE"))
        {
            // Re-derive precise producer constraint locations from the immutable
            // saved baseline. Pending requests always retain their issued scope.
            var candidate = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
            foreach (var choice in candidate.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            var baselineFindings = new TaskPlanCompiler().Compile(candidate, state.Catalog!).Diagnostics;
            state.Diagnostics = state.Diagnostics.Concat(baselineFindings.Where(d => d.Code == "TASK_TRANSFORM_CONSTRAINT")).Distinct().ToList();
            state.RevisionScope = TaskPlanRevisions.Scope(baseline, state.Diagnostics).ToList();
        }
        if (repair && state.PendingCall is null && state.ReplanAttempts >= state.Request.MaxReplanAttempts) { Stop(state); return; }
        state.Phase = repair ? PlanningPhase.Replanning : state.Requirements is null ? PlanningPhase.Requirements : PlanningPhase.Tasks;
        var recovering = state.PendingCall is not null;
        var legacyDiscovery = state.PendingCall is { } issued && !PlanningSchemas.HasQueryProperty(issued.Request.StructuredOutputSchema?["properties"]?["discoveryRequests"]);
        if (state.PendingCall is null)
        {
            await PlanningDiscoveryContext.DiscoverPrerequisitesAsync(state, runtime, ct);
            await PlanningDiscoveryContext.ResolveShortlistAsync(state, runtime, ct);
        }
        var issuedRequest = state.PendingCall?.Request ?? new PlanningPrompt(state).Request();
        var patchRequest = PlanningRepairPatch.Issued(issuedRequest.StructuredOutputSchema?.AsObject());
        if (patchRequest) PlanningRepairPatch.Verify(state, issuedRequest);
        var response = await PlanningModelCalls.CallAsync(state, runtime, state.PendingCall?.Purpose ?? (repair ? "replan" : "tasks"), issuedRequest.Prompt, issuedRequest.StructuredOutputSchema!.AsObject(), ct);
        PlanningProposal proposal;
        if (patchRequest)
        {
            var change = response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
            if ((change.DiscoveryRequests is null) == (change.Patch is null)) Reject("PROPOSAL_ACTION_INVALID", "/", "Return one permitted discovery batch or a semantic patch.");
            proposal = new() { DiscoveryRequests = change.DiscoveryRequests,
                Plan = change.Patch is null ? null : PlanningRepairPatch.Apply(state, change.Patch, issuedRequest) };
        }
        else proposal = JsonSerializer.Deserialize(response, PlanningJsonContext.Default.PlanningProposal)!;
        ValidateRequirements(state, proposal);
        if ((proposal.DiscoveryRequests is null) == (proposal.Plan is null)) Reject("PROPOSAL_ACTION_INVALID", "/", "Return one discovery batch or a complete TaskPlan.");
        state.Requirements ??= proposal.Requirements;
        if (proposal.DiscoveryRequests is { } requests)
        {
            if (requests.Count is < 1 or > 4 || requests.Distinct().Count() != requests.Count)
                Reject("DISCOVERY_BATCH_INVALID", "/discoveryRequests", "Request one to four distinct issued source pages.");
            // Admit the complete batch before reading any source. Fetches are metadata
            // reads; recovery can repeat them using the same recorded model response.
            var effective = new List<PlanningDiscoveryRequest>();
            var inspections = new List<PlanningDiscoveryRequest>();
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i]; var path = "/discoveryRequests/" + i;
                if (!state.Discovery.Sources.Any(s => s.Id == request.SourceId)) Reject("SOURCE_UNKNOWN", path + "/sourceId", "Choose an issued capability source.");
                if (request.OperationIds is not null)
                {
                    if (inspections.Any(r => r.SourceId == request.SourceId))
                        Reject("DISCOVERY_BATCH_INVALID", path, "Replace a source's inspection selection at most once per batch.");
                    PlanningDiscoveryContext.Inspection(state, request, path);
                    inspections.Add(request);
                    continue;
                }
                var parent = request.Cursor is null ? null : state.Discovery.Pages.SingleOrDefault(p => p.SourceId == request.SourceId && p.NextCursor == request.Cursor);
                if (request.Cursor is not null && (parent is null || request.Query is not null || request.ProducedArtifactKind is not null))
                    Reject("CURSOR_UNKNOWN", path + "/cursor", "Use an issued continuation cursor with query null, or start a refined query without a cursor.");
                if (request.Query is { } text && (text.Length > 512 || string.IsNullOrWhiteSpace(Capabilities.CapabilityRelevance.Query(text))))
                    Reject("DISCOVERY_QUERY_INVALID", path + "/query", "Use nonblank query text of at most 512 characters.");
                var query = legacyDiscovery ? null : parent is not null ? parent.Query : (request.Query is null ? PlanningDiscoveryContext.Query(state) : Capabilities.CapabilityRelevance.Query(request.Query));
                var filter = parent?.ProducedArtifactKind ?? request.ProducedArtifactKind;
                if (filter is not null && (!state.Discovery.Pages.Any(p => p.SourceId == request.SourceId) ||
                    !PlanningDiscoveryContext.Prerequisites(state).Contains(filter, StringComparer.Ordinal)))
                    Reject("DISCOVERY_FILTER_INVALID", path + "/producedArtifactKind", "Choose an issued exact artifact kind, not an inferred prerequisite.");
                var next = request with { Query = query, ProducedArtifactKind = filter };
                if (effective.Contains(next) || state.Discovery.Pages.Any(p => p.SourceId == next.SourceId && p.Cursor == next.Cursor && p.Query == next.Query && p.ProducedArtifactKind == next.ProducedArtifactKind))
                    Reject("DISCOVERY_NO_PROGRESS", path, "This query page is cached. Use its operations, refine the query or request an issued continuation cursor.");
                effective.Add(next);
            }
            foreach (var request in inspections)
            {
                state.Discovery.Inspections ??= [];
                state.Discovery.Inspections.RemoveAll(r => r.SourceId == request.SourceId);
                if (request.OperationIds!.Count > 0) state.Discovery.Inspections.Add(request);
            }
            foreach (var request in effective) await PlanningDiscoveryContext.DiscoverPageAsync(state, runtime, request.SourceId, request.Cursor, ct, request.Query, request.ProducedArtifactKind);
            if (!legacyDiscovery && effective.Count > 0)
                state.Discovery.PresentationQuery = Capabilities.CapabilityRelevance.Query(string.Join(' ', effective.Select(r => r.Query).OfType<string>()));
            // Resolve while this adapter still owns the complete source listing.
            // Designer recreates adapters between advances; durable receipts avoid rereading it.
            // Historical pending responses retain their original discovery side effects.
            // A recovered request retains its receipt effects. New presentation and
            // optional contract selection apply before the next newly issued request.
            if (!legacyDiscovery && !recovering) await PlanningDiscoveryContext.ResolveShortlistAsync(state, runtime, ct);
            // A successful replacement batch resolves response errors, not semantic
            // findings or source availability. Historical checkpoints keep the failure.
            state.Diagnostics.RemoveAll(d => (d.Code is "DISCOVERY_BATCH_INVALID" or "SOURCE_UNKNOWN" or "CURSOR_UNKNOWN" or
                "DISCOVERY_QUERY_INVALID" or "DISCOVERY_FILTER_INVALID" or "DISCOVERY_NO_PROGRESS" or "DISCOVERY_SELECTION_INVALID" or "PLANNING_RESPONSE_INVALID") &&
                (d.Location == "/discoveryRequests" || d.Location.StartsWith("/discoveryRequests/", StringComparison.Ordinal)));
            state.Phase = PlanningPhase.Discovery; return;
        }
        var plan = proposal.Plan!;
        var identities = TaskPlanCompiler.IdentityDiagnostics(plan, includeReferences: false);
        if (identities.Count > 0)
        {
            if (state.Plan is not null) throw new PlanningResponseException(identities.ToList());
            state.Diagnostics = identities.ToList(); Stop(state); return;
        }
        // Selections are host-owned. Model repairs cannot silently change a user's decision.
        foreach (var choice in patchRequest ? [] : plan.Choices)
        {
            if (choice.Selected is not null) Reject("CHOICE_SELECTION_FORBIDDEN", "/choices/" + choice.Id, "Only the host selects alternatives.");
            if (state.Plan?.Choices.SingleOrDefault(c => c.Id == choice.Id) is { Selected: not null } previous)
            {
                var before = JsonSerializer.SerializeToNode(previous, PlanningJsonContext.Default.PlanningChoice)!.AsObject(); before["selected"] = null;
                if (JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(choice, PlanningJsonContext.Default.PlanningChoice))) choice.Selected = previous.Selected;
            }
        }
        var findings = patchRequest ? [] : TaskPlanRevisions.Validate(state.Plan, plan, state.RevisionScope, state.Catalog).ToList();
        if (findings.Count > 0) throw new PlanningResponseException(findings);
        state.Plan = plan; state.Graph = null; Invalidate(state);
        foreach (var operation in TaskPlanRevisions.Tasks(plan).Where(t => t.Kind == "operation").Select(t => t.Operation).Distinct(StringComparer.Ordinal))
        {
            if (state.Catalog.Capabilities.Any(c => TaskOperations.Describe(c).Id == operation)) continue;
            if (state.Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.Operation?.Id == operation)
                .Select(c => (c.Id, c.Version)).Distinct().Skip(1).Any())
            {
                state.Diagnostics = [new("SELECTED_OPERATION_UNAVAILABLE", "/tasks/" + TaskPlanRevisions.Tasks(plan).First(t => t.Operation == operation).Id,
                    "Discovery contains conflicting versions of the selected operation. Rediscover and review its exact contract.")];
                Stop(state); return;
            }
            if (state.Discovery.Resolved.SingleOrDefault(c => TaskOperations.Describe(c).Id == operation) is { } cached)
            { state.Catalog.Capabilities.Add(cached); continue; }
            var summaries = state.Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.Operation?.Id == operation).DistinctBy(c => (c.Id, c.Version)).ToArray();
            if (summaries.Length != 1 || state.Catalog.Policy.DeniedCapabilityIds.Contains(summaries[0].Id) || !state.Catalog.AllowedStepTypes.Contains(summaries[0].StepType))
                continue; // Compiler preflight reports every invalid selection and independent semantic error together.
            try
            {
                await PlanningDiscoveryContext.ResolveAsync(state, runtime, summaries[0], ct);
                state.Catalog.Capabilities.Add(state.Discovery.Resolved.Single(c => c.Id == summaries[0].Id && c.Version == summaries[0].Version));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                state.Diagnostics = [new("SELECTED_OPERATION_UNAVAILABLE", "/tasks/" + TaskPlanRevisions.Tasks(plan).First(t => t.Operation == operation).Id, "The selected operation could not be verified. Rediscover and review its exact contract.")];
                Stop(state); return;
            }
        }
        await CompileAsync(state, runtime, ct);
    }

    private static async Task CompileAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var plan = state.Plan!;
        var selectedOperations = TaskPlanRevisions.Tasks(plan).Where(t => t.Kind == "operation").Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in state.Catalog!.Capabilities.Where(c => c.Kind != "registered" && !selectedOperations.Contains(TaskOperations.Describe(c).Id)))
            if (!state.Discovery.Resolved.Any(c => c.Id == capability.Id && c.Version == capability.Version)) state.Discovery.Resolved.Add(capability);
        // Only selected contracts authorize execution. Discovery receipts retain resolved contracts across repairs.
        state.Catalog!.Capabilities.RemoveAll(c => !selectedOperations.Contains(TaskOperations.Describe(c).Id) && c.Kind != "registered");
        // Validate the recommendation by compiling it before either automatic selection or a human pause.
        var candidate = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        foreach (var choice in candidate.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var compilation = new TaskPlanCompiler().Compile(candidate, state.Catalog!);
        if (compilation.Diagnostics.Count > 0) { SemanticFailure(state, compilation.Diagnostics); return; }
        if (plan.Choices.Any(c => c.Selected is null))
        {
            if (state.Request.Mode == PlanningMode.Interactive) { state.Diagnostics.Clear(); state.Status = PlanningStatus.Clarification; return; }
            foreach (var choice in plan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        }
        var graph = compilation.Graph!;
        var findings = PlanningGeneratedGraph.Validate(graph, state.Catalog!).Select(compilation.Locate).ToList();
        if (findings.Count > 0)
        {
            state.Diagnostics = findings.Select(d => d with { Code = "TASK_COMPILER_VALIDATION", Message = d.Code + ": " + d.Message }).ToList();
            Stop(state); return;
        }
        PlanningConfirmationGuards.Apply(graph, state.Catalog!);
        findings = PlanningExecutableValidation.Validate(graph, state.Catalog!).Select(compilation.Locate).ToList();
        if (findings.Count == 0)
        {
            var yaml = new PlanningGraphCompiler().Compile(graph, state.Catalog!, state.Request.Name);
            findings.AddRange((await runtime.ValidateAsync(new(yaml, state.Request, state.Catalog!, PlanningGraphCompiler.CapabilityBindings(graph)), ct))
                .Select(d => compilation.Locate(PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph))));
            if (findings.Count == 0)
            {
                state.Graph = graph; state.Yaml = yaml; state.ApprovedHash = null;
                var unseen = state.Discovery.Sources.Count(s => state.Discovery.Pages.All(p => p.SourceId != s.Id));
                if (unseen > 0) state.Discovery.Limitations.Add($"Discovery is incomplete: {unseen} sources were not inspected.");
                if (state.Discovery.Pages.Any(p => p.NextCursor is { } next && !state.Discovery.Pages.Any(seen => seen.SourceId == p.SourceId && seen.Cursor == next && seen.Query == p.Query && seen.ProducedArtifactKind == p.ProducedArtifactKind)))
                    state.Discovery.Limitations.Add("Additional operation pages remain uninspected.");
                state.Discovery.Limitations = state.Discovery.Limitations.Distinct(StringComparer.Ordinal).ToList();
                state.Diagnostics.Clear(); state.ValidationResults = [new("static", "passed", "Business bindings, contracts and control flow validated. External execution has not been observed.", [])];
                state.Status = PlanningStatus.FinalReview; state.Phase = PlanningPhase.Review; return;
            }
        }
        // A valid semantic contract must compile to valid plumbing. Never ask the model to repair compiler output.
        state.Diagnostics = findings.Select(d => d with { Code = "TASK_COMPILER_VALIDATION", Message = d.Code + ": " + d.Message }).ToList();
        Stop(state);
    }

    private static void SemanticFailure(PlanningSession state, IReadOnlyList<PlanningDiagnostic> findings)
    {
        state.Diagnostics = findings.ToList(); state.Graph = null;
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan!, findings).ToList();
        Invalidate(state);
        var structural = PlanningStructuralRepair.Slots(state, PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject());
        state.RevisionScope.RemoveAll(p => findings.Any(d => d.Code == "TASK_KIND_INVALID" && d.Location == p) &&
            !structural.Any(s => s.Kind == "task" && p == s.Location + "/kind"));
        var unsupported = findings.Where(d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING").Any(d =>
            !structural.Any(s => s.Kind == "prerequisites" && s.Location.Replace("/prerequisites/", "/inputs/", StringComparison.Ordinal) == d.Location));
        if (unsupported) state.RevisionScope.RemoveAll(p => findings.Any(d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING" && d.Location == p));
        if (state.RevisionScope.Count == 0 || unsupported || findings.Any(d => d.Code == "TASK_COMPILER_VALIDATION")) Stop(state);
        else state.Status = PlanningStatus.Generating;
    }

    private static void ValidateRequirements(PlanningSession state, PlanningProposal proposal)
    {
        var requirements = proposal.Requirements;
        if (requirements is null && state.Requirements is not null) return;
        if (requirements is null || string.IsNullOrWhiteSpace(requirements.Summary) || requirements.Outcomes.Count == 0 ||
            requirements.Outcomes.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Description)) ||
            requirements.Outcomes.Select(o => o.Id).Distinct().Count() != requirements.Outcomes.Count)
            Reject("REQUIREMENTS_INVALID", "/requirements", "Declare distinct concrete business outcomes.");
        if (state.Requirements is { } accepted && (accepted.Summary != requirements!.Summary || !accepted.Outcomes.Select(o => (o.Id, o.Description)).Order()
                .SequenceEqual(requirements.Outcomes.Select(o => (o.Id, o.Description)).Order())))
            Reject("REQUIREMENTS_CHANGED", "/requirements", "Preserve accepted requirements. Only an explicit user revision may change their scope.");
    }

    internal static string Prompt(PlanningSession state) => state.PendingCall?.Request.Prompt ?? new PlanningPrompt(state).Request().Prompt;
    internal static List<CapabilitySummary> Shortlist(PlanningSession state, bool resolvedOnly = false) => new PlanningPrompt(state).Shortlist(resolvedOnly);
    internal static string BuildPrompt(PlanningSession state, IReadOnlyList<CapabilitySummary> optional) => new PlanningPrompt(state).Build(optional);

    private static void Reject(string code, string location, string message) => throw new PlanningResponseException([new(code, location, message)]);
    private static void Invalidate(PlanningSession state) { state.Yaml = null; state.ApprovedHash = null; }
    private static void Stop(PlanningSession state) { state.Status = PlanningStatus.Stopped; Invalidate(state); }
}
