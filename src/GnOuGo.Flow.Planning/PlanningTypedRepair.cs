using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningTypedRepair(PlanningValidationPipeline validation)
{
    internal static bool IsProgress(PlanningValidationReport baseline, PlanningValidationReport candidate, PlanningGraph? beforeGraph = null, PlanningGraph? afterGraph = null)
    {
        if (candidate.Stage < baseline.Stage) return false;
        var previousPasses = baseline.Scenarios.Where(s => s.Outcome == "passed").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!previousPasses.IsSubsetOf(candidate.Scenarios.Where(s => s.Outcome == "passed").Select(s => s.Id))) return false;
        if (candidate.Stage > baseline.Stage) return true;
        var beforeJson = beforeGraph is null ? null : PlanningFieldPaths.Json(beforeGraph);
        var afterJson = afterGraph is null ? null : PlanningFieldPaths.Json(afterGraph);
        var before = baseline.Diagnostics.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, beforeJson)).ToHashSet(StringComparer.Ordinal);
        var after = candidate.Diagnostics.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, afterJson)).ToHashSet(StringComparer.Ordinal);
        return after.IsProperSubsetOf(before);
    }

    internal async Task AdvanceAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var scope = PlanningPatches.Scope(state.Graph!, state.Diagnostics);
        if (scope.Count == 0)
        { PlanningContext.Stop(state, "REPAIR_SCOPE_UNRESOLVED", "The findings identify no safely editable typed field. Revise the governing contract."); return; }
        // Choose producers before affected callers, then constrain the response to that workflow.
        var affected = state.Construction.Workflows.Where(w => scope.Any(s => JsonNode.Parse(s)?[0]?.GetValue<string>() == w.WorkflowKey)).ToArray();
        var owner = affected.FirstOrDefault(w => !w.Dependencies.Any(d => affected.Any(p => p.WorkflowKey == d)))?.WorkflowKey;
        var gate = PlanningGates.FromStage(state.Validation.Stage);
        if (state.Construction.Repair is null && !state.Construction.PendingCalls.Any(c => c.Phase == PlanningPhase.Repair && c.WorkflowKey == (owner ?? "")) && !PlanningRepairAllowances.Available(state, owner ?? "", gate)) return;
        if (owner is not null) scope.RemoveWhere(s => JsonNode.Parse(s)?[0]?.GetValue<string>() != owner);
        var dataflow = state.Construction.Dataflow ?? throw new PlanningConflictException("Resolve dataflow obligations before executable repair.");
        var retained = state.Construction.Repair;
        var pendingScope = state.Construction.PendingCalls.SingleOrDefault(c => c.Phase == PlanningPhase.Repair && c.WorkflowKey == (owner ?? ""))?.Assignments;
        var transport = retained?.ResponseSchema is { } retainedSchema && retained.Bindings is { } retainedBindings
            ? new PlanningPatches.Request(retainedSchema, retainedBindings, new JsonObject()) { ParameterScopes = retained.ParameterScopes }
            : pendingScope is not null ? new PlanningPatches.Request(pendingScope.ResponseSchema, pendingScope.Bindings, new JsonObject()) { ParameterScopes = pendingScope.ParameterScopes }
            : PlanningPatches.CreateRequest(state.Graph!, state.Preparation!, scope, dataflow);
        var schema = transport.Schema;
        var context = retained is { Ready: false } ? retained.RequestContext.DeepClone().AsObject() : new JsonObject
        {
            ["assignments"] = transport.Context.DeepClone(),
            ["previousRejection"] = state.Attempts.LastOrDefault(a => a.Phase == PlanningPhase.Repair && !a.Retained) is { } rejected
                ? JsonSerializer.SerializeToNode(rejected.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic) : null,
            ["fields"] = new JsonObject(PlanningPatches.Targets(state.Graph!, scope).Select(t => new KeyValuePair<string, JsonNode?>(t.Id, new JsonObject
            {
                ["path"] = t.Path,
                ["current"] = PlanningModelValues.Compact(PlanningFieldPaths.Read(PlanningFieldPaths.Json(state.Graph!), t.Path))
            }))),
            ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)
        };
        if (state.Construction.Repair is null or { Ready: false })
        {
            state.Construction.Repair ??= new() { Ready = false, GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph!),
                RequestContext = context.DeepClone().AsObject(), ResponseSchema = schema.DeepClone().AsObject(), Bindings = transport.Bindings, ParameterScopes = transport.ParameterScopes };
            if (state.Construction.Repair.GraphFingerprint != PlanningGraphCompiler.Fingerprint(state.Graph!))
                throw new PlanningConflictException("The pending repair no longer targets the current graph.");
            var decisionEvidence = PlanningGraphCompiler.Fingerprint(state.ApprovedBehaviorHash + ":" + PlanningContext.Contracts(state));
            var targets = PlanningPatches.Targets(state.Graph!, scope);
            // Specializations in the issued transport are authoritative. Keep
            // parameter IDs and bindings while paging exact independent fields.
            foreach (var variant in schema["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray())
            {
                var id = variant!["properties"]!["target"]!["enum"]![0]!.ToString();
                var index = targets.FindIndex(t => t.Id == id);
                targets[index] = targets[index] with { Schema = variant["properties"]!["value"]!.DeepClone().AsObject() };
            }
            var response = await PlanningExactPatches.ResolveAsync(state, runtime, PlanningPhase.Repair, owner ?? "$plan", gate,
                targets, schema, context, decisionEvidence, ct);
            state.Construction.Repair.Patches = response;
            state.Construction.Repair.Ready = true;
            await runtime.CheckpointAsync(state, ct);
        }
        var staged = state.Construction.Repair;
        if (staged.GraphFingerprint != PlanningGraphCompiler.Fingerprint(state.Graph!))
            throw new PlanningConflictException("The staged repair no longer targets the current graph.");
        PlanningGraph candidate;
        if (staged.ResponseSchema is null || staged.Bindings is null) throw new PlanningConflictException("The staged repair has no verifiable response contract. Retain it for reconciliation.");
        try { candidate = PlanningPatches.ApplyVerified(state.Graph!, staged.Patches, scope, new(staged.ResponseSchema, staged.Bindings, new JsonObject()) { ParameterScopes = staged.ParameterScopes }); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or NullReferenceException)
        { Reject(state, owner ?? "$plan", "PATCH_INVALID", error.Message, PlanningGraphCompiler.Fingerprint(staged.Patches.ToJsonString())); return; }
        var hash = PlanningGraphCompiler.Fingerprint(candidate);
        if (hash == PlanningGraphCompiler.Fingerprint(state.Graph!) || state.Construction.RejectedCandidates.Contains(hash, StringComparer.Ordinal))
        { state.Construction.Repair = null; PlanningContext.Stop(state, "REPAIR_REPEATED", "The typed repair repeated a candidate or made no change."); return; }
        if (!PlanningRepairInvariants.PreservesUndiagnosedFields(state.Graph!, candidate, state.Diagnostics))
        { Reject(state, owner ?? "$plan", "REPAIR_SCOPE_REGRESSION", "The repair modified typed fields outside the diagnosed locations.", hash); return; }
        var regressions = PlanningRepairDomains.Validate(candidate, state.Preparation!, dataflow, PlanningPatches.Targets(state.Graph!, scope));
        PlanningRepairInvariants.PreserveBehavior(state.Graph!, candidate, state.Preparation!, state.Diagnostics, regressions);
        // Pending workflows still contain their accepted, unimplemented skeletons.
        // They must not make every repair of an independent producer look like a new regression.
        var existingBehaviorFindings = PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan!, state.Graph!, state.Preparation!);
        var existingIds = existingBehaviorFindings.Select(d => PlanningFieldPaths.DiagnosticId(d, PlanningFieldPaths.Json(state.Graph!))).ToHashSet(StringComparer.Ordinal);
        regressions.AddRange(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan!, candidate, state.Preparation!).Where(d => !existingIds.Contains(PlanningFieldPaths.DiagnosticId(d, PlanningFieldPaths.Json(candidate)))));
        if (regressions.Any(d => d.Required)) { Reject(state, owner ?? "$plan", "REPAIR_BEHAVIOR_REGRESSION", "The repair changed accepted behavior or ownership.", hash); return; }
        if (!PlanningContractPreservation.Preserves(state, candidate))
        { Reject(state, owner ?? "$plan", "REPAIR_CONTRACT_REGRESSION", "The repair weakened a previously validated contract.", hash); return; }
        var baseline = new PlanningValidationReport(state.Validation.Stage, state.Diagnostics, state.Validation.Scenarios);
        var report = await validation.EvaluateAsync(state, candidate, runtime, ct, Math.Max(1, baseline.Stage));
        PlanningConvergence.Failure(state, owner ?? "$plan", PlanningGates.FromStage(report.Stage), hash, report.Diagnostics);
        if (!IsProgress(baseline, report, state.Graph!, candidate)) { Reject(state, owner ?? "$plan", "REPAIR_REGRESSION", "The repair did not strictly improve validation while preserving established passes.", hash); return; }
        var changed = state.Construction.Workflows.Where(w => w.Status == "validated" &&
            w.DependencyFingerprint != PlanningWorkflowConstruction.DependencyFingerprint(state, w, candidate)).ToArray();
        state.Graph = candidate;
        foreach (var workflow in changed)
        {
            workflow.Status = "constructed";
            workflow.DependencyFingerprint = PlanningWorkflowConstruction.DependencyFingerprint(state, workflow);
        }
        state.Construction.Repair = null;
        state.Validation.GraphFingerprint = hash; state.Validation.Stage = report.Stage; state.Validation.Scenarios = report.Scenarios;
        state.Diagnostics = report.Diagnostics;
        state.Attempts.Add(new(hash, PlanningPhase.Repair, report.Stage, true, report.Diagnostics));
        PlanningContext.InvalidateArtifact(state);
        state.Status = PlanningStatus.Validating;
    }

    private static void Reject(PlanningSnapshot state, string owner, string code, string message, string hash)
    {
        if (code != "REPAIR_REGRESSION")
            PlanningConvergence.Failure(state, owner, code == "PATCH_INVALID" ? PlanningGates.Response : PlanningGates.Typed, hash, [new(code, "/repair", message)]);
        state.Construction.Repair = null;
        state.Validation.Assessment = new();
        if (state.Construction.RejectedCandidates.Contains(hash, StringComparer.Ordinal))
        { PlanningContext.Stop(state, "REPAIR_REPEATED", "The typed repair repeated a rejected candidate."); return; }
        state.Construction.RejectedCandidates.Add(hash);
        state.Attempts.Add(new(hash, PlanningPhase.Repair, state.Validation.Stage, false, [new(code, "/repair", message)]));
        PlanningContext.Stop(state, code, message);
    }
}
