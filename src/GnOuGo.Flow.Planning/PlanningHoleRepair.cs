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
        var schema = PlanningExactPatches.Schema(scopes, staged.ResponseSchema);
        var fields = new JsonObject(scopes.Select(s => new KeyValuePair<string, JsonNode?>(s.Id, new JsonObject
        {
            ["path"] = s.Path, ["current"] = PlanningFieldPaths.Read(staged.Payload, s.Path)?.DeepClone()
        })));
        var relevant = staged.Targets.Where(h => scopes.Any(s => s.Path == "/assignments/" + h.Id || s.Path.StartsWith("/assignments/" + h.Id + "/", StringComparison.Ordinal))).ToArray();
        var context = PlanningHoleRequests.Create(state, state.Graph!.Workflows.Single(w => w.Key == staged.WorkflowKey), relevant);
        var prompt = "Repair the exact fields identified by target IDs. Preserve every other assignment.\n" + fields.ToJsonString() + "\n" +
            JsonSerializer.Serialize(staged.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic) + "\n" + context.Prompt;
        var before = state.Construction.ModelSequence;
        var call = PlanningModelCalls.Reserve(state, PlanningPhase.Repair, staged.WorkflowKey, PlanningModelCalls.Request(state, prompt, schema), gate, staged.ScopeFingerprint);
        if (before != state.Construction.ModelSequence) PlanningRepairAllowances.Reserved(state, staged.WorkflowKey, gate);
        await runtime.CheckpointAsync(state, ct);
        var response = await runtime.CallAsync(call.Request, call.Phase, ct);
        state.Construction.PendingCalls.Remove(call); PlanningModelCalls.RequireComplete(response, call.Request.MaxTokens);
        if (response.Json is not JsonObject patches) { PlanningContext.Stop(state, "PATCH_INVALID", "A complete exact-field response is required."); return; }
        JsonObject candidate;
        try { candidate = PlanningExactPatches.Apply(staged.Payload, patches, scopes, schema); }
        catch (InvalidOperationException error) { PlanningContext.Stop(state, "PATCH_INVALID", error.Message); return; }
        var hash = PlanningGraphCompiler.Fingerprint(candidate.ToJsonString());
        if (hash == PlanningGraphCompiler.Fingerprint(staged.Payload.ToJsonString()) || staged.RejectedCandidates.Contains(hash, StringComparer.Ordinal))
        { PlanningContext.Stop(state, "REPAIR_REPEATED", "The repair repeated a staged candidate."); return; }
        var previous = staged.Payload; staged.Payload = candidate;
        var (graph, findings) = PlanningHoleAssignments.Evaluate(state, staged);
        var oldIds = staged.Diagnostics.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, previous)).ToHashSet(StringComparer.Ordinal);
        var newIds = findings.Where(d => d.Required).Select(d => PlanningFieldPaths.DiagnosticId(d, candidate)).ToHashSet(StringComparer.Ordinal);
        var nextStage = findings.Any(d => d.Code == "HOLE_RESPONSE_INVALID") ? 0 : 1;
        if (findings.Count != 0 && !(nextStage > staged.Stage || nextStage == staged.Stage && newIds.IsProperSubsetOf(oldIds)))
        {
            staged.Payload = previous; staged.RejectedCandidates.Add(hash);
            state.Attempts.Add(new(hash, PlanningPhase.Repair, staged.Stage, false, [new("REPAIR_REGRESSION", staged.WorkflowKey, "The repair did not reduce required findings.")]));
            state.Diagnostics = staged.Diagnostics; return;
        }
        staged.Diagnostics = findings; staged.Stage = nextStage; state.Diagnostics = findings;
        state.Attempts.Add(new(hash, PlanningPhase.Repair, nextStage, true, findings));
        if (findings.Count == 0) { PlanningHoleAssignments.Commit(state, staged, graph!); PlanningWorkflowConstruction.Process(state); }
    }
}
