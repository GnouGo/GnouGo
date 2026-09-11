using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Coordinates scoped hole assignments; workers never author workflow structure.</summary>
internal sealed partial class PlanningWorkflowConstruction
{
    internal async Task AdvanceAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.Construction.Candidates.Count > 0)
        { Process(state); return; }
        try { while (PlanningContractPropagation.Resolve(state)) { ct.ThrowIfCancellationRequested(); } }
        catch (PlanningHoleUnavailableException error)
        { PlanningContext.Stop(state, "CONTRACT_PROPAGATION_CONFLICT", error.Message, error.Location); return; }
        var work = state.Construction.Workflows;
        if (work.Any(p => p.Status == "constructed")) { state.Status = PlanningStatus.Validating; return; }
        var ready = work.Where(p => p.Status == "pending" && p.Dependencies.All(d => work.Single(c => c.WorkflowKey == d).Status == "validated"))
            .OrderBy(p => p.WorkflowKey, StringComparer.Ordinal).Take(state.Request.MaxConcurrency).ToArray();
        if (ready.Length == 0)
        {
            if (work.All(p => p.Status == "validated")) { state.Status = PlanningStatus.Validating; return; }
            PlanningContext.Stop(state, "WORKFLOW_DEPENDENCY_UNRESOLVED", "Resolve the diagnosed producer before filling caller fields."); return;
        }
        var requests = new List<(PlanningWorkflowProgress Progress, PlanningModelCall Call, PlanningStagedAssignments Candidate)>();
        foreach (var progress in ready)
        {
            var pending = state.Construction.PendingCalls.SingleOrDefault(c => c.Phase == PlanningPhase.Construction && c.WorkflowKey == progress.WorkflowKey);
            if (pending is not null)
            {
                var retained = pending.Assignments ?? throw new PlanningConflictException("The pending construction request has no verifiable field scope. It cannot be redispatched.");
                if (retained.WorkflowFingerprint != PlanningHoleAssignments.WorkflowFingerprint(state.Graph!.Workflows.Single(w => w.Key == progress.WorkflowKey)) ||
                    retained.DependencyFingerprint != DependencyFingerprint(state, progress))
                    throw new PlanningConflictException("The pending construction request targets a stale workflow or callee contract.");
                requests.Add((progress, pending, retained));
                continue;
            }
            var workflow = state.Graph!.Workflows.Single(w => w.Key == progress.WorkflowKey);
            PlanningBindingResolution.Resolve(state, workflow);
            workflow = state.Graph.Workflows.Single(w => w.Key == progress.WorkflowKey);
            var holes = state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved).OrderBy(h => h.Id, StringComparer.Ordinal).ToArray();
            progress.ResolvedHoles = state.Construction.Holes.Count(h => h.WorkflowKey == workflow.Key && h.Resolved);
            progress.UnresolvedHoles = holes.Length;
            if (holes.Length == 0) { progress.Status = "constructed"; continue; }
            PlanningHoleRequests.Request request;
            try { (holes, request) = Batch(state, workflow); }
            catch (PlanningHoleUnavailableException error)
            {
                PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Typed, PlanningGraphCompiler.Fingerprint(state.Graph!),
                    [new("HOLE_DOMAIN_UNRESOLVED", error.Location, error.Message)]);
                PlanningContext.Stop(state, "HOLE_DOMAIN_UNRESOLVED", error.Message, error.Location); return;
            }
            var scope = PlanningHoleRequests.Scope(holes, request.Schema);
            progress.Gate = PlanningGates.Response;
            progress.EstimatedInputTokens = PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.Schema);
            progress.InputTokenLimit = state.Request.Generation.MaxInputTokensPerRequest;
            if (progress.ResponseRepairPending && !PlanningRepairAllowances.Available(state, workflow.Key, PlanningGates.Response)) return;
            var sequence = state.Construction.ModelSequence;
            var call = PlanningModelCalls.Reserve(state, PlanningPhase.Construction, workflow.Key,
                PlanningModelCalls.Request(state, request.Prompt, request.Schema), PlanningGates.Response, scope);
            if (state.Construction.ModelSequence != sequence)
            {
                progress.Calls++;
                if (progress.ResponseRepairPending) PlanningRepairAllowances.Reserved(state, workflow.Key, PlanningGates.Response);
            }
            PlanningConvergence.Expose(state, holes, call.Id);
            PlanningConvergence.AttributeHoles(state, call, workflow, holes);
            progress.DependencyFingerprint = DependencyFingerprint(state, progress);
            call.Assignments = new()
            {
                WorkflowKey = workflow.Key, GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph),
                WorkflowFingerprint = PlanningHoleAssignments.WorkflowFingerprint(workflow), DependencyFingerprint = progress.DependencyFingerprint,
                ScopeFingerprint = scope, Targets = holes.ToList(), ResponseSchema = request.Schema, Bindings = request.Bindings, ParameterScopes = request.ParameterScopes
            };
            requests.Add((progress, call, call.Assignments));
        }
        await runtime.CheckpointAsync(state, ct);
        var results = await Task.WhenAll(requests.Select(async item =>
        {
            try { return (Item: item, Response: await runtime.CallAsync(item.Call.Request, item.Call.Phase, ct), Error: (Exception?)null); }
            catch (Exception error) { return (Item: item, Response: (LLMResponse?)null, Error: error); }
        }));
        state.Diagnostics.Clear();
        foreach (var result in results.OrderBy(r => r.Item.Progress.WorkflowKey, StringComparer.Ordinal))
        {
            if (result.Error is not null)
            {
                if (state.RequestAccounting.SingleOrDefault(a => a.Id == result.Item.Call.Id) is { } interrupted) interrupted.Evidence = "unverifiable";
                var code = result.Error is LLMClientException provider ? "LLM_PROVIDER_" + provider.Kind.ToString().ToUpperInvariant() : "MODEL_DISPATCH_INTERRUPTED";
                var detail = result.Error is LLMClientException rejected ? " Provider status: " + rejected.StatusCode + "; code: " + rejected.SafeProviderCode + "." : "";
                var failure = new PlanningDiagnostic(code, result.Item.Progress.WorkflowKey, "The pending request has no verifiable receipt; reconcile it before continuing." + detail);
                state.Diagnostics.Add(failure); PlanningConvergence.Failure(state, result.Item.Progress.WorkflowKey, PlanningGates.Response, result.Item.Call.Id, [failure]); continue;
            }
            state.Construction.PendingCalls.Remove(result.Item.Call);
            PlanningConvergence.Receipt(state, result.Item.Call, result.Response!);
            if (result.Response!.CompletionStatus == "output_limit")
            {
                result.Item.Progress.ResponseRepairPending = true;
                var failure = new PlanningDiagnostic("MODEL_OUTPUT_LIMIT", result.Item.Progress.WorkflowKey, "The response reached its output ceiling; unresolved fields and prior assignments are retained.");
                state.Diagnostics.Add(failure); PlanningConvergence.Failure(state, result.Item.Progress.WorkflowKey, PlanningGates.Response, result.Item.Call.Id, [failure]); continue;
            }
            result.Item.Progress.ResponseRepairPending = false;
            result.Item.Candidate.Payload = result.Response.Json is JsonObject payload ? payload.DeepClone().AsObject() : new JsonObject();
            state.Construction.Candidates.Add(result.Item.Candidate);
        }
        await runtime.CheckpointAsync(state, ct);
        if (state.Diagnostics.Count > 0) { state.Status = PlanningStatus.Recovery; return; }
        Process(state);
    }
    internal static void Process(PlanningSnapshot state)
    {
        foreach (var candidate in state.Construction.Candidates.OrderBy(c => c.WorkflowKey, StringComparer.Ordinal).ToArray())
        {
            var (graph, findings) = PlanningHoleAssignments.Evaluate(state, candidate);
            if (findings.Count > 0)
            {
                candidate.Diagnostics = findings; candidate.Stage = findings.Any(d => d.Code == "HOLE_RESPONSE_INVALID") ? 0 : 1;
                PlanningConvergence.Failure(state, candidate.WorkflowKey, PlanningGates.FromStage(candidate.Stage), PlanningGraphCompiler.Fingerprint(candidate.Payload.ToJsonString()), findings);
                var progress = state.Construction.Workflows.Single(w => w.WorkflowKey == candidate.WorkflowKey);
                progress.Gate = PlanningGates.FromStage(candidate.Stage); progress.Diagnostics = findings;
                state.Diagnostics = findings; state.CurrentPhase = PlanningPhase.Repair; state.Status = PlanningStatus.Generating;
                return;
            }
            PlanningHoleAssignments.Commit(state, candidate, graph!);
        }
        state.CurrentPhase = PlanningPhase.Construction;
        state.Status = state.Construction.Workflows.Any(w => w.Status == "constructed") ? PlanningStatus.Validating : PlanningStatus.Generating;
    }

    internal static string DependencyFingerprint(PlanningSnapshot state, PlanningWorkflowProgress progress, PlanningGraph? candidate = null) => PlanningGraphCompiler.Fingerprint(
        state.ApprovedBehaviorHash + "\n" + state.Preparation!.Fingerprint + "\n" + string.Join("\n", progress.Dependencies.Order(StringComparer.Ordinal).Select(k =>
        {
            var w = (candidate ?? state.Graph!).Workflows.Single(w => w.Key == k);
            return JsonSerializer.Serialize(new PlanningWorkflow { Key = k, Inputs = w.Inputs, Outputs = w.Outputs }, PlanningJsonContext.Default.PlanningWorkflow);
        })));

    internal static PlanningPreparation RelevantPreparation(PlanningPreparation preparation, PlanningWorkflow workflow)
    {
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
        var types = nodes.Select(n => n.Type).Append("set").ToHashSet(StringComparer.Ordinal);
        return new PlanningPreparation
        {
            Fingerprint = preparation.Fingerprint,
            Capabilities = preparation.Capabilities.Where(c => nodes.Any(n => n.CapabilityId == c.Id)).ToList(),
            AllowedStepTypes = preparation.AllowedStepTypes.Where(types.Contains).ToList(),
            StepContracts = new JsonObject(preparation.StepContracts.Where(p => types.Contains(p.Key)).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))),
            Decisions = preparation.Decisions.Where(d => workflow.OperationIds.Contains(d.SourceOperationId)).ToList(),
            Interactions = preparation.Interactions.Where(d => workflow.OperationIds.Contains(d.OperationId)).ToList()
        };
    }

}
