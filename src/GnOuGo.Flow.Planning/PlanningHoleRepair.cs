using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningHoleRepair
{
    internal async Task AdvanceAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var staged = state.Construction.Candidates.OrderBy(c => c.WorkflowKey, StringComparer.Ordinal).First();
        var pending = state.Construction.PendingCalls.Any(c => c.Phase == PlanningPhase.Repair && c.WorkflowKey == staged.WorkflowKey);
        if (!pending && PlanningProducerSchemaPropagation.Stage(state, staged)) await runtime.CheckpointAsync(state, ct);
        if (!pending && PlanningProducerSchemaPropagation.Resolve(state, staged, out var propagated))
        {
            state.Events.Add(new("producer_contract_propagated", PlanningPhase.Repair, DateTimeOffset.UtcNow, 1));
            PlanningHoleAssignments.Commit(state, staged, propagated!); PlanningWorkflowConstruction.Process(state); return;
        }
        if (!pending && PlanningBindingResolution.RepairStaged(state, staged, out var bound))
        {
            state.Events.Add(new("staged_binding_resolved", PlanningPhase.Repair, DateTimeOffset.UtcNow, 1));
            PlanningHoleAssignments.Commit(state, staged, bound!); PlanningWorkflowConstruction.Process(state); return;
        }
        if (!pending)
        {
            var retained = PlanningHoleAssignments.Evaluate(state, staged);
            if (retained.Diagnostics.Count == 0)
            { PlanningHoleAssignments.Commit(state, staged, retained.Graph!); PlanningWorkflowConstruction.Process(state); return; }
            staged.Diagnostics = retained.Diagnostics;
            staged.Stage = retained.Diagnostics.Any(d => d.Code == "HOLE_RESPONSE_INVALID") ? 0 : 1;
        }
        var gate = PlanningGates.FromStage(staged.Stage);
        var scopes = PlanningExactPatches.Scope(staged.Payload, staged.ResponseSchema, staged.Diagnostics);
        if (scopes.Count == 0) { PlanningContext.Stop(state, "REPAIR_SCOPE_UNRESOLVED", "The staged findings identify no exact editable fields.", staged.WorkflowKey); return; }
        if (!pending && !PlanningRepairAllowances.Available(state, staged.WorkflowKey, gate)) return;
        var relevant = staged.Targets.Where(h => scopes.Any(s => s.Path == "/assignments/" + h.Id || s.Path.StartsWith("/assignments/" + h.Id + "/", StringComparison.Ordinal))).ToArray();
        foreach (var hole in relevant)
            {
                var bindingTarget = scopes.SingleOrDefault(t => t.Path == "/assignments/" + hole.Id + "/binding");
                if (bindingTarget is null) continue;
                var domain = PlanningHoleEligibility.Analyze(state, state.Graph!.Workflows.Single(w => w.Key == staged.WorkflowKey), hole);
                var eligible = domain.Direct.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
                var ids = staged.Bindings.Where(b => eligible.Contains(PlanningBindingIdentity.Id(b.Value))).Select(b => b.Key).ToArray();
                if (ids.Length == 0) throw new PlanningHoleUnavailableException(hole.CanonicalLocation, "Resolve the producer contract before repairing this binding; no admissible source remains.");
                scopes[scopes.IndexOf(bindingTarget)] = bindingTarget with { Schema = PlanningHoleRequests.Enum(ids) };
            }
        var pendingCall = state.Construction.PendingCalls.SingleOrDefault(c => c.Phase == PlanningPhase.Repair && c.WorkflowKey == staged.WorkflowKey);
        var schema = PlanningExactPatches.Schema(scopes, staged.ResponseSchema);
        if (schema["$defs"]?["computationText"] is JsonObject expression)
            expression["description"] = PlanningComputationScopes.ExpressionDescription;
        var fields = new JsonObject(scopes.Select(s => new KeyValuePair<string, JsonNode?>(s.Id, new JsonObject
        {
            ["path"] = s.Path, ["current"] = PlanningFieldPaths.Read(staged.Payload, s.Path)?.DeepClone()
        })));
        // Retain the staged parameter identities and response schema across restart. Current
        // eligibility is checked again before commit; recreating a request cannot alter a receipt.
        var context = PlanningHoleRepairContext.Create(state, staged, relevant);
        context["fields"] = fields;
        context["diagnostics"] = JsonSerializer.SerializeToNode(staged.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic);
        var decisionEvidence = PlanningGraphCompiler.Fingerprint(state.ApprovedBehaviorHash + ":" + PlanningContext.Contracts(state) + ":" + string.Join("|", relevant.Select(h => h.CanonicalLocation)));
        var patches = await PlanningExactPatches.ResolveAsync(state, runtime, PlanningPhase.Repair, staged.WorkflowKey, gate,
            scopes, staged.ResponseSchema, context, decisionEvidence, ct);
        JsonObject candidate;
        try { candidate = PlanningExactPatches.Apply(staged.Payload, patches, scopes, schema); }
        catch (InvalidOperationException error)
        {
            PlanningConvergence.Failure(state, staged.WorkflowKey, PlanningGates.Response, decisionEvidence, [new("PATCH_INVALID", "$", error.Message)]);
            PlanningContext.Stop(state, "PATCH_INVALID", error.Message); return;
        }
        var hash = PlanningGraphCompiler.Fingerprint(candidate.ToJsonString());
        if (hash == PlanningGraphCompiler.Fingerprint(staged.Payload.ToJsonString()) || staged.RejectedCandidates.Contains(hash, StringComparer.Ordinal))
        { PlanningContext.Stop(state, "REPAIR_REPEATED", "The repair repeated a staged candidate."); return; }
        var previous = staged.Payload; staged.Payload = candidate;
        var (graph, findings) = PlanningHoleAssignments.Evaluate(state, staged);
        PlanningConvergence.Failure(state, staged.WorkflowKey, findings.Any(d => d.Code == "HOLE_RESPONSE_INVALID") ? PlanningGates.Response : PlanningGates.Typed, hash, findings);
        var oldIds = staged.Diagnostics.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, previous)).ToHashSet(StringComparer.Ordinal);
        var newIds = findings.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, candidate)).ToHashSet(StringComparer.Ordinal);
        var nextStage = findings.Any(d => d.Code == "HOLE_RESPONSE_INVALID") ? 0 : 1;
        if (findings.Count != 0 && !(nextStage > staged.Stage || nextStage == staged.Stage && newIds.IsProperSubsetOf(oldIds)))
        {
            staged.Payload = previous; staged.RejectedCandidates.Add(hash);
            state.Attempts.Add(new(hash, PlanningPhase.Repair, staged.Stage, false, [new("REPAIR_REGRESSION", staged.WorkflowKey, "The repair did not reduce required findings.")]));
            state.Diagnostics = staged.Diagnostics; PlanningContext.Stop(state, "REPAIR_REGRESSION", "The exact correction did not make monotonic progress."); return;
        }
        staged.Diagnostics = findings; staged.Stage = nextStage; state.Diagnostics = findings;
        state.Attempts.Add(new(hash, PlanningPhase.Repair, nextStage, true, findings));
        if (findings.Count == 0) { PlanningHoleAssignments.Commit(state, staged, graph!); PlanningWorkflowConstruction.Process(state); }
    }
}
